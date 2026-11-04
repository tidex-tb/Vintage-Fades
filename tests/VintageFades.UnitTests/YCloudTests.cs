using System.Net;
using System.Security.Cryptography;
using System.Text;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;
using VintageFades.Integrations.Meta;

namespace VintageFades.UnitTests;

public class YCloudSignatureValidatorTests
{
    private const string Secret = "whsec";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static string Header(string body, DateTimeOffset at, string secret = Secret)
    {
        var t = at.ToUnixTimeSeconds().ToString();
        return $"t={t},s={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + "." + body)))}";
    }

    private static bool Valid(string body, string? header) =>
        YCloudSignatureValidator.IsValid(Encoding.UTF8.GetBytes(body), header, Secret, Now, Tolerance);

    [Fact] public void Accepts_valid_signature() => Assert.True(Valid("{\"a\":1}", Header("{\"a\":1}", Now)));
    [Fact] public void Accepts_uppercase_hex_and_extra_signatures() =>
        Assert.True(Valid("{}", Header("{}", Now).Replace(",s=", ",s=00,s=").ToUpperInvariant().Replace("T=", "t=").Replace(",S=", ",s=")));
    [Fact] public void Rejects_wrong_secret() => Assert.False(Valid("{}", Header("{}", Now, "other")));
    [Fact] public void Rejects_tampered_body() => Assert.False(Valid("{\"a\":2}", Header("{\"a\":1}", Now)));
    [Fact] public void Rejects_old_timestamp() => Assert.False(Valid("{}", Header("{}", Now.AddMinutes(-6))));
    [Fact] public void Rejects_future_timestamp() => Assert.False(Valid("{}", Header("{}", Now.AddMinutes(6))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s=abc")]
    [InlineData("t=1,t=2,s=abc")]
    [InlineData("t=notanumber,s=abc")]
    public void Rejects_malformed_headers(string? header) => Assert.False(Valid("{}", header));
}

public class YCloudErrorClassifierTests
{
    [Fact]
    public void Accepted_message_is_success_with_ycloud_id()
    {
        var (result, _) = YCloudErrorClassifier.Interpret(HttpStatusCode.OK, """{"id":"abc","status":"accepted"}""");
        Assert.True(result.Success);
        Assert.Equal("abc", result.ProviderMessageId);
    }

    [Theory]
    [InlineData(200, """{"id":"x","status":"failed","errorCode":"131047","errorMessage":"Re-engagement"}""", SendFailureKind.OutsideMessagingWindow)]
    [InlineData(400, """{"error":{"status":400,"code":"BAD_REQUEST","message":"x","whatsappApiError":{"code":"131047","message":"window"}}}""", SendFailureKind.OutsideMessagingWindow)]
    [InlineData(400, """{"error":{"status":400,"code":"BAD_REQUEST","message":"x","whatsappApiError":{"code":"100","message":"Invalid parameter"}}}""", SendFailureKind.Permanent)]
    [InlineData(401, """{"error":{"status":401,"code":"UNAUTHORIZED","message":"bad key"}}""", SendFailureKind.AuthenticationFailed)]
    [InlineData(429, """{"error":{"status":429,"code":"TOO_MANY_REQUESTS","message":"slow"}}""", SendFailureKind.Transient)]
    [InlineData(503, "unavailable", SendFailureKind.Transient)]
    [InlineData(403, """{"error":{"status":403,"code":"INSUFFICIENT_BALANCE","message":"top up"}}""", SendFailureKind.Permanent)]
    public void Classifies(int status, string body, SendFailureKind expected) =>
        Assert.Equal(expected, YCloudErrorClassifier.Interpret((HttpStatusCode)status, body).Result.Failure);
}

public class YCloudPayloadTests
{
    [Fact]
    public void Uses_cloud_api_message_objects_with_ycloud_addressing()
    {
        var payload = YCloudWhatsAppChannel.BuildPayload("+264 61 000 0000", "264812345678",
            new OutboundMessage("Confirm?", [new("yes", "Yes"), new("no", "No")]));
        Assert.Equal("+264610000000", (string?)payload["from"]);
        Assert.Equal("+264812345678", (string?)payload["to"]);
        Assert.Null(payload["messaging_product"]);
        Assert.Equal("button", (string?)payload["interactive"]!["type"]);
    }
}

public class YCloudWebhookParserTests
{
    private readonly YCloudWebhookParser _parser = new();

    [Fact]
    public void Parses_inbound_text_and_normalises_number()
    {
        var result = _parser.Parse("""
            {"id":"evt_1","type":"whatsapp.inbound_message.received","apiVersion":"v2","createTime":"2026-11-02T06:00:00.000Z",
             "whatsappInboundMessage":{"id":"m1","wamid":"wamid.1","wabaId":"W","from":"+264812345678","to":"+264610000000",
             "sendTime":"2026-11-02T06:00:00.000Z","type":"text","text":{"body":"Hi"},"customerProfile":{"name":"John"}}}
            """);
        var message = Assert.Single(result.Messages);
        Assert.Equal("264812345678", message.SenderId);
        Assert.Equal("wamid.1", message.MessageId);
        Assert.Equal("Hi", message.Text);
        Assert.Equal("John", message.SenderName);
        Assert.Equal("evt_1", result.FirstEventId);
    }

    [Fact]
    public void Parses_button_and_list_replies()
    {
        var button = _parser.Parse("""
            {"id":"e","type":"whatsapp.inbound_message.received","whatsappInboundMessage":{"wamid":"w","from":"+1","type":"interactive",
             "interactive":{"type":"button_reply","button_reply":{"id":"yes","title":"Yes"}}}}
            """);
        Assert.Equal("yes", Assert.Single(button.Messages).ReplyPayload);
        var list = _parser.Parse("""
            {"id":"e","type":"whatsapp.inbound_message.received","whatsappInboundMessage":{"wamid":"w2","from":"+1","type":"interactive",
             "interactive":{"type":"list_reply","list_reply":{"id":"3","title":"Reschedule"}}}}
            """);
        Assert.Equal("3", Assert.Single(list.Messages).ReplyPayload);
    }

    [Fact]
    public void Unsupported_and_image_messages_are_flagged()
    {
        var result = _parser.Parse("""
            {"id":"e","type":"whatsapp.inbound_message.received","whatsappInboundMessage":{"wamid":"w","from":"+1","type":"image","image":{"id":"i"}}}
            """);
        Assert.Equal(MessageType.Unsupported, Assert.Single(result.Messages).MessageType);
    }

    [Fact]
    public void Parses_status_updates_and_business_app_echoes()
    {
        var status = _parser.Parse("""
            {"id":"e","type":"whatsapp.message.updated","whatsappMessage":{"id":"yc1","wamid":"w","status":"failed","errorCode":"131026","errorMessage":"Undeliverable"}}
            """);
        var update = Assert.Single(status.Statuses);
        Assert.Equal("yc1", update.ProviderMessageId);
        Assert.Equal("131026 Undeliverable", update.Error);

        var echo = _parser.Parse("""
            {"id":"e","type":"whatsapp.smb.message.echoes","whatsappMessage":{"id":"s1","wamid":"wamid.s1","from":"+264610000000","to":"+264812345678",
             "type":"text","text":{"body":"On my way"},"sendTime":"2026-11-02T06:00:00.000Z"}}
            """);
        var reply = Assert.Single(echo.BusinessReplies);
        Assert.Equal("264812345678", reply.CustomerId);
        Assert.Equal("On my way", reply.Text);
        Assert.Empty(echo.Messages);
    }

    [Fact]
    public void Ignores_other_events() =>
        Assert.Empty(_parser.Parse("""{"id":"e","type":"whatsapp.template.reviewed","whatsappTemplate":{}}""").Messages);
}
