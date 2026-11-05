using VintageFades.Domain;

namespace VintageFades.Application.Abstractions;

/// <summary>A selectable reply. <see cref="Id"/> is the text the chatbot accepts for this choice in the current state.</summary>
public sealed record ReplyOption(string Id, string Title, string? Description = null);

/// <summary>Platform-neutral outgoing message. Adapters render options as buttons, lists or quick replies where supported.
/// <see cref="Text"/> always stands on its own so typed replies keep working.</summary>
public sealed record OutboundMessage(string Text, IReadOnlyList<ReplyOption>? Options = null);

public sealed record TemplateRequest(string Name, string Language, IReadOnlyList<string> BodyParameters);

public enum SendFailureKind
{
    None,
    Transient,
    Permanent,
    OutsideMessagingWindow,
    AuthenticationFailed,
    NotConfigured,
    NotSupported
}

public sealed record SendResult(bool Success, string? ProviderMessageId, SendFailureKind Failure, string? Error)
{
    public static SendResult Sent(string? providerMessageId) => new(true, providerMessageId, SendFailureKind.None, null);
    public static SendResult Failed(SendFailureKind kind, string error) => new(false, null, kind, error);
}

/// <summary>Outbound adapter for one messaging platform. Contains no booking logic.</summary>
public interface IMessagingChannel
{
    Platform Platform { get; }
    bool IsConfigured { get; }
    Task<SendResult> SendAsync(string recipientId, OutboundMessage message, CancellationToken ct);
    Task<SendResult> SendTemplateAsync(string recipientId, TemplateRequest template, CancellationToken ct);
}

public sealed record InboundMessage(
    Platform Platform,
    string SenderId,
    string MessageId,
    DateTime TimestampUtc,
    MessageType MessageType,
    string? Text,
    string? ReplyPayload,
    string? SenderName,
    bool IsEcho = false);

public sealed record DeliveryStatusUpdate(Platform Platform, string ProviderMessageId, string Status, string? Error);

/// <summary>A message the shop typed itself: in the WhatsApp Business app on a Coexistence number, or in the
/// Facebook Page / Instagram inbox.</summary>
public sealed record BusinessAppReply(Platform Platform, string CustomerId, string MessageId, DateTime TimestampUtc, string? Text);

public sealed record WebhookParseResult(
    IReadOnlyList<InboundMessage> Messages,
    IReadOnlyList<DeliveryStatusUpdate> Statuses,
    string? FirstEventId)
{
    public IReadOnlyList<BusinessAppReply> BusinessReplies { get; init; } = [];
}

/// <summary>Parses a verified Meta webhook payload for one platform into platform-neutral events.</summary>
public interface IWebhookPayloadParser
{
    Platform Platform { get; }
    WebhookParseResult Parse(string payload);
}

public enum WorkQueue
{
    Webhooks,
    Outbox
}

/// <summary>Wakes background workers immediately after new work is committed (they also poll).</summary>
public interface IWorkSignal
{
    void Notify(WorkQueue queue);
    Task WaitAsync(WorkQueue queue, TimeSpan timeout, CancellationToken ct);
}
