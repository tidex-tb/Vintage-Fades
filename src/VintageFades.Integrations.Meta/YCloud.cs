using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageFades.Application;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Integrations.Meta;

/// <summary>
/// WhatsApp through the Meta Solution Partner YCloud (https://docs.ycloud.com), used for Coexistence so the shop keeps
/// its existing WhatsApp Business app number. POST {BaseUrl}/whatsapp/messages/sendDirectly with X-API-Key.
/// Message bodies (text, interactive, template) use the same objects as the Cloud API, plus "from" and E.164 "to".
/// </summary>
public sealed class YCloudWhatsAppChannel(HttpClient http, IOptions<YCloudOptions> options, ILogger<YCloudWhatsAppChannel> logger) : IMessagingChannel
{
    public Platform Platform => Platform.WhatsApp;
    public bool IsConfigured => options.Value.IsConfigured;

    private string Url => $"{options.Value.BaseUrl.TrimEnd('/')}/whatsapp/messages/sendDirectly";

    public async Task<SendResult> SendAsync(string recipientId, OutboundMessage message, CancellationToken ct)
    {
        if (!IsConfigured) return SendResult.Failed(SendFailureKind.NotConfigured, "YCloud is not configured.");
        var parts = SplitText(message.Text);
        SendResult result = SendResult.Sent(null);
        for (var i = 0; i < parts.Count; i++)
        {
            var part = new OutboundMessage(parts[i], i == parts.Count - 1 ? message.Options : null);
            result = await PostAsync(BuildPayload(options.Value.FromNumber!, recipientId, part), ct);
            if (!result.Success) return result;
        }
        return result;
    }

    public Task<SendResult> SendTemplateAsync(string recipientId, TemplateRequest template, CancellationToken ct)
    {
        if (!IsConfigured) return Task.FromResult(SendResult.Failed(SendFailureKind.NotConfigured, "YCloud is not configured."));
        var parameters = new JsonArray();
        foreach (var value in template.BodyParameters) parameters.Add(new JsonObject { ["type"] = "text", ["text"] = value });
        var payload = new JsonObject
        {
            ["from"] = ToE164(options.Value.FromNumber!),
            ["to"] = ToE164(recipientId),
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
        return PostAsync(payload, ct);
    }

    /// <summary>Reuses the Cloud API message object (buttons / list / text) and swaps the addressing fields for YCloud's.</summary>
    public static JsonObject BuildPayload(string fromNumber, string recipientId, OutboundMessage message)
    {
        var payload = WhatsAppChannel.BuildPayload(recipientId, message);
        payload.Remove("messaging_product");
        payload.Remove("recipient_type");
        payload["from"] = ToE164(fromNumber);
        payload["to"] = ToE164(recipientId);
        return payload;
    }

    public static string ToE164(string number) => "+" + PhoneNumbers.Normalize(number);

    private async Task<SendResult> PostAsync(JsonObject payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-API-Key", options.Value.ApiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var (result, error) = YCloudErrorClassifier.Interpret(response.StatusCode, body);
            if (!result.Success) logger.LogWarning("YCloud send failed ({Kind}): {Error}", result.Failure, error);
            return result;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "YCloud send network failure");
            return SendResult.Failed(SendFailureKind.Transient, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "YCloud send timed out");
            return SendResult.Failed(SendFailureKind.Transient, "Request to YCloud timed out.");
        }
    }

    private static List<string> SplitText(string text)
    {
        if (text.Length <= WhatsAppChannel.TextLimit) return [text];
        var parts = new List<string>();
        for (var i = 0; i < text.Length; i += WhatsAppChannel.TextLimit)
            parts.Add(text.Substring(i, Math.Min(WhatsAppChannel.TextLimit, text.Length - i)));
        return parts;
    }
}

/// <summary>
/// YCloud errors: { "error": { "status", "code", "message", "whatsappApiError": {Meta error} } }. 429 and 5xx are
/// retryable, 401 is an authentication problem, other 4xx are permanent. A Meta error inside is classified with Meta's codes.
/// A 2xx response whose message has status "failed" is also a failure.
/// </summary>
public static class YCloudErrorClassifier
{
    public static (SendResult Result, string? Error) Interpret(HttpStatusCode status, string body)
    {
        JsonNode? root = null;
        try { root = JsonNode.Parse(body); } catch (JsonException) { }

        if ((int)status is >= 200 and < 300)
        {
            if ((string?)root?["status"] != "failed") return (SendResult.Sent((string?)root?["id"]), null);
            var (kind, description) = ClassifyMeta(HttpStatusCode.BadRequest, root?["whatsappApiError"], (string?)root?["errorCode"], (string?)root?["errorMessage"]);
            return (SendResult.Failed(kind, description), description);
        }

        var error = root?["error"];
        var metaError = error?["whatsappApiError"];
        if (metaError is not null)
        {
            var (kind, description) = ClassifyMeta(status, metaError, null, (string?)error?["message"]);
            return (SendResult.Failed(kind, description), description);
        }

        var text = $"HTTP {(int)status}" + ((string?)error?["code"] is { } code ? $", YCloud {code}" : "") +
                   ((string?)error?["message"] is { } m ? $": {m}" : "");
        var failure = status switch
        {
            HttpStatusCode.Unauthorized => SendFailureKind.AuthenticationFailed,
            HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout => SendFailureKind.Transient,
            _ when (int)status >= 500 => SendFailureKind.Transient,
            _ => SendFailureKind.Permanent
        };
        return (SendResult.Failed(failure, text), text);
    }

    private static (SendFailureKind, string) ClassifyMeta(HttpStatusCode status, JsonNode? metaError, string? fallbackCode, string? fallbackMessage)
    {
        var code = ParseInt(metaError?["code"]) ?? ParseInt(fallbackCode);
        var subcode = ParseInt(metaError?["error_subcode"]);
        var message = (string?)metaError?["message"] ?? fallbackMessage;
        return MetaErrorClassifier.Classify(status, code, subcode, message);
    }

    private static int? ParseInt(JsonNode? node) => node is null ? null : ParseInt(node.ToString());
    private static int? ParseInt(string? value) => int.TryParse(value, out var n) ? n : null;
}

public static class YCloudSignatureValidator
{
    /// <summary>
    /// Validates YCloud-Signature: "t={unix seconds},s={hex HMAC-SHA256}" where the HMAC (key = endpoint secret) is
    /// computed over "{t}.{raw body}". Rejects timestamps outside the tolerance window (replay protection).
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> body, string? header, string? secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;

        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t")
            {
                if (timestamp is not null) return false;
                timestamp = kv[1];
            }
            else if (kv[0] == "s") signatures.Add(kv[1]);
        }
        if (timestamp is null || signatures.Count == 0 || !long.TryParse(timestamp, out var seconds)) return false;
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds) > tolerance.TotalSeconds) return false;

        var signed = new byte[Encoding.UTF8.GetByteCount(timestamp) + 1 + body.Length];
        var written = Encoding.UTF8.GetBytes(timestamp, signed);
        signed[written] = (byte)'.';
        body.CopyTo(signed.AsSpan(written + 1));
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);

        foreach (var candidate in signatures)
        {
            byte[] provided;
            try { provided = Convert.FromHexString(candidate); }
            catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(expected, provided)) return true;
        }
        return false;
    }
}

/// <summary>
/// YCloud webhook events (one event per delivery): whatsapp.inbound_message.received (customer messages),
/// whatsapp.message.updated (delivery status of our sends, keyed by YCloud message id) and
/// whatsapp.smb.message.echoes (the shop replied from the WhatsApp Business app on a Coexistence number).
/// Numbers are normalised to digits so they match Meta's wa_id format used elsewhere.
/// </summary>
public sealed class YCloudWebhookParser : IWebhookPayloadParser
{
    public Platform Platform => Platform.WhatsApp;

    public WebhookParseResult Parse(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var eventId = WhatsAppWebhookParser.Str(root, "id");
        var messages = new List<InboundMessage>();
        var statuses = new List<DeliveryStatusUpdate>();
        var replies = new List<BusinessAppReply>();

        switch (WhatsAppWebhookParser.Str(root, "type"))
        {
            case "whatsapp.inbound_message.received" when root.TryGetProperty("whatsappInboundMessage", out var m):
            {
                var from = PhoneNumbers.Normalize(WhatsAppWebhookParser.Str(m, "from"));
                var id = WhatsAppWebhookParser.Str(m, "wamid") ?? WhatsAppWebhookParser.Str(m, "id");
                if (from is null || id is null) break;

                string? text = null, reply = null;
                var type = MessageType.Unsupported;
                switch (WhatsAppWebhookParser.Str(m, "type"))
                {
                    case "text":
                        text = m.TryGetProperty("text", out var t) ? WhatsAppWebhookParser.Str(t, "body") : null;
                        type = MessageType.Text;
                        break;
                    case "interactive" when m.TryGetProperty("interactive", out var interactive):
                        var kind = WhatsAppWebhookParser.Str(interactive, "type");
                        if (kind is "button_reply" or "list_reply" && interactive.TryGetProperty(kind, out var choice))
                        {
                            reply = WhatsAppWebhookParser.Str(choice, "id");
                            text = WhatsAppWebhookParser.Str(choice, "title");
                            type = MessageType.Interactive;
                        }
                        break;
                    case "button" when m.TryGetProperty("button", out var button):
                        reply = WhatsAppWebhookParser.Str(button, "payload");
                        text = WhatsAppWebhookParser.Str(button, "text");
                        type = MessageType.Interactive;
                        break;
                }
                var name = m.TryGetProperty("customerProfile", out var profile) ? WhatsAppWebhookParser.Str(profile, "name") : null;
                messages.Add(new InboundMessage(Platform.WhatsApp, from, id, Time(m, "sendTime"), type, text, reply, name));
                break;
            }
            case "whatsapp.message.updated" when root.TryGetProperty("whatsappMessage", out var m):
            {
                var id = WhatsAppWebhookParser.Str(m, "id");
                var status = WhatsAppWebhookParser.Str(m, "status");
                if (id is null || status is null) break;
                var error = WhatsAppWebhookParser.Str(m, "errorCode") is { } code ? $"{code} {WhatsAppWebhookParser.Str(m, "errorMessage")}".Trim() : null;
                statuses.Add(new DeliveryStatusUpdate(Platform.WhatsApp, id, status, error));
                break;
            }
            case "whatsapp.smb.message.echoes" when root.TryGetProperty("whatsappMessage", out var m):
            {
                var to = PhoneNumbers.Normalize(WhatsAppWebhookParser.Str(m, "to"));
                var id = WhatsAppWebhookParser.Str(m, "wamid") ?? WhatsAppWebhookParser.Str(m, "id");
                if (to is null || id is null) break;
                var text = m.TryGetProperty("text", out var t) ? WhatsAppWebhookParser.Str(t, "body") : $"[{WhatsAppWebhookParser.Str(m, "type")}]";
                replies.Add(new BusinessAppReply(Platform.WhatsApp, to, id, Time(m, "sendTime"), text));
                break;
            }
        }
        return new WebhookParseResult(messages, statuses, eventId) { BusinessReplies = replies };
    }

    private static DateTime Time(JsonElement e, string name) =>
        DateTimeOffset.TryParse(WhatsAppWebhookParser.Str(e, name), out var value) ? value.UtcDateTime : DateTime.UtcNow;
}
