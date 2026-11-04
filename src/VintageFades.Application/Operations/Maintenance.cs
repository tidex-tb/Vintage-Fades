using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Messaging;
using VintageFades.Domain;

namespace VintageFades.Application.Operations;

/// <summary>Queues appointment reminders. One reminder per booking, offset and start time (deduplication key),
/// only for confirmed bookings that already existed when the reminder became due.</summary>
public sealed class ReminderScheduler(
    IAppDbContext db,
    ShopSettingsProvider settings,
    NotificationService notifications,
    TimeProvider time,
    IOptions<BookingOptions> options,
    ILogger<ReminderScheduler> logger)
{
    public async Task<int> EnqueueDueRemindersAsync(CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var queued = 0;

        foreach (var offset in EnabledOffsets(calendar.Settings.ReminderMinutesBefore))
        {
            var horizon = now.AddMinutes(offset);
            var due = await db.Bookings.AsNoTracking().Include(b => b.Barber).Include(b => b.Customer)
                .Where(b => b.Status == BookingStatus.Confirmed && b.StartAtUtc > now && b.StartAtUtc <= horizon
                            && b.CreatedAtUtc <= b.StartAtUtc.AddMinutes(-offset)
                            && b.SourcePlatform != BookingSource.Admin)
                .ToListAsync(ct);

            foreach (var booking in due)
            {
                var key = $"reminder:{booking.Id}:{offset}:{booking.StartAtUtc:yyyyMMddHHmm}";
                if (await db.OutboxMessages.AnyAsync(o => o.DeduplicationKey == key, ct)) continue;

                var platform = booking.SourcePlatform switch
                {
                    BookingSource.WhatsApp => Platform.WhatsApp,
                    BookingSource.Instagram => Platform.Instagram,
                    _ => Platform.Messenger
                };
                var conversation = await db.Conversations.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CustomerId == booking.CustomerId && c.Platform == platform, ct);
                if (conversation is null) continue;

                var local = calendar.ToLocal(booking.StartAtUtc);
                var name = booking.Customer?.FullName ?? "there";
                var date = Format.DateShort(DateOnly.FromDateTime(local));
                var startTime = Format.Time(TimeOnly.FromDateTime(local));
                var barber = booking.Barber?.DisplayName ?? "your barber";
                var text = $"Hi {name}!\nJust a friendly reminder about your appointment at {calendar.Settings.ShopName}.\n\n" +
                           $"Date: {date}\nTime: {startTime}\nBarber: {barber}\n\nWe look forward to seeing you!";
                notifications.EnqueueReminder(conversation, booking, text, [name, date, startTime, barber], key);
                queued++;
            }
        }

        if (queued > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Queued {Count} appointment reminders", queued);
        }
        return queued;
    }

    private IEnumerable<int> EnabledOffsets(int[] offsets) => offsets.Distinct().Where(o => o > 0 && o switch
    {
        1440 => options.Value.Reminder24HoursEnabled,
        120 => options.Value.Reminder2HoursEnabled,
        _ => true
    });
}

/// <summary>Detects stalled processing and alerts the owner (at most once per hour per problem).</summary>
public sealed class OperationalMonitor(IAppDbContext db, NotificationService notifications, TimeProvider time)
{
    public static readonly TimeSpan WebhookStallThreshold = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OutboxStallThreshold = TimeSpan.FromMinutes(30);

    public async Task CheckAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var webhookCutoff = now - WebhookStallThreshold;
        var outboxCutoff = now - OutboxStallThreshold;

        if (await db.WebhookEvents.AnyAsync(e => e.ProcessingStatus == ProcessingStatus.Pending && e.ReceivedAtUtc < webhookCutoff, ct))
            await AlertAsync("webhook-stalled", "Incoming messages have been waiting more than 10 minutes to be processed. Customers may not be getting replies.", now, ct);

        if (await db.OutboxMessages.AnyAsync(o => o.Status == OutboxStatus.Pending && o.CreatedAtUtc < outboxCutoff, ct))
            await AlertAsync("outbox-stalled", "Outgoing messages have been waiting more than 30 minutes. A messaging integration may be down.", now, ct);

        await db.SaveChangesAsync(ct);
    }

    private Task AlertAsync(string kind, string text, DateTime now, CancellationToken ct) =>
        notifications.EnqueueOwnerNotificationAsync(NotificationTypes.SystemAlert, $"SYSTEM ALERT – VINTAGE FADES\n{text}",
            $"alert:{kind}:{now:yyyyMMddHH}", ct);
}

/// <summary>Deletes operational records older than the configured retention periods (data minimisation).
/// Bookings and customers are business records and are not purged.</summary>
public sealed class DataRetentionService(IAppDbContext db, TimeProvider time, IOptions<RetentionOptions> options, ILogger<DataRetentionService> logger)
{
    public async Task PurgeAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var o = options.Value;
        var messageCutoff = now.AddDays(-o.ConversationMessageDays);
        var webhookCutoff = now.AddDays(-o.WebhookEventDays);
        var outboxCutoff = now.AddDays(-o.OutboxMessageDays);
        var logCutoff = now.AddDays(-o.NotificationLogDays);

        var messages = await db.ConversationMessages.Where(m => m.ReceivedAtUtc < messageCutoff).ExecuteDeleteAsync(ct);
        var webhooks = await db.WebhookEvents.Where(e => e.ReceivedAtUtc < webhookCutoff && e.ProcessingStatus != ProcessingStatus.Pending).ExecuteDeleteAsync(ct);
        var outbox = await db.OutboxMessages.Where(m => m.CreatedAtUtc < outboxCutoff && m.Status != OutboxStatus.Pending).ExecuteDeleteAsync(ct);
        var logs = await db.NotificationLogs.Where(n => n.SentAtUtc < logCutoff).ExecuteDeleteAsync(ct);
        logger.LogInformation("Retention purge removed {Messages} messages, {Webhooks} webhook events, {Outbox} outbox rows, {Logs} notification logs",
            messages, webhooks, outbox, logs);
    }
}

public sealed record OperationalStatus(
    int PendingWebhookEvents,
    int FailedWebhookEventsLast24h,
    int PendingOutboxMessages,
    int FailedOutboxMessagesLast24h,
    int SkippedOutboxMessagesLast24h,
    int ConversationsAwaitingHuman,
    DateTime? LastWebhookReceivedAtUtc,
    DateTime? LastMessageSentAtUtc,
    IReadOnlyDictionary<string, bool> Channels);

public sealed class OperationalStatusService(IAppDbContext db, IEnumerable<IMessagingChannel> channels, TimeProvider time)
{
    public async Task<OperationalStatus> GetAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow().UtcDateTime.AddHours(-24);
        return new OperationalStatus(
            await db.WebhookEvents.CountAsync(e => e.ProcessingStatus == ProcessingStatus.Pending, ct),
            await db.WebhookEvents.CountAsync(e => e.ProcessingStatus == ProcessingStatus.Failed && e.ReceivedAtUtc >= since, ct),
            await db.OutboxMessages.CountAsync(o => o.Status == OutboxStatus.Pending, ct),
            await db.OutboxMessages.CountAsync(o => o.Status == OutboxStatus.Failed && o.CreatedAtUtc >= since, ct),
            await db.OutboxMessages.CountAsync(o => o.Status == OutboxStatus.Skipped && o.CreatedAtUtc >= since, ct),
            await db.Conversations.CountAsync(c => c.RequiresAttention, ct),
            await db.WebhookEvents.MaxAsync(e => (DateTime?)e.ReceivedAtUtc, ct),
            await db.NotificationLogs.Where(n => n.Status == NotificationStatus.Sent).MaxAsync(n => (DateTime?)n.SentAtUtc, ct),
            channels.ToDictionary(c => c.Platform.ToString(), c => c.IsConfigured));
    }
}
