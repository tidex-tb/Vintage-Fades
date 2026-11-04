using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;
using VintageFades.Integrations.Meta;

namespace VintageFades.UnitTests;

public class MetaSignatureValidatorTests
{
    private const string Secret = "app-secret";

    private static string Sign(string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Accepts_valid_signature() =>
        Assert.True(MetaSignatureValidator.IsValid(Encoding.UTF8.GetBytes("{\"a\":1}"), Sign("{\"a\":1}"), Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abc")]
    [InlineData("sha256=nothex")]
    [InlineData("sha256=00")]
    public void Rejects_missing_or_malformed_signature(string? header) =>
        Assert.False(MetaSignatureValidator.IsValid(Encoding.UTF8.GetBytes("{}"), header, Secret));

    [Fact]
    public void Rejects_tampered_body() =>
        Assert.False(MetaSignatureValidator.IsValid(Encoding.UTF8.GetBytes("{\"a\":2}"), Sign("{\"a\":1}"), Secret));

    [Fact]
    public void Rejects_when_secret_not_configured() =>
        Assert.False(MetaSignatureValidator.IsValid(Encoding.UTF8.GetBytes("{}"), Sign("{}"), null));
}

public class MetaErrorClassifierTests
{
    [Theory]
    [InlineData(400, """{"error":{"code":131047,"message":"Re-engagement message"}}""", SendFailureKind.OutsideMessagingWindow)]
    [InlineData(400, """{"error":{"code":10,"error_subcode":2018278,"message":"outside window"}}""", SendFailureKind.OutsideMessagingWindow)]
    [InlineData(401, """{"error":{"code":190,"message":"Invalid OAuth access token"}}""", SendFailureKind.AuthenticationFailed)]
    [InlineData(400, """{"error":{"code":130429,"message":"Rate limit hit"}}""", SendFailureKind.Transient)]
    [InlineData(500, """{"error":{"code":1,"message":"Unknown"}}""", SendFailureKind.Transient)]
    [InlineData(503, "<html>bad gateway</html>", SendFailureKind.Transient)]
    [InlineData(400, """{"error":{"code":100,"message":"Invalid parameter"}}""", SendFailureKind.Permanent)]
    [InlineData(400, """{"error":{"code":131026,"message":"Message undeliverable"}}""", SendFailureKind.Permanent)]
    public void Classifies(int status, string body, SendFailureKind expected) =>
        Assert.Equal(expected, MetaErrorClassifier.Classify((HttpStatusCode)status, body).Kind);
}

public class PayloadBuilderTests
{
    [Fact]
    public void Three_short_options_become_whatsapp_buttons()
    {
        var payload = WhatsAppChannel.BuildPayload("264811234567", new OutboundMessage("Confirm?", [new("yes", "Yes"), new("no", "No")]));
        Assert.Equal("interactive", (string?)payload["type"]);
        Assert.Equal("button", (string?)payload["interactive"]!["type"]);
        Assert.Equal("yes", (string?)payload["interactive"]!["action"]!["buttons"]![0]!["reply"]!["id"]);
    }

    [Fact]
    public void Up_to_ten_options_become_a_whatsapp_list()
    {
        var options = Enumerable.Range(1, 5).Select(i => new ReplyOption(i.ToString(), $"Option {i}")).ToList();
        var payload = WhatsAppChannel.BuildPayload("264811234567", new OutboundMessage("Pick one", options));
        Assert.Equal("list", (string?)payload["interactive"]!["type"]);
        Assert.Equal(5, payload["interactive"]!["action"]!["sections"]![0]!["rows"]!.AsArray().Count);
    }

    [Fact]
    public void Plain_text_without_options()
    {
        var payload = WhatsAppChannel.BuildPayload("264811234567", new OutboundMessage("Hello"));
        Assert.Equal("text", (string?)payload["type"]);
        Assert.Equal("Hello", (string?)payload["text"]!["body"]);
        Assert.Equal("whatsapp", (string?)payload["messaging_product"]);
    }

    [Fact]
    public void Send_api_quick_replies_are_capped_and_titles_clipped()
    {
        var options = Enumerable.Range(1, 15).Select(i => new ReplyOption(i.ToString(), $"A very long option title {i}")).ToList();
        var payload = SendApiChannel.BuildPayload("psid", "Pick", options);
        var replies = payload["message"]!["quick_replies"]!.AsArray();
        Assert.Equal(13, replies.Count);
        Assert.True(((string?)replies[0]!["title"])!.Length <= 20);
        Assert.Equal("psid", (string?)payload["recipient"]!["id"]);
    }
}

public class WebhookParserTests
{
    private static IOptions<T> Opt<T>(T value) where T : class => Options.Create(value);

    [Fact]
    public void Parses_whatsapp_text_interactive_and_statuses()
    {
        const string json = """
        {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp","metadata":{"display_phone_number":"264610000000","phone_number_id":"PNID"},
          "contacts":[{"profile":{"name":"John"},"wa_id":"264811234567"}],
          "messages":[
            {"from":"264811234567","id":"wamid.1","timestamp":"1793000000","type":"text","text":{"body":"Hi"}},
            {"from":"264811234567","id":"wamid.2","timestamp":"1793000001","type":"interactive","interactive":{"type":"button_reply","button_reply":{"id":"yes","title":"Yes"}}},
            {"from":"264811234567","id":"wamid.3","timestamp":"1793000002","type":"image","image":{"id":"x"}}
          ],
          "statuses":[{"id":"wamid.out","status":"delivered","timestamp":"1793000003","recipient_id":"264811234567"}]}}]}]}
        """;
        var result = new WhatsAppWebhookParser(Opt(new WhatsAppOptions { PhoneNumberId = "PNID" })).Parse(json);
        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("Hi", result.Messages[0].Text);
        Assert.Equal("John", result.Messages[0].SenderName);
        Assert.Equal("yes", result.Messages[1].ReplyPayload);
        Assert.Equal(MessageType.Unsupported, result.Messages[2].MessageType);
        Assert.Equal("delivered", Assert.Single(result.Statuses).Status);
    }

    [Fact]
    public void Ignores_whatsapp_events_for_other_phone_numbers()
    {
        const string json = """
        {"object":"whatsapp_business_account","entry":[{"changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"OTHER"},
          "messages":[{"from":"1","id":"wamid.x","timestamp":"1","type":"text","text":{"body":"Hi"}}]}}]}]}
        """;
        Assert.Empty(new WhatsAppWebhookParser(Opt(new WhatsAppOptions { PhoneNumberId = "PNID" })).Parse(json).Messages);
    }

    [Fact]
    public void Parses_messenger_messages_quick_replies_echoes_and_postbacks()
    {
        const string json = """
        {"object":"page","entry":[{"id":"PAGE","time":1,"messaging":[
          {"sender":{"id":"PSID"},"recipient":{"id":"PAGE"},"timestamp":1793000000000,"message":{"mid":"m_1","text":"Book me"}},
          {"sender":{"id":"PSID"},"recipient":{"id":"PAGE"},"timestamp":1793000000001,"message":{"mid":"m_2","text":"Yes","quick_reply":{"payload":"yes"}}},
          {"sender":{"id":"PAGE"},"recipient":{"id":"PSID"},"timestamp":1793000000002,"message":{"mid":"m_3","text":"echo","is_echo":true}},
          {"sender":{"id":"PSID"},"recipient":{"id":"PAGE"},"timestamp":1793000000003,"postback":{"title":"Get Started","payload":"hi"}}
        ]}]}
        """;
        var result = new MessengerWebhookParser(Opt(new FacebookOptions { PageId = "PAGE" })).Parse(json);
        Assert.Equal(4, result.Messages.Count);
        Assert.All(result.Messages, m => Assert.Equal(Platform.Messenger, m.Platform));
        Assert.Equal("yes", result.Messages[1].ReplyPayload);
        Assert.True(result.Messages[2].IsEcho);
        Assert.Equal("hi", result.Messages[3].ReplyPayload);
    }

    [Fact]
    public void Parses_instagram_and_rejects_wrong_object()
    {
        const string json = """
        {"object":"instagram","entry":[{"id":"IGID","time":1,"messaging":[
          {"sender":{"id":"IGSID"},"recipient":{"id":"IGID"},"timestamp":1793000000000,"message":{"mid":"ig_1","text":"hello"}}]}]}
        """;
        var parser = new InstagramWebhookParser(Opt(new InstagramOptions { AccountId = "IGID" }));
        var message = Assert.Single(parser.Parse(json).Messages);
        Assert.Equal(Platform.Instagram, message.Platform);
        Assert.Equal("IGSID", message.SenderId);
        Assert.Empty(new MessengerWebhookParser(Opt(new FacebookOptions())).Parse(json).Messages);
    }

    [Fact]
    public void Instagram_needs_only_the_page_token_and_accepts_any_account_without_an_id()
    {
        const string json = """
        {"object":"instagram","entry":[{"id":"IGID","time":1,"messaging":[
          {"sender":{"id":"IGSID"},"recipient":{"id":"IGID"},"timestamp":1793000000000,"message":{"mid":"ig_1","text":"hello"}}]}]}
        """;
        Assert.True(new InstagramOptions { AccessToken = "page-token" }.IsConfigured);
        Assert.Single(new InstagramWebhookParser(Opt(new InstagramOptions())).Parse(json).Messages);
        Assert.Empty(new InstagramWebhookParser(Opt(new InstagramOptions { AccountId = "OTHER" })).Parse(json).Messages);
    }
}
