using System.Net;
using Microsoft.EntityFrameworkCore;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

/// <summary>Acceptance scenarios A–F from the Phase 1 specification, simulated end to end with mocked Meta APIs.</summary>
[Collection(PostgresCollection.Name)]
public class EndToEndScenarioTests(TestApp app) : IntegrationTest(app)
{
    private const string WhatsAppUser = "264812000001";
    private const string InstagramUser = "IGSID-2000002";
    private const string MessengerUser = "PSID-2000003";

    private async Task BookAsync(Platform platform, string sender, string request, string name)
    {
        await App.SendAsync(platform, sender, request);
        await App.SendAsync(platform, sender, name);
        Assert.Contains("is confirmed", Joined(await App.SendAsync(platform, sender, "yes")));
    }

    [Fact]
    public async Task A_whatsapp_booking_is_immediately_unavailable_on_instagram()
    {
        await BookAsync(Platform.WhatsApp, WhatsAppUser, "Book me with Luther on Saturday at 14:00", "Tom");

        var reply = Joined(await App.SendAsync(Platform.Instagram, InstagramUser, "Book me with Luther on Saturday at 14:00"));
        Assert.Contains("Luther is already booked at 14:00 on Saturday, 7 November.", reply);
        Assert.Equal(1, await App.Db(db => db.Bookings.CountAsync(b => b.BarberId == 2 && b.Status != BookingStatus.Cancelled)));
    }

    [Fact]
    public async Task B_messenger_booking_notifies_owner_on_whatsapp()
    {
        await BookAsync(Platform.Messenger, MessengerUser, "Book me tomorrow at 11:00 with anyone", "Maria");

        var owner = Assert.Single(App.Meta.To(TestApp.OwnerNumber));
        Assert.StartsWith("https://graph.facebook.com/v26.0/PNID/messages", owner.Url);
        Assert.Contains("NEW BOOKING – VINTAGE FADES", owner.Text);
        Assert.Contains("Customer: Maria", owner.Text);
        Assert.Contains("Platform: Facebook Messenger", owner.Text);
        Assert.Contains("Reference: VF-2026-000001", owner.Text);
    }

    [Fact]
    public async Task C_cancelled_whatsapp_slot_is_immediately_bookable_elsewhere()
    {
        await BookAsync(Platform.WhatsApp, WhatsAppUser, "Book me with Kapza on Saturday at 09:00", "Tom");
        await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "cancel my appointment");
        await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "yes");

        var ig = Joined(await App.SendAsync(Platform.Instagram, InstagramUser, "Book me with Kapza on Saturday at 09:00"));
        Assert.Contains("09:00 on Saturday, 7 November with Kapza is available", ig);
        var fb = Joined(await App.SendAsync(Platform.Messenger, MessengerUser, "Is Kapza free on Saturday at 9am?"));
        Assert.Contains("Kapza is already booked", fb); // Instagram customer's hold now occupies it
    }

    [Fact]
    public async Task D_two_customers_racing_for_the_same_barber_only_one_wins()
    {
        // Both customers reach the confirmation step at the same moment for the same slot.
        await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "Book me with Godwin on Saturday at 16:00");
        var second = Joined(await App.SendAsync(Platform.Instagram, InstagramUser, "Book me with Godwin on Saturday at 16:00"));
        Assert.Contains("Godwin is already booked", second);

        await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "Tom");
        Assert.Contains("is confirmed", Joined(await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "yes")));
        Assert.Equal(1, await App.Db(db => db.Bookings.CountAsync(b => b.BarberId == 3 && b.Status == BookingStatus.Confirmed)));
    }

    [Fact]
    public async Task E_human_request_notifies_owner_and_pauses_bot()
    {
        await App.SendAsync(Platform.Instagram, InstagramUser, "hi");
        Assert.Contains("someone will get back to you", Joined(await App.SendAsync(Platform.Instagram, InstagramUser, "I need to talk to a human")));
        Assert.Contains(App.Meta.To(TestApp.OwnerNumber), r => r.Text.Contains("CUSTOMER NEEDS HUMAN ASSISTANCE") && r.Text.Contains("Platform: Instagram"));
        Assert.Empty(await App.SendAsync(Platform.Instagram, InstagramUser, "book me tomorrow at 10"));
        Assert.Equal(0, await App.Db(db => db.Bookings.CountAsync()));
    }

    [Fact]
    public async Task F_failed_confirmation_is_retried_without_a_second_booking()
    {
        await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "Book me with Kapza tomorrow at 15:00");
        await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "Tom");

        App.Meta.FailNext(3, HttpStatusCode.ServiceUnavailable, """{"error":{"code":131016,"message":"Service unavailable"}}""");
        var firstAttempt = await App.SendAsync(Platform.WhatsApp, WhatsAppUser, "yes");
        Assert.Contains("is confirmed", Joined(firstAttempt)); // request was made (and failed) with the confirmation text
        Assert.Equal(1, await App.Db(db => db.Bookings.CountAsync(b => b.Status == BookingStatus.Confirmed)));

        for (var i = 0; i < 6; i++)
        {
            App.Time.Advance(TimeSpan.FromMinutes(10));
            await App.DispatchAsync();
        }

        var confirmations = App.Meta.To(WhatsAppUser).Where(r => r.Text.Contains("is confirmed")).ToList();
        Assert.True(confirmations.Count >= 2);
        Assert.Equal(1, await App.Db(db => db.Bookings.CountAsync()));
        Assert.Equal(0, await App.Db(db => db.OutboxMessages.CountAsync(o => o.Status == Domain.OutboxStatus.Pending)));
        Assert.Contains(await App.Db(db => db.NotificationLogs.Select(n => n.Status).ToListAsync()), s => s == NotificationStatus.Sent);
    }
}
