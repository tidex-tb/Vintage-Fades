using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Messaging;
using VintageFades.Domain;

namespace VintageFades.Application.Inbound;

/// <summary>Stores verified webhook deliveries so the HTTP response can return 200 immediately and processing
/// can be retried safely. Identical deliveries (Meta retries) are recognised by payload hash.</summary>
public sealed class WebhookIngestionService(IAppDbContext db, IWorkSignal signal, TimeProvider time)
{
    public async Task<bool> StoreAsync(Platform platform, string payload, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        if (await db.WebhookEvents.AnyAsync(e => e.Platform == platform && e.PayloadHash == hash, ct)) return false;

        var now = time.GetUtcNow().UtcDateTime;
        var webhookEvent = new WebhookEvent
        {
            Platform = platform,
            PayloadHash = hash,
            Payload = payload,
            ReceivedAtUtc = now,
            NextAttemptAtUtc = now,
            ProcessingStatus = ProcessingStatus.Pending
        };
        db.WebhookEvents.Add(webhookEvent);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            db.Discard(webhookEvent);
            return false;
        }
        signal.Notify(WorkQueue.Webhooks);
        return true;
    }
}

public sealed class WebhookProcessor(
    IAppDbContext db,
    IEnumerable<IWebhookPayloadParser> parsers,
    InboundMessageHandler handler,
    NotificationService notifications,
    IWorkSignal signal,
    TimeProvider time,
    ILogger<WebhookProcessor> logger)
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);

    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ids = await db.WebhookEvents
            .Where(e => e.ProcessingStatus == ProcessingStatus.Pending && e.NextAttemptAtUtc <= now
                        && (e.LockedUntilUtc == null || e.LockedUntilUtc < now))
            .OrderBy(e => e.Id).Select(e => e.Id).Take(20).ToListAsync(ct);

        var processed = 0;
        foreach (var id in ids)
        {
            // Claim the row so a second instance (e.g. during a rolling deploy) cannot process it concurrently.
            var lockUntil = now + LockDuration;
            var claimed = await db.WebhookEvents
                .Where(e => e.Id == id && e.ProcessingStatus == ProcessingStatus.Pending && (e.LockedUntilUtc == null || e.LockedUntilUtc < now))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntilUtc, lockUntil), ct);
            if (claimed == 0) continue;

            await ProcessOneAsync(id, ct);
            db.ChangeTracker.Clear();
            processed++;
        }
        if (processed > 0) signal.Notify(WorkQueue.Outbox);
        return processed;
    }

    private async Task ProcessOneAsync(long id, CancellationToken ct)
    {
        var webhookEvent = await db.WebhookEvents.AsNoTracking().FirstAsync(e => e.Id == id, ct);
        var parser = parsers.FirstOrDefault(p => p.Platform == webhookEvent.Platform);
        try
        {
            if (parser is null) throw new InvalidOperationException($"No parser registered for {webhookEvent.Platform}.");
            var result = parser.Parse(webhookEvent.Payload ?? "{}");
            foreach (var message in result.Messages)
            {
                await handler.HandleAsync(message, ct);
                db.ChangeTracker.Clear();
            }
            foreach (var reply in result.BusinessReplies)
            {
                await handler.HandleBusinessAppReplyAsync(reply, ct);
                db.ChangeTracker.Clear();
            }
            foreach (var status in result.Statuses)
                await ApplyDeliveryStatusAsync(status, ct);

            var done = time.GetUtcNow().UtcDateTime;
            var handled = result.Messages.Count + result.Statuses.Count + result.BusinessReplies.Count;
            await db.WebhookEvents.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.ProcessingStatus, handled == 0 ? ProcessingStatus.Ignored : ProcessingStatus.Processed)
                .SetProperty(e => e.ExternalEventId, result.FirstEventId)
                .SetProperty(e => e.ProcessedAtUtc, done)
                .SetProperty(e => e.Payload, (string?)null)
                .SetProperty(e => e.ErrorMessage, (string?)null)
                .SetProperty(e => e.LockedUntilUtc, (DateTime?)null), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var permanent = ex is JsonException;
            var attempts = webhookEvent.RetryCount + 1;
            var failed = permanent || attempts >= MaxAttempts;
            var next = time.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(15 * Math.Pow(2, attempts - 1));
            var error = Truncate($"{ex.GetType().Name}: {ex.Message}");
            logger.LogError(ex, "Webhook event {EventId} ({Platform}) failed on attempt {Attempt}", id, webhookEvent.Platform, attempts);

            await db.WebhookEvents.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.RetryCount, attempts)
                .SetProperty(e => e.ProcessingStatus, failed ? ProcessingStatus.Failed : ProcessingStatus.Pending)
                .SetProperty(e => e.NextAttemptAtUtc, next)
                .SetProperty(e => e.ErrorMessage, error)
                .SetProperty(e => e.LockedUntilUtc, (DateTime?)null), CancellationToken.None);

            if (failed)
            {
                var now = time.GetUtcNow().UtcDateTime;
                await notifications.EnqueueOwnerNotificationAsync(NotificationTypes.SystemAlert,
                    "SYSTEM ALERT – VINTAGE FADES\n" +
                    $"A {webhookEvent.Platform.DisplayName()} message could not be processed (event #{id}). " +
                    "A customer may be waiting for a reply. Please check the conversation and the service logs.",
                    $"alert:webhook-failed:{webhookEvent.Platform}:{now:yyyyMMddHH}", CancellationToken.None);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    private Task ApplyDeliveryStatusAsync(DeliveryStatusUpdate status, CancellationToken ct) =>
        db.NotificationLogs.Where(n => n.ProviderMessageId == status.ProviderMessageId && n.Platform == status.Platform)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.DeliveryStatus, status.Status)
                .SetProperty(n => n.ErrorMessage, n => status.Error ?? n.ErrorMessage), ct);

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
