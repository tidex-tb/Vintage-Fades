using Microsoft.EntityFrameworkCore;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

/// <summary>Conversation tests run through the full pipeline (signed webhook → processor → chatbot → outbox → fake Meta).</summary>
[Collection(PostgresCollection.Name)]
public class ChatbotConversationTests(TestApp app) : IntegrationTest(app)
{
    private const string Customer = "264812345678";

    private async Task<string> Say(string text, string sender = Customer, Platform platform = Platform.WhatsApp) =>
        Joined(await App.SendAsync(platform, sender, text));

    private async Task SetNameAsync(string sender, string name) => await App.Db(async db =>
    {
        var c = await db.Customers.SingleAsync(x => x.WhatsAppId == sender);
        c.FullName = name;
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Greeting_shows_the_welcome_menu()
    {
        var reply = await Say("Hello");
        Assert.Contains("Welcome to VINTAGE FADES BARBER SHOP!", reply);
        Assert.Contains("1. Book an appointment", reply);
        Assert.Contains("5. Speak to someone", reply);
    }

    [Fact]
    public async Task Menu_number_starts_booking()
    {
        await Say("hi");
        Assert.Contains("What date would you like to come in?", await Say("1"));
    }

    [Fact]
    public async Task Full_booking_conversation_confirms_only_after_database_commit()
    {
        Assert.Contains("What date would you like to come in?", await Say("Hi, I need a haircut."));

        var time = await Say("Tomorrow.");
        Assert.Contains("What time would you prefer on Tuesday, 3 November?", time);
        Assert.Contains("We operate from 09:00 to 19:00, with one-hour appointments.", time);

        Assert.Contains("Would you prefer a specific barber or the first available barber?", await Say("14:00."));

        var held = await Say("Anyone.");
        Assert.Contains("Let me check availability", held);
        Assert.Contains("What name should I put the booking under?", held);
        Assert.Equal(BookingStatus.Pending, (await App.Db(db => db.Bookings.SingleAsync())).Status);

        var confirm = await Say("John");
        Assert.Contains("Would you like me to confirm your appointment for Tuesday, 3 November at 14:00 with Barber 1?", confirm);

        var done = await Say("Yes.");
        Assert.Contains("Your appointment at VINTAGE FADES BARBER SHOP is confirmed!", done);
        Assert.Contains("Booking reference: VF-2026-000001", done);
        Assert.Contains("Date: Tuesday, 3 November 2026", done);
        Assert.Contains("Time: 14:00", done);
        Assert.Contains("Barber: Barber 1", done);
        Assert.Contains("Duration: 1 hour", done);

        var booking = await App.Db(db => db.Bookings.Include(b => b.Customer).SingleAsync());
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal("John", booking.Customer!.FullName);
        Assert.Equal(BookingSource.WhatsApp, booking.SourcePlatform);

        var owner = App.Meta.To(TestApp.OwnerNumber);
        Assert.Contains(owner, r => r.Text.Contains("NEW BOOKING – VINTAGE FADES") && r.Text.Contains("Customer: John") && r.Text.Contains("Platform: WhatsApp"));
    }

    [Fact]
    public async Task Only_missing_information_is_requested()
    {
        var reply = await Say("Book me with Barber 2 tomorrow at 15:00.");
        Assert.DoesNotContain("What date", reply);
        Assert.DoesNotContain("What time", reply);
        Assert.DoesNotContain("specific barber", reply);
        Assert.Contains("15:00 on Tuesday, 3 November with Barber 2 is available", reply);
        Assert.Contains("What name", reply);

        // A returning customer is not asked for their name again.
        await Say("John");
        await Say("yes");
        var second = await Say("Book me with Barber 2 on Saturday at 10am");
        Assert.DoesNotContain("What name", second);
        Assert.Contains("Would you like me to confirm your appointment for Saturday, 7 November at 10:00 with Barber 2?", second);
    }

    [Fact]
    public async Task Ambiguous_hour_is_clarified_with_opening_hours()
    {
        var reply = await Say("book me at 7 tomorrow");
        Assert.Contains("did you mean 07:00 or 19:00?", reply);
        Assert.Contains("our first appointment is at 09:00 and the last one starts at 18:00", reply);
        Assert.Equal(0, await App.Db(db => db.Bookings.CountAsync()));
    }

    [Fact]
    public async Task Bare_hour_with_one_sensible_reading_is_used()
    {
        await Say("Can I book tomorrow?");
        Assert.Contains("specific barber", await Say("2"));
    }

    [Fact]
    public async Task Ambiguous_next_weekday_is_confirmed_before_use()
    {
        var reply = await Say("I need a slot next friday");
        Assert.Contains("do you mean Friday, 6 November or Friday, 13 November?", reply);
        Assert.Contains("What time would you prefer on Friday, 13 November?", await Say("2"));
    }

    [Fact]
    public async Task Next_week_asks_which_day()
    {
        var reply = await Say("can i come in next week");
        Assert.Contains("Which day next week works for you?", reply);
        Assert.Contains("1. Monday, 9 November", reply);
    }

    [Fact]
    public async Task Availability_question_lists_open_times()
    {
        await App.Db(async db =>
        {
            var c = new Customer { FullName = "Other", CreatedAtUtc = Now, UpdatedAtUtc = Now };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            for (var barber = 1; barber <= 4; barber++)
                db.Bookings.Add(new Booking
                {
                    BookingReference = $"VF-2026-90000{barber}", CustomerId = c.Id, BarberId = barber, Status = BookingStatus.Confirmed, SourcePlatform = BookingSource.Admin,
                    StartAtUtc = new DateTime(2026, 11, 3, 7, 0, 0, DateTimeKind.Utc), EndAtUtc = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc), CreatedAtUtc = Now, UpdatedAtUtc = Now
                });
            return await db.SaveChangesAsync();
        });
        var reply = await Say("Are you free tomorrow?");
        Assert.Contains("Available: 10:00, 11:00", reply);
        Assert.DoesNotContain("09:00,", reply);
    }

    [Fact]
    public async Task Unavailable_barber_gets_other_barbers_at_the_same_time()
    {
        await Say("Book me with Barber 2 on Saturday at 14:00");
        await Say("Sam");
        await Say("yes");

        const string other = "264819999999";
        var reply = await Say("Is Barber 2 available on Saturday at 2pm?", other);
        Assert.Contains("Barber 2 is already booked at 14:00 on Saturday, 7 November.", reply);
        Assert.Contains("We can offer you:", reply);
        Assert.Contains("1. 14:00 – Barber 1", reply);

        var pick = await Say("1", other);
        Assert.Contains("14:00 on Saturday, 7 November with Barber 1 is available", pick);
    }

    [Fact]
    public async Task Fully_booked_time_offers_alternative_times()
    {
        for (var i = 0; i < 4; i++)
        {
            var sender = $"26481000000{i}";
            await Say("Book me tomorrow at 14:00, anyone", sender);
            await Say(new[] { "Ana", "Ben", "Cleo", "Dan" }[i], sender);
            Assert.Contains("is confirmed", await Say("yes", sender));
        }

        var reply = await Say("Can I get a haircut at 14:00 tomorrow?");
        Assert.Contains("Unfortunately, all our barbers are booked for 14:00 on Tuesday, 3 November.", reply);
        Assert.Contains("1. 13:00 – Barber 1", reply);
        Assert.Contains("Would you like me to book one of these?", reply);
    }

    [Fact]
    public async Task Cancellation_requires_explicit_confirmation()
    {
        await Say("Book me with Barber 3 on Saturday at 11:00");
        await Say("Ana");
        await Say("yes");

        var ask = await Say("Cancel my appointment");
        Assert.Contains("Just to confirm, you'd like to cancel this appointment?", ask);
        Assert.Contains("VF-2026-000001", ask);

        Assert.Contains("Sorry, I didn't quite get that.", await Say("hmm maybe"));
        Assert.Equal(BookingStatus.Confirmed, (await App.Db(db => db.Bookings.SingleAsync())).Status);

        var done = await Say("yes");
        Assert.Contains("has been cancelled", done);
        Assert.Equal(BookingStatus.Cancelled, (await App.Db(db => db.Bookings.SingleAsync())).Status);
        Assert.Contains(App.Meta.To(TestApp.OwnerNumber), r => r.Text.Contains("BOOKING CANCELLED"));
    }

    [Fact]
    public async Task Saying_no_keeps_the_booking()
    {
        await Say("Book me with Barber 3 on Saturday at 11:00");
        await Say("Ana");
        await Say("yes");
        await Say("cancel my booking");
        Assert.Contains("your appointment is still booked", await Say("no"));
        Assert.Equal(BookingStatus.Confirmed, (await App.Db(db => db.Bookings.SingleAsync())).Status);
    }

    [Fact]
    public async Task Customer_with_several_bookings_chooses_which_to_cancel()
    {
        await Say("Book me with Barber 1 on Saturday at 11:00");
        await Say("Ana");
        await Say("yes");
        await Say("Book me with Barber 1 on Sunday at 12:00");
        await Say("yes");

        var list = await Say("I want to cancel");
        Assert.Contains("Which appointment would you like to cancel?", list);
        Assert.Contains("2. Sunday, 8 November at 12:00", list);
        Assert.Contains("Sunday, 8 November at 12:00", await Say("2"));
        await Say("yes");
        var statuses = await App.Db(db => db.Bookings.OrderBy(b => b.Id).Select(b => b.Status).ToListAsync());
        Assert.Equal([BookingStatus.Confirmed, BookingStatus.Cancelled], statuses);
    }

    [Fact]
    public async Task Rescheduling_moves_the_existing_booking()
    {
        await Say("Book me with Barber 2 on Saturday at 14:00");
        await Say("Ana");
        await Say("yes");

        var ask = await Say("Move my booking to Friday");
        Assert.Contains("Let's move your appointment on Saturday, 7 November at 14:00 with Barber 2.", ask);
        Assert.Contains("What time would you prefer on Friday, 6 November?", ask);

        Assert.Contains("move your appointment from Saturday, 7 November at 14:00 to Friday, 6 November at 15:00 with Barber 2?", await Say("15:00"));
        var done = await Say("yes");
        Assert.Contains("Your appointment has been rescheduled!", done);
        Assert.Contains("Booking reference: VF-2026-000001", done);

        var booking = await App.Db(db => db.Bookings.SingleAsync());
        Assert.Equal(new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc), booking.StartAtUtc);
        Assert.Contains(App.Meta.To(TestApp.OwnerNumber), r => r.Text.Contains("BOOKING RESCHEDULED") && r.Text.Contains("Was: Saturday, 7 November 14:00 with Barber 2"));
    }

    [Fact]
    public async Task Human_request_pauses_the_bot_until_owner_resumes()
    {
        await Say("hi");
        var reply = await Say("Can I speak to someone?");
        Assert.Contains("someone will get back to you", reply);

        var conversation = await App.Db(db => db.Conversations.SingleAsync());
        Assert.True(conversation.RequiresAttention);
        Assert.Equal(ConversationState.HumanSupportRequested, conversation.CurrentState);
        Assert.Contains(App.Meta.To(TestApp.OwnerNumber), r => r.Text.Contains("CUSTOMER NEEDS HUMAN ASSISTANCE") && r.Text.Contains($"RESUME {conversation.Id}"));

        Assert.Empty(await App.SendAsync(Platform.WhatsApp, Customer, "hello? book me tomorrow"));

        var ownerReply = await App.SendAsync(Platform.WhatsApp, TestApp.OwnerNumber, $"RESUME {conversation.Id}");
        Assert.Contains("is back with the bot", Joined(ownerReply));
        Assert.Contains("Welcome", await Say("hi"));
    }

    [Fact]
    public async Task Owner_can_reply_to_a_customer_through_the_bot()
    {
        await Say("talk to a person");
        var id = (await App.Db(db => db.Conversations.SingleAsync())).Id;
        await App.SendAsync(Platform.WhatsApp, TestApp.OwnerNumber, $"REPLY {id} Hi, this is the owner. How can I help?");
        Assert.Contains(App.Meta.To(Customer), r => r.Text == "Hi, this is the owner. How can I help?");
    }

    [Fact]
    public async Task Owner_messages_never_reach_the_customer_chatbot()
    {
        var reply = await App.SendAsync(Platform.WhatsApp, TestApp.OwnerNumber, "Book me tomorrow at 10");
        Assert.Contains("RESUME <id>", Joined(reply));
        Assert.Equal(0, await App.Db(db => db.Conversations.CountAsync()));
    }

    [Fact]
    public async Task Two_misunderstandings_offer_human_help()
    {
        await Say("hi");
        Assert.Contains("Sorry, I didn't quite get that.", await Say("qwerty"));
        Assert.Contains("Would you like me to notify someone at the shop to assist you?", await Say("asdfgh"));
        Assert.Contains("someone will get back to you", await Say("yes"));
    }

    [Fact]
    public async Task Conversation_resumes_where_it_left_off()
    {
        await Say("I'd like to book with Barber 4");
        App.Time.Advance(TimeSpan.FromHours(3));
        var reply = await Say("Saturday");
        Assert.Contains("What time would you prefer on Saturday, 7 November?", reply);
        Assert.Contains("What name", await Say("10:00"));
        Assert.Contains("Would you like me to confirm your appointment for Saturday, 7 November at 10:00 with Barber 4?", await Say("Lee"));
    }

    [Fact]
    public async Task Stale_conversation_restarts_after_a_day()
    {
        await Say("I'd like to book");
        App.Time.Advance(TimeSpan.FromHours(25));
        Assert.Contains("Welcome", await Say("hello"));
    }

    [Fact]
    public async Task Past_dates_and_closed_hours_are_explained()
    {
        Assert.Contains("That date has already passed.", await Say("book me on 1 november at 10:00"));
        Assert.Contains("the last appointment starts at 18:00", await Say("tomorrow at 20:00"));
        Assert.Contains("Our appointments start on the hour", await Say("tomorrow at 14:30"));
    }

    [Fact]
    public async Task Declining_a_held_slot_releases_it()
    {
        await Say("Book me with Barber 1 tomorrow at 10:00");
        await Say("Kim");
        Assert.Contains("released that slot", await Say("no"));
        Assert.Equal(BookingStatus.Cancelled, (await App.Db(db => db.Bookings.SingleAsync())).Status);
    }

    [Fact]
    public async Task Interactive_button_reply_is_understood()
    {
        await Say("Book me with Barber 1 tomorrow at 10:00");
        await Say("Kim");
        var done = Joined(await App.SendAsync(Platform.WhatsApp, Customer, "Yes", replyPayload: "yes"));
        Assert.Contains("is confirmed", done);
        var sent = App.Meta.To(Customer);
        Assert.Contains(sent, r => r.Type == "interactive");
    }
}
