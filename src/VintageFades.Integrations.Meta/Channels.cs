using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Integrations.Meta;

/// <summary>WhatsApp Cloud API: POST /{version}/{phone-number-id}/messages.
/// Up to 3 short options become reply buttons, up to 10 become a list message, otherwise plain text.</summary>
public sealed class WhatsAppChannel(HttpClient http, IOptions<MetaOptions> meta, IOptions<WhatsAppOptions> options, ILogger<WhatsAppChannel> logger)
    : MetaGraphClient(http, logger), IMessagingChannel
{
    public const int TextLimit = 4096;
    public const int InteractiveBodyLimit = 1024;

    public Platform Platform => Platform.WhatsApp;
    public bool IsConfigured => options.Value.IsConfigured;

    private string Url => $"{meta.Value.GraphBaseUrl.TrimEnd('/')}/{meta.Value.ApiVersion}/{options.Value.PhoneNumberId}/messages";

    public async Task<SendResult> SendAsync(string recipientId, OutboundMessage message, CancellationToken ct)
    {
        if (!IsConfigured) return SendResult.Failed(SendFailureKind.NotConfigured, "WhatsApp is not configured.");
        var parts = Split(message.Text, t => t.Length <= TextLimit);
        SendResult result = SendResult.Sent(null);
        for (var i = 0; i < parts.Count; i++)
        {
            var isLast = i == parts.Count - 1;
            var payload = isLast ? BuildPayload(recipientId, new OutboundMessage(parts[i], message.Options)) : BuildPayload(recipientId, new OutboundMessage(parts[i]));
            result = await PostAsync(Url, options.Value.AccessToken!, payload, ReadId, ct);
            if (!result.Success) return result;
        }
        return result;
    }

    public Task<SendResult> SendTemplateAsync(string recipientId, TemplateRequest template, CancellationToken ct)
    {
        if (!IsConfigured) return Task.FromResult(SendResult.Failed(SendFailureKind.NotConfigured, "WhatsApp is not configured."));
        var parameters = new JsonArray();
        foreach (var value in template.BodyParameters) parameters.Add(new JsonObject { ["type"] = "text", ["text"] = value });
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = recipientId,
            ["type"] = "template",
            ["template"] = new JsonObject
            {
                ["name"] = template.Name,
                ["language"] = new JsonObject { ["code"] = template.Language },
                ["components"] = template.BodyParameters.Count == 0
                    ? new JsonArray()
                    : new JsonArray(new JsonObject { ["type"] = "body", ["parameters"] = parameters })
            }
        };
        return PostAsync(Url, options.Value.AccessToken!, payload, ReadId, ct);
    }

    public static JsonObject BuildPayload(string recipientId, OutboundMessage message)
    {
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = recipientId
        };
        var opts = message.Options;
        if (opts is { Count: > 0 } && message.Text.Length <= InteractiveBodyLimit)
        {
            if (opts.Count <= 3 && opts.All(o => o.Title.Length <= 20))
            {
                var buttons = new JsonArray();
                foreach (var o in opts)
                    buttons.Add(new JsonObject { ["type"] = "reply", ["reply"] = new JsonObject { ["id"] = o.Id, ["title"] = o.Title } });
                payload["type"] = "interactive";
                payload["interactive"] = new JsonObject
                {
                    ["type"] = "button",
                    ["body"] = new JsonObject { ["text"] = message.Text },
                    ["action"] = new JsonObject { ["buttons"] = buttons }
                };
                return payload;
            }
            if (opts.Count <= 10)
            {
                var rows = new JsonArray();
                foreach (var o in opts)
                {
                    var row = new JsonObject { ["id"] = o.Id, ["title"] = Clip(o.Title, 24) };
                    if (o.Description is not null) row["description"] = Clip(o.Description, 72);
                    rows.Add(row);
                }
                payload["type"] = "interactive";
                payload["interactive"] = new JsonObject
                {
                    ["type"] = "list",
                    ["body"] = new JsonObject { ["text"] = message.Text },
                    ["action"] = new JsonObject
                    {
                        ["button"] = "Choose",
                        ["sections"] = new JsonArray(new JsonObject { ["title"] = "Options", ["rows"] = rows })
                    }
                };
                return payload;
            }
        }
        payload["type"] = "text";
        payload["text"] = new JsonObject { ["preview_url"] = false, ["body"] = message.Text };
        return payload;
    }

    private static string? ReadId(JsonNode? response) => (string?)response?["messages"]?[0]?["id"];
}

/// <summary>Shared Send API shape for Messenger and Instagram: recipient.id + message.text + quick_replies (max 13, title max 20).</summary>
public abstract class SendApiChannel(HttpClient http, ILogger logger) : MetaGraphClient(http, logger), IMessagingChannel
{
    public abstract Platform Platform { get; }
    public abstract bool IsConfigured { get; }
    protected abstract string Url { get; }
    protected abstract string AccessToken { get; }
    protected abstract bool Fits(string text);
    protected virtual void Decorate(JsonObject payload) { }

    public async Task<SendResult> SendAsync(string recipientId, OutboundMessage message, CancellationToken ct)
    {
        if (!IsConfigured) return SendResult.Failed(SendFailureKind.NotConfigured, $"{Platform} is not configured.");
        var parts = Split(message.Text, Fits);
        SendResult result = SendResult.Sent(null);
        for (var i = 0; i < parts.Count; i++)
        {
            var payload = BuildPayload(recipientId, parts[i], i == parts.Count - 1 ? message.Options : null);
            Decorate(payload);
            result = await PostAsync(Url, AccessToken, payload, r => (string?)r?["message_id"], ct);
            if (!result.Success) return result;
        }
        return result;
    }

    public Task<SendResult> SendTemplateAsync(string recipientId, TemplateRequest template, CancellationToken ct) =>
        Task.FromResult(SendResult.Failed(SendFailureKind.NotSupported, $"Templates are not used for {Platform} in Phase 1."));

    public static JsonObject BuildPayload(string recipientId, string text, IReadOnlyList<ReplyOption>? options)
    {
        var message = new JsonObject { ["text"] = text };
        if (options is { Count: > 0 })
        {
            var replies = new JsonArray();
            foreach (var o in options.Take(13))
                replies.Add(new JsonObject { ["content_type"] = "text", ["title"] = Clip(o.Title, 20), ["payload"] = o.Id });
            message["quick_replies"] = replies;
        }
        return new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = recipientId },
            ["message"] = message
        };
    }
}

/// <summary>Messenger Platform Send API: POST /{version}/{page-id}/messages with a Page access token.
/// messaging_type RESPONSE: replies inside the 24-hour standard messaging window only.</summary>
public sealed class MessengerChannel(HttpClient http, IOptions<MetaOptions> meta, IOptions<FacebookOptions> options, ILogger<MessengerChannel> logger)
    : SendApiChannel(http, logger)
{
    public override Platform Platform => Platform.Messenger;
    public override bool IsConfigured => options.Value.IsConfigured;
    protected override string Url => $"{meta.Value.GraphBaseUrl.TrimEnd('/')}/{meta.Value.ApiVersion}/{options.Value.PageId}/messages";
    protected override string AccessToken => options.Value.PageAccessToken!;
    protected override bool Fits(string text) => text.Length <= 2000;
    protected override void Decorate(JsonObject payload) => payload["messaging_type"] = "RESPONSE";
}

/// <summary>Instagram Messaging: POST /{version}/me/messages. With a Page access token (Facebook Login, the only
/// option for new apps) "me" is the Page linked to the Instagram account; with an Instagram Login token on
/// graph.instagram.com it is the Instagram account. Text is limited to 1000 UTF-8 bytes per message.</summary>
public sealed class InstagramChannel(HttpClient http, IOptions<MetaOptions> meta, IOptions<InstagramOptions> options, ILogger<InstagramChannel> logger)
    : SendApiChannel(http, logger)
{
    public override Platform Platform => Platform.Instagram;
    public override bool IsConfigured => options.Value.IsConfigured;
    protected override string Url => $"{options.Value.GraphBaseUrl.TrimEnd('/')}/{meta.Value.ApiVersion}/me/messages";
    protected override string AccessToken => options.Value.AccessToken!;
    protected override bool Fits(string text) => Encoding.UTF8.GetByteCount(text) <= 1000;
}
