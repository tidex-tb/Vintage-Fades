using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VintageFades.Application.Operations;
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
        Assert.Contains("Who would you like to book with?", await Say("1"));
        Assert.Contains("What date would you like to come in?", await Say("1"));
    }

    [Fact]
    public async Task Full_booking_conversation_confirms_only_after_database_commit()
    {
        var ask = await Say("Hi, I need a haircut.");
        Assert.Contains("Who would you like to book with?", ask);
        Assert.Contains("1. First available", ask);
        Assert.Contains("2. Kapza", ask);
        Assert.Contains("5. Tevohn", ask);
        Assert.Contains("What date would you like to come in?", await Say("1"));

        var slots = await Say("Tomorrow.");
        Assert.Contains("Here are the open slots on Tuesday, 3 November:", slots);
        Assert.Contains("1. 09:00 – 10:00", slots);
        Assert.Contains("10. 18:00 – 19:00", slots);
        Assert.Contains("Reply with the number of the slot you'd like.", slots);

        // The customer picks a slot by its number: 6 is 14:00 – 15:00, not 06:00.
        var held = await Say("6");
        Assert.Contains("14:00 on Tuesday, 3 November with Kapza is available", held);
        Assert.Contains("What name should I put the booking under?", held);
        Assert.Equal(BookingStatus.Pending, (await App.Db(db => db.Bookings.SingleAsync())).Status);

        var confirm = await Say("John");
        Assert.Contains("Would you like me to confirm your appointment for Tuesday, 3 November at 14:00 with Kapza?", confirm);

        var done = await Say("Yes.");
        Assert.Contains("Your appointment at VINTAGE FADES BARBER SHOP is confirmed!", done);
        Assert.Contains("Booking reference: VF-2026-000001", done);
        Assert.Contains("Date: Tuesday, 3 November 2026", done);
        Assert.Contains("Time: 14:00", done);
        Assert.Contains("Barber: Kapza", done);
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
        var reply = await Say("Book me with Luther tomorrow at 15:00.");
        Assert.DoesNotContain("What date", reply);
        Assert.DoesNotContain("What time", reply);
        Assert.DoesNotContain("specific barber", reply);
        Assert.Contains("15:00 on Tuesday, 3 November with Luther is available", reply);
        Assert.Contains("What name", reply);

        // A returning customer is not asked for their name again.
        await Say("John");
        await Say("yes");
        var second = await Say("Book me with Luther on Saturday at 10am");
        Assert.DoesNotContain("What name", second);
        Assert.Contains("Would you like me to confirm your appointment for Saturday, 7 November at 10:00 with Luther?", second);
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
        await Say("1");
        Assert.Contains("with Kapza is available", await Say("2")); // slot 2 is 10:00 – 11:00
    }

    [Fact]
    public async Task Barber_can_be_named_while_choosing_a_slot_or_confirming()
    {
        await Say("Can I book tomorrow?");
        var luther = await Say("3"); // barber list: 1 first available, 2 Kapza, 3 Luther
        Assert.Contains("Here are the open slots on Tuesday, 3 November with Luther:", luther);
        await Say("1");
        await Say("Ana");
        var confirm = await Say("actually Godwin please");
        Assert.Contains("with Godwin", confirm);
    }

    [Fact]
    public async Task Slot_number_and_barber_name_in_one_message_are_both_used()
    {
        await Say("Can I book tomorrow?");
        await Say("1");
        var reply = await Say("9 Luther");
        Assert.Contains("17:00 on Tuesday, 3 November with Luther is available", reply);
        Assert.Contains("What name", reply);
    }

    [Fact]
    public async Task Hi_in_the_middle_of_a_booking_continues_it_and_restart_starts_over()
    {
        await Say("Can I book tomorrow?");
        var hi = await Say("hi");
        Assert.Contains("Let's pick up where we left off.", hi);
        Assert.Contains("Who would you like to book with?", hi);
        Assert.DoesNotContain("didn't quite get that", hi);

        await Say("1");
        Assert.Contains("What would you like to do?", await Say("lets restart"));
        Assert.Equal(0, await App.Db(db => db.Bookings.CountAsync(b => b.Status == BookingStatus.Pending)));
    }

    private Task<int> RunIdleCheckAsync() => App.WithScopeAsync(sp => sp.GetRequiredService<IdleConversationService>().ProcessAsync(CancellationToken.None));

    [Fact]
    public async Task Quiet_customer_is_asked_to_continue_and_a_yes_resumes_without_confirming_anything()
    {
        await Say("Book me tomorrow at 14:00");
        await Say("1");
        await Say("Sam"); // now waiting on the confirmation
        Assert.Equal(BookingStatus.Pending, (await App.Db(db => db.Bookings.SingleAsync())).Status);

        App.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, await RunIdleCheckAsync());

        App.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await RunIdleCheckAsync());
        await App.DispatchAsync();
        Assert.Contains(App.Meta.To(Customer), r => r.Text.Contains("Are you still there?"));
        Assert.Equal(0, await RunIdleCheckAsync()); // asked only once

        var reply = await Say("yes");
        Assert.Contains("Great, let's carry on.", reply);
        Assert.Contains("reply *yes* to confirm", reply);
        Assert.DoesNotContain("is confirmed", reply);
        Assert.NotEqual(BookingStatus.Confirmed, (await App.Db(db => db.Bookings.SingleAsync())).Status);

        App.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, await RunIdleCheckAsync()); // quiet again: a fresh question, not a reset
    }

    [Fact]
    public async Task Customer_who_ignores_the_question_has_the_session_reset_and_the_hold_released()
    {
        await Say("Book me tomorrow at 14:00");
        await Say("1");
        await Say("Sam");

        App.Time.Advance(TimeSpan.FromMinutes(11));
        await RunIdleCheckAsync();
        App.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, await RunIdleCheckAsync());

        App.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await RunIdleCheckAsync());
        await App.DispatchAsync();
        Assert.Contains(App.Meta.To(Customer), r => r.Text.Contains("closed your booking request"));

        var conversation = await App.Db(db => db.Conversations.SingleAsync());
        Assert.Equal(ConversationState.Idle, conversation.CurrentState);
        Assert.Equal(BookingStatus.Cancelled, (await App.Db(db => db.Bookings.SingleAsync())).Status);
        Assert.Equal(0, await RunIdleCheckAsync());
        Assert.Contains("Welcome", await Say("hi"));
    }

    [Fact]
    public async Task Customers_not_in_the_middle_of_a_booking_are_left_alone()
    {
        await Say("hi"); // menu only
        App.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await RunIdleCheckAsync());
    }

    [Fact]
    public async Task Price_hours_and_location_questions_are_answered_and_the_booking_continues()
    {
        await Say("Can I book tomorrow?");
        await Say("1");
        var reply = await Say("how much is a taper with go black");
        Assert.Contains("Adult's cut + go black – N$250", reply);
        Assert.Contains("Full black dye – N$100", reply);
        Assert.Contains("Back to your booking: Reply with the number of the slot", reply);
        Assert.DoesNotContain("didn't quite get that", reply);

        Assert.Contains("with Kapza is available", await Say("3"));
    }

    [Fact]
    public async Task First_messages_without_a_greeting_get_a_direct_answer_and_the_menu()
    {
        // Meta's default first message on a click-to-WhatsApp ad.
        var lead = await Say("Hello! Can I get more info on this?", "264810000001");
        Assert.Contains("Haircut services", lead);
        Assert.Contains("Massage services", lead);
        Assert.Contains("1. Book an appointment", lead);

        var price = await Say("How much is a kids cut?", "264810000002");
        Assert.Contains("Kids cut (below 11) – N$100", price);
        Assert.DoesNotContain("Welcome to", price); // answered the question instead of a generic welcome
        Assert.Contains("1. Book an appointment", price);

        var where = await Say("where are you located", "264810000003");
        Assert.Contains("26 Van Rhijn Street", where);

        var facial = await Say("do you guys do facials", "264810000004");
        Assert.Contains("30 minute express facial for men – N$500", facial);

        var hours = await Say("what time do you open?", "264810000005");
        Assert.Contains("Opening hours: 09:00 – 19:00", hours);

        // None of that started a booking, and the customer can carry on with the menu.
        Assert.Equal(0, await App.Db(db => db.Bookings.CountAsync()));
        Assert.Contains("Who would you like to book with?", await Say("1", "264810000002"));
    }

    [Fact]
    public async Task A_price_question_after_a_booking_request_does_not_lose_the_booking_details()
    {
        await Say("I want a haircut tomorrow at 14:00"); // no barber yet, so the bot asks for one
        var reply = await Say("how much is it?");
        Assert.Contains("Haircut services", reply);
        Assert.Contains("Back to your booking: Who would you like to book with?", reply);

        // The day and time given earlier are still remembered.
        Assert.Contains("14:00 on Tuesday, 3 November with Kapza is available", await Say("1"));
    }

    [Fact]
    public async Task A_name_at_the_name_step_is_not_mistaken_for_a_service()
    {
        await Say("Book me with Kapza tomorrow at 14:00");
        var reply = await Say("Hollywood"); // a surname that happens to be a service name
        Assert.Contains("confirm your appointment", reply);
        Assert.DoesNotContain("N$", reply);
    }

    [Fact]
    public async Task Declining_the_human_offer_and_saying_what_you_want_starts_that_request()
    {
        await Say("hi");
        await Say("qwerty");
        Assert.Contains("Would you like me to notify someone", await Say("asdfgh"));
        var reply = await Say("no i want to book a haircut");
        Assert.Contains("Who would you like to book with?", reply);
        Assert.DoesNotContain("let's keep going", reply);
    }

    [Fact]
    public async Task Ambiguous_next_weekday_is_confirmed_before_use()
    {
        Assert.Contains("Who would you like to book with?", await Say("I need a slot next friday"));
        var reply = await Say("1");
        Assert.Contains("do you mean Friday, 6 November or Friday, 13 November?", reply);
        Assert.Contains("Here are the open slots on Friday, 13 November:", await Say("2"));
    }

    [Fact]
    public async Task Next_week_asks_which_day()
    {
        await Say("can i come in next week");
        var reply = await Say("1");
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
        await Say("Are you free tomorrow?");
        var reply = await Say("1");
        Assert.Contains("Here are the open slots on Tuesday, 3 November:", reply);
        Assert.Contains("1. 10:00 – 11:00", reply);
        Assert.DoesNotContain("09:00 –", reply);
    }

    /// <summary>Books every barber for every slot on the given local date, so the date is fully booked.</summary>
    private Task FillDayAsync(DateOnly date) => App.Db(async db =>
    {
        var c = new Customer { FullName = "Other", CreatedAtUtc = Now, UpdatedAtUtc = Now };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        var n = 0;
        for (var barber = 1; barber <= 4; barber++)
            for (var hour = 9; hour < 19; hour++)
            {
                var start = new DateTime(date.Year, date.Month, date.Day, hour - 2, 0, 0, DateTimeKind.Utc); // Africa/Windhoek is UTC+2
                db.Bookings.Add(new Booking
                {
                    BookingReference = $"VF-2026-8{++n:D5}", CustomerId = c.Id, BarberId = barber, Status = BookingStatus.Confirmed, SourcePlatform = BookingSource.Admin,
                    StartAtUtc = start, EndAtUtc = start.AddHours(1), CreatedAtUtc = Now, UpdatedAtUtc = Now
                });
            }
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Fully_booked_date_offers_other_dates_and_then_their_slots()
    {
        await FillDayAsync(new DateOnly(2026, 11, 3));

        await Say("Book me tomorrow");
        var reply = await Say("1");
        Assert.Contains("We're fully booked on Tuesday, 3 November.", reply);
        Assert.Contains("These dates still have open slots:", reply);
        Assert.Contains("1. Wednesday, 4 November", reply);

        var slots = await Say("1");
        Assert.Contains("Here are the open slots on Wednesday, 4 November:", slots);
        Assert.Contains("1. 09:00 – 10:00", slots);
    }

    [Fact]
    public async Task Unavailable_barber_gets_other_barbers_at_the_same_time()
    {
        await Say("Book me with Luther on Saturday at 14:00");
        await Say("Sam");
        await Say("yes");

        const string other = "264819999999";
        var reply = await Say("Is Luther available on Saturday at 2pm?", other);
        Assert.Contains("Luther is already booked at 14:00 on Saturday, 7 November.", reply);
        Assert.Contains("We can offer you:", reply);
        Assert.Contains("1. 14:00 – Kapza", reply);

        var pick = await Say("1", other);
        Assert.Contains("14:00 on Saturday, 7 November with Kapza is available", pick);
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

        await Say("Can I get a haircut at 14:00 tomorrow?");
        var reply = await Say("1");
        Assert.Contains("Unfortunately, all our barbers are booked for 14:00 on Tuesday, 3 November.", reply);
        Assert.Contains("Here are the open slots on Tuesday, 3 November:", reply);
        Assert.Contains("13:00 – 14:00", reply);
        Assert.DoesNotContain("14:00 – 15:00", reply);
    }

    [Fact]
    public async Task Cancellation_requires_explicit_confirmation()
    {
        await Say("Book me with Godwin on Saturday at 11:00");
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
        await Say("Book me with Godwin on Saturday at 11:00");
        await Say("Ana");
        await Say("yes");
        await Say("cancel my booking");
        Assert.Contains("your appointment is still booked", await Say("no"));
        Assert.Equal(BookingStatus.Confirmed, (await App.Db(db => db.Bookings.SingleAsync())).Status);
    }

    [Fact]
    public async Task Customer_with_several_bookings_chooses_which_to_cancel()
    {
        await Say("Book me with Kapza on Saturday at 11:00");
        await Say("Ana");
        await Say("yes");
        await Say("Book me with Kapza on Sunday at 12:00");
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
        await Say("Book me with Luther on Saturday at 14:00");
        await Say("Ana");
        await Say("yes");

        var ask = await Say("Move my booking to Friday");
        Assert.Contains("Let's move your appointment on Saturday, 7 November at 14:00 with Luther.", ask);
        Assert.Contains("Here are the open slots on Friday, 6 November with Luther:", ask);

        Assert.Contains("move your appointment from Saturday, 7 November at 14:00 to Friday, 6 November at 15:00 with Luther?", await Say("15:00"));
        var done = await Say("yes");
        Assert.Contains("Your appointment has been rescheduled!", done);
        Assert.Contains("Booking reference: VF-2026-000001", done);

        var booking = await App.Db(db => db.Bookings.SingleAsync());
        Assert.Equal(new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc), booking.StartAtUtc);
        Assert.Contains(App.Meta.To(TestApp.OwnerNumber), r => r.Text.Contains("BOOKING RESCHEDULED") && r.Text.Contains("Was: Saturday, 7 November 14:00 with Luther"));
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
        await Say("I'd like to book with Tevohn");
        App.Time.Advance(TimeSpan.FromHours(3));
        var reply = await Say("Saturday");
        Assert.Contains("Here are the open slots on Saturday, 7 November with Tevohn:", reply);
        Assert.Contains("What name", await Say("1")); // slot 1 is 09:00 – 10:00
        Assert.Contains("Would you like me to confirm your appointment for Saturday, 7 November at 09:00 with Tevohn?", await Say("Lee"));
    }

    [Fact]
    public async Task Human_request_hands_the_chat_back_to_the_bot_after_the_pause()
    {
        await Say("hi");
        Assert.Contains("someone will get back to you", await Say("Can I speak to someone?"));
        Assert.Contains(App.Meta.To(TestApp.OwnerNumber), r => r.Text.Contains("stays quiet in this chat for 10 minutes"));

        App.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty(await App.SendAsync(Platform.WhatsApp, Customer, "hello?"));

        App.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains("Welcome", await Say("hi"));
        Assert.False((await App.Db(db => db.Conversations.SingleAsync())).CurrentState == ConversationState.HumanSupportRequested);
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
        Assert.Contains("That date has already passed.", await Say("book me on 1 november at 10:00, anyone"));
        Assert.Contains("the last appointment starts at 18:00", await Say("tomorrow at 20:00"));
        Assert.Contains("Our appointments start on the hour", await Say("tomorrow at 14:30"));
    }

    [Fact]
    public async Task Declining_a_held_slot_releases_it()
    {
        await Say("Book me with Kapza tomorrow at 10:00");
        await Say("Kim");
        Assert.Contains("released that slot", await Say("no"));
        Assert.Equal(BookingStatus.Cancelled, (await App.Db(db => db.Bookings.SingleAsync())).Status);
    }

    [Fact]
    public async Task Interactive_button_reply_is_understood()
    {
        await Say("Book me with Kapza tomorrow at 10:00");
        await Say("Kim");
        var done = Joined(await App.SendAsync(Platform.WhatsApp, Customer, "Yes", replyPayload: "yes"));
        Assert.Contains("is confirmed", done);
        var sent = App.Meta.To(Customer);
        Assert.Contains(sent, r => r.Type == "interactive");
    }
}
