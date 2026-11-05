using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Chatbot;
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

/// <summary>
/// A customer who goes quiet in the middle of a booking is asked once whether they want to continue. If that question
/// also goes unanswered, the session is reset: any held slot is released and the next message starts from the menu.
/// Only conversations inside the 24-hour messaging window are contacted.
/// </summary>
public sealed class IdleConversationService(
    IAppDbContext db,
    BookingService bookings,
    NotificationService notifications,
    TimeProvider time,
    IOptions<ChatbotOptions> options,
    ILogger<IdleConversationService> logger)
{
    private static readonly ConversationState[] MidFlow =
    [
        ConversationState.CollectingCustomerName, ConversationState.CollectingBookingDate, ConversationState.CollectingBookingTime,
        ConversationState.SelectingBarber, ConversationState.OfferingAlternatives, ConversationState.AwaitingBookingConfirmation,
        ConversationState.AwaitingCancellationSelection, ConversationState.AwaitingCancellationConfirmation,
        ConversationState.Rescheduling, ConversationState.OfferingHumanSupport
    ];

    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var o = options.Value;
        var quietSince = now.AddMinutes(-o.IdleCheckMinutes);
        var windowStart = now.AddHours(-24);

        var idle = await db.Conversations
            .Where(c => MidFlow.Contains(c.CurrentState) && c.LastInboundAtUtc != null && c.LastInboundAtUtc < quietSince && c.LastInboundAtUtc > windowStart)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var conversation in idle)
        {
            try
            {
                var draft = BookingDraft.Deserialize(conversation.CurrentBookingIntent);
                if (draft.IdleCheckSentAtUtc is null)
                {
                    draft.IdleCheckSentAtUtc = now;
                    conversation.CurrentBookingIntent = draft.Serialize();
                    await SendAsync(conversation, new OutboundMessage(
                        "Are you still there? Would you like to continue with your booking?\n\n" +
                        "Reply *yes* to carry on, or *menu* to start again. If I don't hear back, I'll close this request.",
                        [new("yes", "Yes, continue"), new("menu", "Start again")]), now, ct);
                }
                else if (draft.IdleCheckSentAtUtc <= now.AddMinutes(-o.IdleResetMinutes))
                {
                    if (draft.HeldBookingId is long holdId) await bookings.ReleaseHoldAsync(holdId, ct);
                    conversation.CurrentState = ConversationState.Idle;
                    conversation.CurrentBookingIntent = null;
                    await SendAsync(conversation, new OutboundMessage(
                        "I haven't heard back, so I've closed your booking request and released the slot. Message me any time to book again. 💈"), now, ct);
                }
                else
                {
                    continue;
                }
                conversation.UpdatedAtUtc = now;
                await db.SaveChangesAsync(ct);
                changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Idle check failed for conversation {ConversationId}", conversation.Id);
            }
        }
        if (changed > 0) logger.LogInformation("Idle conversations handled: {Count}", changed);
        return changed;
    }

    private async Task SendAsync(Conversation conversation, OutboundMessage message, DateTime now, CancellationToken ct)
    {
        var logged = new ConversationMessage
        {
            ConversationId = conversation.Id,
            Platform = conversation.Platform,
            Direction = MessageDirection.Outbound,
            MessageType = message.Options is null ? MessageType.Text : MessageType.Interactive,
            MessageContent = message.Text,
            ProcessingStatus = ProcessingStatus.Pending,
            ReceivedAtUtc = now
        };
        db.ConversationMessages.Add(logged);
        await db.SaveChangesAsync(ct);
        notifications.EnqueueCustomerMessage(conversation, message, logged.Id);
    }
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
