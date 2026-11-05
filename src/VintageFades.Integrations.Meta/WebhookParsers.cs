using System.Text.Json;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Integrations.Meta;

/// <summary>
/// WhatsApp Cloud API webhook: object "whatsapp_business_account", entry[].changes[] with field "messages";
/// value.messages[] (text, interactive button_reply/list_reply, template button) and value.statuses[].
/// Events for other phone numbers on the same WABA are ignored.
/// </summary>
public sealed class WhatsAppWebhookParser(IOptions<WhatsAppOptions> options) : IWebhookPayloadParser
{
    public Platform Platform => Platform.WhatsApp;

    public WebhookParseResult Parse(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var messages = new List<InboundMessage>();
        var statuses = new List<DeliveryStatusUpdate>();
        if (Str(root, "object") != "whatsapp_business_account") return new(messages, statuses, null);
        var phoneNumberId = options.Value.PhoneNumberId;

        foreach (var entry in Arr(root, "entry"))
        foreach (var change in Arr(entry, "changes"))
        {
            if (Str(change, "field") != "messages" || !change.TryGetProperty("value", out var value)) continue;
            if (value.TryGetProperty("metadata", out var metadata) && !string.IsNullOrEmpty(phoneNumberId) &&
                Str(metadata, "phone_number_id") is { } target && target != phoneNumberId) continue;

            var names = Arr(value, "contacts").Where(c => Str(c, "wa_id") is not null)
                .GroupBy(c => Str(c, "wa_id")!)
                .ToDictionary(g => g.Key, g => g.First().TryGetProperty("profile", out var p) ? Str(p, "name") : null);

            foreach (var m in Arr(value, "messages"))
            {
                var from = Str(m, "from");
                var id = Str(m, "id");
                if (from is null || id is null) continue;
                var timestamp = long.TryParse(Str(m, "timestamp"), out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : DateTime.UtcNow;

                string? text = null, reply = null;
                var type = MessageType.Unsupported;
                switch (Str(m, "type"))
                {
                    case "text":
                        text = m.TryGetProperty("text", out var t) ? Str(t, "body") : null;
                        type = MessageType.Text;
                        break;
                    case "interactive" when m.TryGetProperty("interactive", out var interactive):
                        var kind = Str(interactive, "type");
                        if (kind is "button_reply" or "list_reply" && interactive.TryGetProperty(kind, out var choice))
                        {
                            reply = Str(choice, "id");
                            text = Str(choice, "title");
                            type = MessageType.Interactive;
                        }
                        break;
                    case "button" when m.TryGetProperty("button", out var button):
                        reply = Str(button, "payload");
                        text = Str(button, "text");
                        type = MessageType.Interactive;
                        break;
                }
                messages.Add(new InboundMessage(Platform.WhatsApp, from, id, timestamp, type, text, reply, names.GetValueOrDefault(from)));
            }

            foreach (var s in Arr(value, "statuses"))
            {
                var id = Str(s, "id");
                var status = Str(s, "status");
                if (id is null || status is null) continue;
                var error = Arr(s, "errors").Select(e => $"{(e.TryGetProperty("code", out var c) ? c.ToString() : "")} {Str(e, "title")}".Trim()).FirstOrDefault();
                statuses.Add(new DeliveryStatusUpdate(Platform.WhatsApp, id, status, error));
            }
        }
        return new(messages, statuses, messages.FirstOrDefault()?.MessageId ?? statuses.FirstOrDefault()?.ProviderMessageId);
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}

/// <summary>
/// Messenger ("page") and Instagram ("instagram") share the messaging webhook shape:
/// entry[].messaging[] with sender.id, recipient.id, timestamp (ms), and message {mid, text, is_echo, quick_reply.payload}
/// or postback {mid, title, payload}. Echoes of our own sends are flagged and ignored downstream.
/// </summary>
public abstract class MessagingWebhookParser(IOptions<MetaOptions> meta) : IWebhookPayloadParser
{
    public abstract Platform Platform { get; }
    protected abstract string ObjectName { get; }
    /// <summary>Configured Page / IG account id; when set, entries for other accounts are ignored.</summary>
    protected abstract string? AccountId { get; }

    public WebhookParseResult Parse(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var messages = new List<InboundMessage>();
        var replies = new List<BusinessAppReply>();
        if (WhatsAppWebhookParser.Str(root, "object") != ObjectName) return new(messages, [], null);

        foreach (var entry in WhatsAppWebhookParser.Arr(root, "entry"))
        {
            if (!string.IsNullOrEmpty(AccountId) && WhatsAppWebhookParser.Str(entry, "id") is { } entryId && entryId != AccountId) continue;

            foreach (var e in WhatsAppWebhookParser.Arr(entry, "messaging"))
            {
                var sender = e.TryGetProperty("sender", out var s) ? WhatsAppWebhookParser.Str(s, "id") : null;
                if (sender is null) continue;
                var timestamp = e.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.UtcNow;

                if (e.TryGetProperty("message", out var m))
                {
                    var mid = WhatsAppWebhookParser.Str(m, "mid");
                    if (mid is null) continue;
                    var isEcho = m.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True;
                    var isDeleted = m.TryGetProperty("is_deleted", out var del) && del.ValueKind == JsonValueKind.True;
                    if (isDeleted) continue;
                    var text = WhatsAppWebhookParser.Str(m, "text");
                    if (isEcho)
                    {
                        // The Page sent this message. If this app sent it, it is our own reply. Otherwise a person typed it
                        // in the Page or Instagram inbox (or Meta's automations sent it), which the bot treats as a handover.
                        var appId = m.TryGetProperty("app_id", out var a) ? a.ToString() : null;
                        var customer = e.TryGetProperty("recipient", out var r) ? WhatsAppWebhookParser.Str(r, "id") : null;
                        if (customer is not null && (string.IsNullOrEmpty(appId) || appId != meta.Value.AppId))
                            replies.Add(new BusinessAppReply(Platform, customer, mid, timestamp, text ?? "[attachment]"));
                        continue;
                    }
                    var quick = m.TryGetProperty("quick_reply", out var q) ? WhatsAppWebhookParser.Str(q, "payload") : null;
                    var type = quick is not null ? MessageType.Interactive : text is not null ? MessageType.Text : MessageType.Unsupported;
                    messages.Add(new InboundMessage(Platform, sender, mid, timestamp, type, text, quick, null, isEcho));
                }
                else if (e.TryGetProperty("postback", out var p))
                {
                    var payloadValue = WhatsAppWebhookParser.Str(p, "payload");
                    var mid = WhatsAppWebhookParser.Str(p, "mid") ?? $"postback:{sender}:{timestamp:O}";
                    messages.Add(new InboundMessage(Platform, sender, mid, timestamp, MessageType.Interactive,
                        WhatsAppWebhookParser.Str(p, "title"), payloadValue, null));
                }
                // delivery, read, reaction and seen events need no action.
            }
        }
        return new(messages, [], messages.FirstOrDefault()?.MessageId ?? replies.FirstOrDefault()?.MessageId) { BusinessReplies = replies };
    }
}

public sealed class MessengerWebhookParser(IOptions<FacebookOptions> options, IOptions<MetaOptions> meta) : MessagingWebhookParser(meta)
{
    public override Platform Platform => Platform.Messenger;
    protected override string ObjectName => "page";
    protected override string? AccountId => options.Value.PageId;
}

public sealed class InstagramWebhookParser(IOptions<InstagramOptions> options, IOptions<MetaOptions> meta) : MessagingWebhookParser(meta)
{
    public override Platform Platform => Platform.Instagram;
    protected override string ObjectName => "instagram";
    protected override string? AccountId => options.Value.AccountId;
}
