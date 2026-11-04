using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Application.Messaging;

public static class OutboxEventTypes
{
    public const string CustomerMessage = "CustomerMessage";
    public const string OwnerNotification = "OwnerNotification";
    public const string OwnerCommandReply = "OwnerCommandReply";
    public const string AppointmentReminder = "AppointmentReminder";
}

public static class NotificationTypes
{
    public const string ChatReply = "ChatReply";
    public const string NewBooking = "NewBooking";
    public const string Cancellation = "Cancellation";
    public const string Rescheduled = "Rescheduled";
    public const string HumanAssistance = "HumanAssistance";
    public const string SystemAlert = "SystemAlert";
    public const string Reminder = "Reminder";
    public const string OwnerCommandReply = "OwnerCommandReply";
}

public sealed record OutboxEnvelope
{
    public required string NotificationType { get; init; }
    public required Platform Platform { get; init; }
    /// <summary>Platform recipient id. Null for owner notifications (resolved from configuration at send time).</summary>
    public string? Recipient { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<ReplyOption>? Options { get; init; }
    public long? ConversationId { get; init; }
    public long? ConversationMessageId { get; init; }
    public long? BookingId { get; init; }
    public DateTime? ExpectedStartAtUtc { get; init; }
    public TemplateRequest? Template { get; init; }
    /// <summary>Business-initiated message: free-form text only inside the platform's customer-service window.</summary>
    public bool RequiresOpenWindow { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Serialize() => JsonSerializer.Serialize(this, Json);
    public static OutboxEnvelope Deserialize(string json) =>
        JsonSerializer.Deserialize<OutboxEnvelope>(json, Json) ?? throw new InvalidOperationException("Empty outbox payload.");
}

/// <summary>
/// Writes outbound messages to the transactional outbox. Callers save in the same unit of work as the
/// business change, so a committed booking always has its confirmation and owner notification recorded.
/// </summary>
public sealed class NotificationService(IAppDbContext db, TimeProvider time, IOptions<NotificationOptions> options)
{
    private readonly NotificationOptions _options = options.Value;

    public void EnqueueCustomerMessage(Conversation conversation, OutboundMessage message, long? conversationMessageId, string? deduplicationKey = null) =>
        Add(OutboxEventTypes.CustomerMessage, new OutboxEnvelope
        {
            NotificationType = NotificationTypes.ChatReply,
            Platform = conversation.Platform,
            Recipient = conversation.PlatformConversationId,
            Text = message.Text,
            Options = message.Options,
            ConversationId = conversation.Id,
            ConversationMessageId = conversationMessageId
        }, deduplicationKey);

    public async Task EnqueueOwnerNotificationAsync(string notificationType, string text, string? deduplicationKey, CancellationToken ct)
    {
        if (deduplicationKey is not null && await db.OutboxMessages.AnyAsync(o => o.DeduplicationKey == deduplicationKey, ct))
            return;

        var template = _options.OwnerNotificationTemplate;
        Add(OutboxEventTypes.OwnerNotification, new OutboxEnvelope
        {
            NotificationType = notificationType,
            Platform = Platform.WhatsApp,
            Text = text,
            Template = template.IsConfigured
                ? new TemplateRequest(template.Name!, template.Language, [ToTemplateParameter(text)])
                : null
        }, deduplicationKey);
    }

    public void EnqueueOwnerCommandReply(string text, string? deduplicationKey) =>
        Add(OutboxEventTypes.OwnerCommandReply, new OutboxEnvelope
        {
            NotificationType = NotificationTypes.OwnerCommandReply,
            Platform = Platform.WhatsApp,
            Text = text
        }, deduplicationKey);

    public void EnqueueReminder(Conversation conversation, Domain.Booking booking, string text, IReadOnlyList<string> templateParameters, string deduplicationKey)
    {
        var template = _options.ReminderTemplate;
        Add(OutboxEventTypes.AppointmentReminder, new OutboxEnvelope
        {
            NotificationType = NotificationTypes.Reminder,
            Platform = conversation.Platform,
            Recipient = conversation.PlatformConversationId,
            Text = text,
            ConversationId = conversation.Id,
            BookingId = booking.Id,
            ExpectedStartAtUtc = booking.StartAtUtc,
            RequiresOpenWindow = true,
            Template = conversation.Platform == Platform.WhatsApp && template.IsConfigured
                ? new TemplateRequest(template.Name!, template.Language, templateParameters.Select(ToTemplateParameter).ToList())
                : null
        }, deduplicationKey);
    }

    private void Add(string eventType, OutboxEnvelope envelope, string? deduplicationKey)
    {
        var now = time.GetUtcNow().UtcDateTime;
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = eventType,
            Payload = envelope.Serialize(),
            DeduplicationKey = deduplicationKey,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
            Status = OutboxStatus.Pending
        });
    }

    /// <summary>WhatsApp template parameters may not contain new lines, tabs or more than four consecutive spaces.</summary>
    public static string ToTemplateParameter(string value)
    {
        var flattened = Regex.Replace(value.Replace("\r", ""), @"\n+", " | ");
        flattened = Regex.Replace(flattened.Replace('\t', ' '), " {2,}", " ").Trim();
        return flattened.Length <= 1000 ? flattened : flattened[..997] + "...";
    }
}
