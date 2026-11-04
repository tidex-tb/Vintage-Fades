using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class YCloudCollection : ICollectionFixture<YCloudTestApp>
{
    public const string Name = "ycloud";
}

/// <summary>Same host, with WhatsApp running through YCloud (Coexistence) instead of the direct Meta Cloud API.</summary>
public sealed class YCloudTestApp : TestApp
{
    public const string WebhookSecret = "ycloud-webhook-secret";
    public const string ApiKey = "ycloud-api-key";
    public const string ShopNumber = "+264610000000";
    private int _events;

    protected override void Customize(Dictionary<string, string?> settings)
    {
        settings["WhatsApp:Provider"] = "YCloud";
        settings["WhatsApp:PhoneNumberId"] = null;
        settings["WhatsApp:AccessToken"] = null;
        settings["YCloud:ApiKey"] = ApiKey;
        settings["YCloud:WebhookSecret"] = WebhookSecret;
        settings["YCloud:FromNumber"] = ShopNumber;
    }

    public string Sign(string payload, DateTimeOffset? at = null, string secret = WebhookSecret)
    {
        var t = (at ?? Time.GetUtcNow()).ToUnixTimeSeconds().ToString();
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}"));
        return $"t={t},s={Convert.ToHexStringLower(mac)}";
    }

    public async Task<HttpResponseMessage> PostAsync(string payload, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/ycloud") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("YCloud-Signature", signature ?? Sign(payload));
        return await Client.SendAsync(request);
    }

    public string Event(string type, string objectKey, JsonObject body) => new JsonObject
    {
        ["id"] = $"evt_{Interlocked.Increment(ref _events)}",
        ["type"] = type,
        ["apiVersion"] = "v2",
        ["createTime"] = Time.GetUtcNow().ToString("O"),
        [objectKey] = body
    }.ToJsonString();

    public string Inbound(string from, string text, string? buttonId = null)
    {
        var n = Interlocked.Increment(ref _events);
        var message = new JsonObject
        {
            ["id"] = $"in_{n}",
            ["wamid"] = $"wamid.yc{n}",
            ["wabaId"] = "WABA",
            ["from"] = from,
            ["to"] = ShopNumber,
            ["sendTime"] = Time.GetUtcNow().ToString("O"),
            ["customerProfile"] = new JsonObject { ["name"] = "Test" }
        };
        if (buttonId is null)
        {
            message["type"] = "text";
            message["text"] = new JsonObject { ["body"] = text };
        }
        else
        {
            message["type"] = "interactive";
            message["interactive"] = new JsonObject { ["type"] = "button_reply", ["button_reply"] = new JsonObject { ["id"] = buttonId, ["title"] = text } };
        }
        return Event("whatsapp.inbound_message.received", "whatsappInboundMessage", message);
    }

    /// <summary>Customer message through YCloud; returns the texts the bot sent back to that number.</summary>
    public async Task<IReadOnlyList<string>> SayAsync(string from, string text)
    {
        var before = Meta.Requests.Count;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Inbound(from, text))).StatusCode);
        await ProcessAsync();
        return Meta.Requests.Skip(before).Where(r => r.Recipient == from).Select(r => r.Text).ToList();
    }
}

[Collection(YCloudCollection.Name)]
public class YCloudWhatsAppTests(YCloudTestApp app) : IntegrationTest(app)
{
    private const string Customer = "+264812345678";
    private YCloudTestApp YApp => (YCloudTestApp)App;

    [Fact]
    public async Task Customer_message_is_answered_through_ycloud_from_the_shop_number()
    {
        var replies = await YApp.SayAsync(Customer, "hello");
        Assert.Contains("Welcome to VINTAGE FADES BARBER SHOP!", Assert.Single(replies));

        var request = Assert.Single(App.Meta.To(Customer));
        Assert.Equal("https://api.ycloud.com/v2/whatsapp/messages/sendDirectly", request.Url);
        Assert.Equal(YCloudTestApp.ApiKey, request.ApiKey);
        Assert.Null(request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(YCloudTestApp.ShopNumber, (string?)body["from"]);
        Assert.Null(body["messaging_product"]);

        var customer = await App.Db(db => db.Customers.SingleAsync());
        Assert.Equal("264812345678", customer.WhatsAppId);
        Assert.Equal("+264812345678", customer.PhoneNumber);
    }

    [Fact]
    public async Task Full_booking_and_owner_notification_work_through_ycloud()
    {
        await YApp.SayAsync(Customer, "Book me with Barber 2 tomorrow at 15:00");
        await YApp.SayAsync(Customer, "John");
        var done = string.Join("\n", await YApp.SayAsync(Customer, "yes"));
        Assert.Contains("is confirmed", done);

        var owner = App.Meta.To("+" + TestApp.OwnerNumber);
        Assert.Contains(owner, r => r.Text.Contains("NEW BOOKING") && r.Url.Contains("api.ycloud.com"));
        Assert.Equal(1, await App.Db(db => db.Bookings.CountAsync(b => b.Status == BookingStatus.Confirmed)));
    }

    [Fact]
    public async Task Bad_or_stale_signatures_are_rejected()
    {
        var payload = YApp.Inbound(Customer, "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await YApp.PostAsync(payload, YApp.Sign(payload, secret: "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await YApp.PostAsync(payload, YApp.Sign(payload, App.Time.GetUtcNow().AddMinutes(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await YApp.PostAsync(payload, "garbage")).StatusCode);
        Assert.Equal(0, await App.Db(db => db.WebhookEvents.CountAsync()));
    }

    [Fact]
    public async Task Direct_meta_whatsapp_route_is_disabled_when_using_ycloud()
    {
        var payload = App.BuildPayload(Platform.WhatsApp, "264812345678", "hi");
        Assert.Equal(HttpStatusCode.NotFound, (await App.PostWebhookAsync(Platform.WhatsApp, payload)).StatusCode);
    }

    [Fact]
    public async Task Owner_reply_from_business_app_pauses_the_bot_then_it_resumes()
    {
        await YApp.SayAsync(Customer, "Book me with Barber 1 tomorrow at 10:00"); // creates a hold
        var echo = YApp.Event("whatsapp.smb.message.echoes", "whatsappMessage", new JsonObject
        {
            ["id"] = "smb_1", ["wamid"] = "wamid.smb1", ["from"] = YCloudTestApp.ShopNumber, ["to"] = Customer,
            ["type"] = "text", ["text"] = new JsonObject { ["body"] = "Hi, Peter here, I'll sort you out." },
            ["status"] = "sent", ["sendTime"] = App.Time.GetUtcNow().ToString("O")
        });
        Assert.Equal(HttpStatusCode.OK, (await YApp.PostAsync(echo)).StatusCode);
        await App.ProcessAsync();

        var conversation = await App.Db(db => db.Conversations.SingleAsync());
        Assert.Equal(ConversationState.HumanSupportRequested, conversation.CurrentState);
        Assert.Equal(BookingStatus.Cancelled, (await App.Db(db => db.Bookings.SingleAsync())).Status); // hold released
        Assert.Contains(App.Meta.To("+" + TestApp.OwnerNumber), r => r.Text.Contains("BOT PAUSED") && r.Text.Contains($"RESUME {conversation.Id}"));

        Assert.Empty(await YApp.SayAsync(Customer, "thanks Peter"));

        App.Time.Advance(TimeSpan.FromHours(13));
        Assert.Contains("Welcome", string.Join("\n", await YApp.SayAsync(Customer, "hi")));
    }

    [Fact]
    public async Task Delivery_status_from_ycloud_is_recorded()
    {
        await YApp.SayAsync(Customer, "hello");
        var log = await App.Db(db => db.NotificationLogs.SingleAsync());
        Assert.StartsWith("yc_", log.ProviderMessageId);

        var update = YApp.Event("whatsapp.message.updated", "whatsappMessage", new JsonObject
        {
            ["id"] = log.ProviderMessageId, ["wamid"] = "wamid.x", ["status"] = "delivered"
        });
        await YApp.PostAsync(update);
        await App.ProcessAsync();
        Assert.Equal("delivered", (await App.Db(db => db.NotificationLogs.SingleAsync())).DeliveryStatus);
    }

    [Fact]
    public async Task Ycloud_error_is_retried_without_losing_the_message()
    {
        App.Meta.FailNext(1, HttpStatusCode.TooManyRequests, """{"error":{"status":429,"code":"TOO_MANY_REQUESTS","message":"slow down"}}""");
        await YApp.SayAsync(Customer, "hello");
        var outbox = await App.Db(db => db.OutboxMessages.SingleAsync());
        Assert.Equal(OutboxStatus.Pending, outbox.Status);
        Assert.Contains("TOO_MANY_REQUESTS", outbox.LastError);

        App.Time.Advance(TimeSpan.FromMinutes(1));
        await App.DispatchAsync();
        Assert.Equal(OutboxStatus.Sent, (await App.Db(db => db.OutboxMessages.SingleAsync())).Status);
    }
}
