using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Application.Messaging;

/// <summary>
/// Delivers outbox messages through the platform adapters with exponential backoff. Booking data is never
/// touched here: a failed confirmation is retried, it never causes a second booking.
/// </summary>
public sealed class OutboxDispatcher(
    IAppDbContext db,
    IEnumerable<IMessagingChannel> channels,
    NotificationService notifications,
    TimeProvider time,
    IOptions<NotificationOptions> notificationOptions,
    IOptions<OutboxOptions> outboxOptions,
    ILogger<OutboxDispatcher> logger)
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);
    /// <summary>Kept slightly under 24 hours so a message is never sent just as the window closes.</summary>
    private static readonly TimeSpan CustomerServiceWindow = TimeSpan.FromHours(23.5);
    private readonly OutboxOptions _options = outboxOptions.Value;

    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ids = await db.OutboxMessages
            .Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptAtUtc <= now && (o.LockedUntilUtc == null || o.LockedUntilUtc < now))
            .OrderBy(o => o.Id).Select(o => o.Id).Take(50).ToListAsync(ct);

        var count = 0;
        foreach (var id in ids)
        {
            var lockUntil = now + LockDuration;
            var claimed = await db.OutboxMessages
                .Where(o => o.Id == id && o.Status == OutboxStatus.Pending && (o.LockedUntilUtc == null || o.LockedUntilUtc < now))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LockedUntilUtc, lockUntil), ct);
            if (claimed == 0) continue;

            var message = await db.OutboxMessages.FirstAsync(o => o.Id == id, ct);
            try
            {
                await DispatchOneAsync(message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Unexpected error dispatching outbox message {OutboxId}", id);
                db.ChangeTracker.Clear();
                message = await db.OutboxMessages.FirstAsync(o => o.Id == id, CancellationToken.None);
                ScheduleRetry(message, $"{ex.GetType().Name}: {ex.Message}", time.GetUtcNow().UtcDateTime);
            }
            message.LockedUntilUtc = null;
            await db.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            count++;
        }
        return count;
    }

    private async Task DispatchOneAsync(OutboxMessage message, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var envelope = OutboxEnvelope.Deserialize(message.Payload);
        var isOwner = message.EventType is OutboxEventTypes.OwnerNotification or OutboxEventTypes.OwnerCommandReply;
        var recipient = isOwner ? notificationOptions.Value.OwnerRecipient : envelope.Recipient;

        if (recipient is null)
        {
            await Complete(message, envelope, "(owner)", OutboxStatus.Failed, null,
                "Owner notification number is not configured (WhatsApp__OwnerNotificationNumber).", now);
            return;
        }

        if (message.EventType == OutboxEventTypes.AppointmentReminder)
        {
            var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == envelope.BookingId, ct);
            if (booking is null || booking.Status != BookingStatus.Confirmed || booking.StartAtUtc != envelope.ExpectedStartAtUtc || booking.StartAtUtc <= now)
            {
                await Complete(message, envelope, recipient, OutboxStatus.Skipped, null, "Booking was cancelled, moved or has already started.", now);
                return;
            }
        }

        var channel = channels.FirstOrDefault(c => c.Platform == envelope.Platform);
        if (channel is null || !channel.IsConfigured)
        {
            await Complete(message, envelope, recipient, OutboxStatus.Failed, null, $"{envelope.Platform.DisplayName()} integration is not configured.", now);
            return;
        }

        SendResult result;
        if (envelope.Template is not null && isOwner)
        {
            result = await channel.SendTemplateAsync(recipient, envelope.Template, ct);
        }
        else if (!envelope.RequiresOpenWindow || await IsWindowOpenAsync(envelope.ConversationId, now, ct))
        {
            result = await channel.SendAsync(recipient, new OutboundMessage(envelope.Text, envelope.Options), ct);
            if (result.Failure == SendFailureKind.OutsideMessagingWindow && envelope.Template is not null)
                result = await channel.SendTemplateAsync(recipient, envelope.Template, ct);
        }
        else if (envelope.Template is not null)
        {
            result = await channel.SendTemplateAsync(recipient, envelope.Template, ct);
        }
        else
        {
            await Complete(message, envelope, recipient, OutboxStatus.Skipped, null,
                "Outside the 24-hour messaging window and no approved template is configured for this platform.", now);
            return;
        }

        if (result.Success)
        {
            await Complete(message, envelope, recipient, OutboxStatus.Sent, result.ProviderMessageId, null, now);
            return;
        }

        if (result.Failure == SendFailureKind.Transient)
        {
            ScheduleRetry(message, result.Error ?? "Transient failure", now);
            if (message.Status == OutboxStatus.Failed) await AlertAsync(envelope, message, $"delivery kept failing: {result.Error}", now, ct);
            return;
        }

        await Complete(message, envelope, recipient, OutboxStatus.Failed, null, result.Error ?? result.Failure.ToString(), now);
        if (result.Failure == SendFailureKind.AuthenticationFailed || isOwner)
            await AlertAsync(envelope, message, $"{result.Failure}: {result.Error}", now, ct);
    }

    private async Task<bool> IsWindowOpenAsync(long? conversationId, DateTime now, CancellationToken ct)
    {
        if (conversationId is null) return false;
        var lastInbound = await db.Conversations.Where(c => c.Id == conversationId).Select(c => c.LastInboundAtUtc).FirstOrDefaultAsync(ct);
        return lastInbound is not null && now - lastInbound < CustomerServiceWindow;
    }

    private void ScheduleRetry(OutboxMessage message, string error, DateTime now)
    {
        message.RetryCount++;
        message.LastError = Truncate(error);
        if (message.RetryCount >= _options.MaxAttempts)
        {
            message.Status = OutboxStatus.Failed;
            message.ProcessedAtUtc = now;
            logger.LogError("Outbox message {OutboxId} failed permanently after {Attempts} attempts: {Error}", message.Id, message.RetryCount, error);
            return;
        }
        var delay = TimeSpan.FromSeconds(_options.BaseRetryDelaySeconds * Math.Pow(2, message.RetryCount - 1));
        var cap = TimeSpan.FromMinutes(_options.MaxRetryDelayMinutes);
        message.NextAttemptAtUtc = now + (delay < cap ? delay : cap);
        logger.LogWarning("Outbox message {OutboxId} attempt {Attempt} failed, retrying at {NextAttempt}: {Error}",
            message.Id, message.RetryCount, message.NextAttemptAtUtc, error);
    }

    private async Task Complete(OutboxMessage message, OutboxEnvelope envelope, string recipient, OutboxStatus status, string? providerId, string? error, DateTime now)
    {
        message.Status = status;
        message.ProcessedAtUtc = now;
        message.LastError = error is null ? null : Truncate(error);
        if (status != OutboxStatus.Sent)
            logger.LogWarning("Outbox message {OutboxId} ({Type}) {Status}: {Error}", message.Id, envelope.NotificationType, status, error);

        db.NotificationLogs.Add(new NotificationLog
        {
            OutboxMessageId = message.Id,
            NotificationType = envelope.NotificationType,
            Recipient = recipient,
            Platform = envelope.Platform,
            MessageContent = envelope.Text.Length <= 4096 ? envelope.Text : envelope.Text[..4096],
            Status = status switch
            {
                OutboxStatus.Sent => NotificationStatus.Sent,
                OutboxStatus.Skipped => NotificationStatus.Skipped,
                _ => NotificationStatus.Failed
            },
            ProviderMessageId = providerId,
            SentAtUtc = now,
            ErrorMessage = error is null ? null : Truncate(error)
        });

        if (envelope.ConversationMessageId is long conversationMessageId &&
            await db.ConversationMessages.FindAsync([conversationMessageId], CancellationToken.None) is { } logged)
        {
            logged.PlatformMessageId = providerId;
            logged.ProcessingStatus = status == OutboxStatus.Sent ? ProcessingStatus.Processed : ProcessingStatus.Failed;
            logged.ProcessedAtUtc = now;
            logged.ErrorMessage = error is null ? null : Truncate(error);
        }
    }

    /// <summary>Raises at most one owner alert per platform per hour. Failed system alerts never raise further alerts.</summary>
    private async Task AlertAsync(OutboxEnvelope envelope, OutboxMessage message, string detail, DateTime now, CancellationToken ct)
    {
        if (envelope.NotificationType == NotificationTypes.SystemAlert) return;
        await notifications.EnqueueOwnerNotificationAsync(NotificationTypes.SystemAlert,
            "SYSTEM ALERT – VINTAGE FADES\n" +
            $"A {envelope.Platform.DisplayName()} message ({envelope.NotificationType}) could not be delivered (outbox #{message.Id}).\n" +
            $"Detail: {Truncate(detail)}",
            $"alert:delivery:{envelope.Platform}:{now:yyyyMMddHH}", ct);
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
