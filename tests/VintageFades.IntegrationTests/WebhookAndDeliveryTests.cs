using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VintageFades.Application.Messaging;
using VintageFades.Application.Operations;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class WebhookAndDeliveryTests(TestApp app) : IntegrationTest(app)
{
    [Theory]
    [InlineData("whatsapp")]
    [InlineData("instagram")]
    [InlineData("messenger")]
    public async Task Verification_echoes_challenge_only_for_the_right_token(string platform)
    {
        var ok = await App.Client.GetAsync($"/webhooks/{platform}?hub.mode=subscribe&hub.verify_token={TestApp.VerifyToken}&hub.challenge=12345");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("12345", await ok.Content.ReadAsStringAsync());

        var bad = await App.Client.GetAsync($"/webhooks/{platform}?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=12345");
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
    }

    [Fact]
    public async Task Unknown_platform_returns_not_found() =>
        Assert.Equal(HttpStatusCode.NotFound, (await App.Client.GetAsync("/webhooks/telegram?hub.mode=subscribe")).StatusCode);

    [Theory]
    [InlineData(Platform.WhatsApp, "sha256=0000")]
    [InlineData(Platform.Instagram, "")]
    [InlineData(Platform.Messenger, "sha1=abc")]
    public async Task Invalid_signature_is_rejected_and_nothing_is_stored(Platform platform, string signature)
    {
        var payload = App.BuildPayload(platform, "user-1", "hi");
        var response = await App.PostWebhookAsync(platform, payload, signature);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await App.Db(db => db.WebhookEvents.CountAsync()));
    }

    [Fact]
    public async Task Duplicate_webhook_delivery_is_processed_once()
    {
        var payload = App.BuildPayload(Platform.WhatsApp, "264811234000", "hi");
        Assert.Equal(HttpStatusCode.OK, (await App.PostWebhookAsync(Platform.WhatsApp, payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await App.PostWebhookAsync(Platform.WhatsApp, payload)).StatusCode);
        await App.ProcessAsync();

        Assert.Equal(1, await App.Db(db => db.WebhookEvents.CountAsync()));
        Assert.Equal(1, await App.Db(db => db.ConversationMessages.CountAsync(m => m.Direction == MessageDirection.Inbound)));
        Assert.Single(App.Meta.To("264811234000"));
    }

    [Fact]
    public async Task Same_message_in_a_different_delivery_does_not_book_twice()
    {
        const string sender = "264811234001";
        await App.SendAsync(Platform.WhatsApp, sender, "Book me with Kapza tomorrow at 10:00");
        await App.SendAsync(Platform.WhatsApp, sender, "Kim");
        var yes = App.BuildPayload(Platform.WhatsApp, sender, "yes");
        await App.PostWebhookAsync(Platform.WhatsApp, yes);
        // Meta can redeliver the same message id in a differently shaped batch.
        await App.PostWebhookAsync(Platform.WhatsApp, yes.Replace("\"field\":\"messages\"", "\"field\":\"messages\" "));
        await App.ProcessAsync();

        Assert.Equal(1, await App.Db(db => db.Bookings.CountAsync(b => b.Status == BookingStatus.Confirmed)));
        Assert.Single(App.Meta.To(sender), r => r.Text.Contains("is confirmed"));
    }

    [Theory]
    [InlineData(Platform.Instagram, "https://graph.facebook.com/v26.0/me/messages", "Bearer ig-token")]
    [InlineData(Platform.Messenger, "https://graph.facebook.com/v26.0/PAGEID/messages", "Bearer fb-token")]
    [InlineData(Platform.WhatsApp, "https://graph.facebook.com/v26.0/PNID/messages", "Bearer wa-token")]
    public async Task Each_platform_enters_the_same_chatbot_and_replies_on_its_own_api(Platform platform, string url, string auth)
    {
        var replies = await App.SendAsync(platform, "sender-42", "hello");
        Assert.Contains("Welcome to VINTAGE FADES BARBER SHOP!", Assert.Single(replies));
        var request = Assert.Single(App.Meta.To("sender-42"));
        Assert.Equal(url, request.Url);
        Assert.Equal(auth, request.Authorization);

        var conversation = await App.Db(db => db.Conversations.Include(c => c.Customer).SingleAsync());
        Assert.Equal(platform, conversation.Platform);
        switch (platform)
        {
            case Platform.Instagram: Assert.Equal("sender-42", conversation.Customer!.InstagramScopedUserId); break;
            case Platform.Messenger: Assert.Equal("sender-42", conversation.Customer!.FacebookScopedUserId); break;
            default: Assert.Equal("sender-42", conversation.Customer!.WhatsAppId); break;
        }
    }

    [Fact]
    public async Task Messenger_echoes_of_our_own_messages_are_ignored()
    {
        var payload = App.BuildPayload(Platform.Messenger, "psid-1", "Welcome!").Replace("\"text\":\"Welcome!\"", "\"text\":\"Welcome!\",\"is_echo\":true");
        await App.PostWebhookAsync(Platform.Messenger, payload);
        await App.ProcessAsync();
        Assert.Empty(App.Meta.Requests);
        Assert.Equal(0, await App.Db(db => db.Conversations.CountAsync()));
    }

    [Fact]
    public async Task Transient_provider_failure_is_retried_with_backoff()
    {
        App.Meta.FailNext(1, HttpStatusCode.InternalServerError, """{"error":{"code":2,"message":"Service temporarily unavailable"}}""");
        await App.SendAsync(Platform.Messenger, "psid-2", "hello");

        var pending = await App.Db(db => db.OutboxMessages.SingleAsync());
        Assert.Equal(OutboxStatus.Pending, pending.Status);
        Assert.Equal(1, pending.RetryCount);
        Assert.Contains("HTTP 500", pending.LastError);

        Assert.Equal(0, await App.DispatchAsync()); // not due yet
        App.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await App.DispatchAsync());
        Assert.Equal(OutboxStatus.Sent, (await App.Db(db => db.OutboxMessages.SingleAsync())).Status);
        Assert.Equal(2, App.Meta.To("psid-2").Count);
    }

    [Fact]
    public async Task Permanent_failure_is_recorded_not_retried()
    {
        App.Meta.FailNext(1, HttpStatusCode.BadRequest, """{"error":{"code":100,"message":"Invalid parameter"}}""");
        await App.SendAsync(Platform.Instagram, "igsid-3", "hello");

        var outbox = await App.Db(db => db.OutboxMessages.SingleAsync());
        Assert.Equal(OutboxStatus.Failed, outbox.Status);
        var log = await App.Db(db => db.NotificationLogs.SingleAsync());
        Assert.Equal(NotificationStatus.Failed, log.Status);
        Assert.Contains("Meta error 100", log.ErrorMessage);
        var message = await App.Db(db => db.ConversationMessages.SingleAsync(m => m.Direction == MessageDirection.Outbound));
        Assert.Equal(ProcessingStatus.Failed, message.ProcessingStatus);
    }

    [Fact]
    public async Task Invalid_token_raises_an_owner_alert()
    {
        App.Meta.FailNext(1, HttpStatusCode.Unauthorized, """{"error":{"code":190,"message":"Error validating access token"}}""");
        await App.SendAsync(Platform.Messenger, "psid-4", "hello");
        await App.DispatchAsync();
        Assert.Contains(App.Meta.To(TestApp.OwnerNumber), r => r.Text.Contains("SYSTEM ALERT") && r.Text.Contains("AuthenticationFailed"));
    }

    [Fact]
    public async Task Delivery_status_updates_are_recorded()
    {
        await App.SendAsync(Platform.WhatsApp, "264811234002", "hello");
        var log = await App.Db(db => db.NotificationLogs.SingleAsync());
        var statusPayload = $$$"""
            {"object":"whatsapp_business_account","entry":[{"changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"PNID"},
            "statuses":[{"id":"{{{log.ProviderMessageId}}}","status":"read","timestamp":"1","recipient_id":"264811234002"}]}}]}]}
            """;
        await App.PostWebhookAsync(Platform.WhatsApp, statusPayload);
        await App.ProcessAsync();
        Assert.Equal("read", (await App.Db(db => db.NotificationLogs.SingleAsync())).DeliveryStatus);
    }

    [Fact]
    public async Task Reminders_are_sent_once_and_respect_the_messaging_window()
    {
        const string sender = "264811234003";
        await App.SendAsync(Platform.WhatsApp, sender, "Book me with Kapza tomorrow at 14:00");
        await App.SendAsync(Platform.WhatsApp, sender, "Lee");
        await App.SendAsync(Platform.WhatsApp, sender, "yes");

        Task<int> Schedule() => App.WithScopeAsync(sp => sp.GetRequiredService<ReminderScheduler>().EnqueueDueRemindersAsync(CancellationToken.None));

        Assert.Equal(0, await Schedule());
        App.Time.SetUtcNow(new DateTimeOffset(2026, 11, 2, 12, 1, 0, TimeSpan.Zero)); // 24h before 14:00 local on 3 Nov
        Assert.Equal(1, await Schedule());
        Assert.Equal(0, await Schedule());
        await App.DispatchAsync();
        Assert.Contains(App.Meta.To(sender), r => r.Text.Contains("Just a friendly reminder") && r.Text.Contains("Time: 14:00"));

        App.Time.SetUtcNow(new DateTimeOffset(2026, 11, 3, 10, 1, 0, TimeSpan.Zero)); // 2h before; last message was 28h ago
        Assert.Equal(1, await Schedule());
        await App.DispatchAsync();
        var skipped = await App.Db(db => db.OutboxMessages.Where(o => o.EventType == OutboxEventTypes.AppointmentReminder).OrderBy(o => o.Id).ToListAsync());
        Assert.Equal([OutboxStatus.Sent, OutboxStatus.Skipped], skipped.Select(o => o.Status));
        Assert.Contains("24-hour messaging window", skipped[1].LastError);
    }

    [Fact]
    public async Task Cancelled_bookings_get_no_reminder()
    {
        const string sender = "264811234004";
        await App.SendAsync(Platform.WhatsApp, sender, "Book me with Kapza tomorrow at 14:00");
        await App.SendAsync(Platform.WhatsApp, sender, "Lee");
        await App.SendAsync(Platform.WhatsApp, sender, "yes");
        App.Time.SetUtcNow(new DateTimeOffset(2026, 11, 2, 12, 1, 0, TimeSpan.Zero));
        await App.WithScopeAsync(sp => sp.GetRequiredService<ReminderScheduler>().EnqueueDueRemindersAsync(CancellationToken.None));
        // Cancelled after the reminder was queued but before it was delivered.
        var bookingId = await App.Db(db => db.Bookings.Select(b => b.Id).SingleAsync());
        await App.WithScopeAsync(sp => sp.GetRequiredService<VintageFades.Application.Bookings.BookingService>()
            .CancelBookingAsync(bookingId, null, "test", false, CancellationToken.None));

        await App.DispatchAsync();
        Assert.DoesNotContain(App.Meta.To(sender), r => r.Text.Contains("friendly reminder"));
        Assert.Equal(OutboxStatus.Skipped, (await App.Db(db => db.OutboxMessages.SingleAsync(o => o.EventType == OutboxEventTypes.AppointmentReminder))).Status);
        Assert.Equal(0, await App.WithScopeAsync(sp => sp.GetRequiredService<ReminderScheduler>().EnqueueDueRemindersAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Admin_api_requires_key_and_supports_manual_booking()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.Client.GetAsync("/api/admin/status")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/bookings")
        {
            Content = JsonContent.Create(new { customerName = "Walk-in", phoneNumber = "+264 81 555 0000", date = "2026-11-07", time = "10:00:00", barberId = 2 })
        };
        request.Headers.Add("X-Admin-Key", TestApp.AdminKey);
        var response = await App.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var duplicate = new HttpRequestMessage(HttpMethod.Post, "/api/admin/bookings")
        {
            Content = JsonContent.Create(new { customerName = "Other", date = "2026-11-07", time = "10:00:00", barberId = 2 })
        };
        duplicate.Headers.Add("X-Admin-Key", TestApp.AdminKey);
        Assert.Equal(HttpStatusCode.Conflict, (await App.Client.SendAsync(duplicate)).StatusCode);

        var status = new HttpRequestMessage(HttpMethod.Get, "/api/admin/status");
        status.Headers.Add("X-Admin-Key", TestApp.AdminKey);
        Assert.Equal(HttpStatusCode.OK, (await App.Client.SendAsync(status)).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_respond()
    {
        Assert.Equal(HttpStatusCode.OK, (await App.Client.GetAsync("/health/live")).StatusCode);
        var ready = await App.Client.GetAsync("/health/ready");
        Assert.Contains("\"database\":\"Healthy\"", await ready.Content.ReadAsStringAsync());
    }
}
