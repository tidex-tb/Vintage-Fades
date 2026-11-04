using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VintageFades.Application.Bookings;
using VintageFades.Application.Messaging;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class BookingServiceTests(TestApp app) : IntegrationTest(app)
{
    private static readonly DateOnly Saturday = new(2026, 11, 7);
    private static readonly TimeOnly Two = new(14, 0);

    private Task<BookingResult> Book(long customerId, DateOnly date, TimeOnly time, int? barberId = null, bool hold = false) =>
        App.WithScopeAsync(sp => sp.GetRequiredService<BookingService>()
            .CreateBookingAsync(new CreateBookingRequest(customerId, date, time, barberId, BookingSource.WhatsApp, hold), CancellationToken.None));

    private Task<T> Service<T>(Func<BookingService, Task<T>> work) => App.WithScopeAsync(sp => work(sp.GetRequiredService<BookingService>()));

    [Fact]
    public async Task Books_a_valid_available_slot_and_stores_utc()
    {
        var customer = await App.CreateCustomerAsync("John");
        var result = await Book(customer, Saturday, Two);

        Assert.True(result.Success);
        var booking = result.Booking!;
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc), booking.StartAtUtc);
        Assert.Equal(booking.StartAtUtc.AddHours(1), booking.EndAtUtc);
        Assert.Equal("VF-2026-000001", booking.BookingReference);
        var found = await Service(s => s.GetBookingByReferenceAsync("vf-2026-000001", CancellationToken.None));
        Assert.Equal(booking.Id, found!.Id);
    }

    [Fact]
    public async Task Books_a_specific_barber()
    {
        var customer = await App.CreateCustomerAsync("John");
        var result = await Book(customer, Saturday, Two, barberId: 3);
        Assert.Equal(3, result.Booking!.BarberId);
    }

    [Fact]
    public async Task Any_barber_spreads_load_instead_of_always_using_the_first()
    {
        var c = await App.CreateCustomerAsync("A");
        var first = await Book(c, Saturday, new TimeOnly(9, 0));
        var second = await Book(c, Saturday, new TimeOnly(10, 0));
        var third = await Book(c, Saturday, new TimeOnly(11, 0));
        Assert.Equal([1, 2, 3], new[] { first.Booking!.BarberId, second.Booking!.BarberId, third.Booking!.BarberId });
    }

    [Fact]
    public async Task Unavailable_barber_is_rejected_and_other_barbers_are_offered()
    {
        var a = await App.CreateCustomerAsync("A");
        var b = await App.CreateCustomerAsync("B");
        await Book(a, Saturday, Two, barberId: 2);

        var result = await Book(b, Saturday, Two, barberId: 2);
        Assert.Equal(BookingError.BarberUnavailable, result.Error);
        Assert.Equal(BarberUnavailableReason.Booked, result.BarberReason);

        var status = await Service(s => s.GetSlotStatusAsync(Saturday, Two, 2, null, CancellationToken.None));
        Assert.False(status.RequestedBarberAvailable);
        Assert.Equal([1, 3, 4], status.AvailableBarbers.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task Fifth_simultaneous_booking_is_refused_and_alternatives_are_close_to_the_request()
    {
        for (var i = 0; i < 4; i++) Assert.True((await Book(await App.CreateCustomerAsync($"C{i}"), Saturday, Two)).Success);

        var fifth = await Book(await App.CreateCustomerAsync("C5"), Saturday, Two);
        Assert.Equal(BookingError.NoAvailability, fifth.Error);

        var alternatives = await Service(s => s.GetAlternativeSlotsAsync(Saturday, Two, null, null, 5, CancellationToken.None));
        Assert.Equal(5, alternatives.Count);
        Assert.DoesNotContain(alternatives, a => a.Time == Two);
        Assert.Equal(new TimeOnly(13, 0), alternatives[0].Time); // nearest, earlier wins the tie
        Assert.True(alternatives.GroupBy(a => a.Time).All(g => g.Count() <= 2));
        Assert.All(alternatives, a => Assert.True(Math.Abs((a.Time.ToTimeSpan() - Two.ToTimeSpan()).TotalHours) <= 2));
    }

    [Fact]
    public async Task Alternatives_move_to_next_day_when_the_day_is_full()
    {
        var c = await App.CreateCustomerAsync("Full");
        foreach (var hour in Enumerable.Range(9, 10))
            for (var barber = 1; barber <= 4; barber++)
                Assert.True((await Book(c, Saturday, new TimeOnly(hour, 0), barber)).Success);

        var alternatives = await Service(s => s.GetAlternativeSlotsAsync(Saturday, Two, null, null, 5, CancellationToken.None));
        Assert.All(alternatives, a => Assert.Equal(Saturday.AddDays(1), a.Date));
    }

    [Theory]
    [InlineData("2026-11-01", "14:00", SlotRejection.InPast)]
    [InlineData("2026-11-07", "08:00", SlotRejection.BeforeOpening)]
    [InlineData("2026-11-07", "19:00", SlotRejection.AfterClosing)]
    [InlineData("2026-11-07", "14:30", SlotRejection.NotOnSlotBoundary)]
    [InlineData("2026-12-25", "10:00", SlotRejection.BeyondAdvanceWindow)]
    public async Task Rejects_invalid_slots(string date, string time, SlotRejection expected)
    {
        var result = await Book(await App.CreateCustomerAsync("X"), DateOnly.Parse(date), TimeOnly.Parse(time));
        Assert.Equal(BookingError.InvalidSlot, result.Error);
        Assert.Equal(expected, result.Rejection);
    }

    [Fact]
    public async Task Rejects_booking_during_barber_time_off()
    {
        await App.Db(async db =>
        {
            db.BarberTimeOff.Add(new BarberTimeOff { BarberId = 1, StartAtUtc = new DateTime(2026, 11, 7, 11, 0, 0, DateTimeKind.Utc), EndAtUtc = new DateTime(2026, 11, 7, 14, 0, 0, DateTimeKind.Utc), CreatedAtUtc = Now });
            return await db.SaveChangesAsync();
        });
        var result = await Book(await App.CreateCustomerAsync("X"), Saturday, Two, barberId: 1);
        Assert.Equal(BarberUnavailableReason.TimeOff, result.BarberReason);
        var anyone = await Book(await App.CreateCustomerAsync("Y"), Saturday, Two);
        Assert.NotEqual(1, anyone.Booking!.BarberId);
    }

    [Fact]
    public async Task Rejects_booking_outside_barber_working_hours()
    {
        await App.Db(async db =>
        {
            var hours = await db.BarberWorkingHours.Where(h => h.BarberId == 4 && h.DayOfWeek == DayOfWeek.Saturday).SingleAsync();
            hours.ClosingTime = new TimeOnly(13, 0);
            var sunday = await db.BarberWorkingHours.Where(h => h.BarberId == 4 && h.DayOfWeek == DayOfWeek.Sunday).SingleAsync();
            sunday.IsWorking = false;
            return await db.SaveChangesAsync();
        });
        var customer = await App.CreateCustomerAsync("X");
        Assert.Equal(BarberUnavailableReason.NotWorking, (await Book(customer, Saturday, Two, 4)).BarberReason);
        Assert.Equal(BarberUnavailableReason.NotWorking, (await Book(customer, Saturday.AddDays(1), new TimeOnly(10, 0), 4)).BarberReason);
        Assert.True((await Book(customer, Saturday, new TimeOnly(12, 0), 4)).Success);
    }

    [Fact]
    public async Task Cancelling_releases_the_slot_and_notifies_the_owner()
    {
        var a = await App.CreateCustomerAsync("A");
        var booking = (await Book(a, Saturday, Two, 2)).Booking!;

        var cancel = await Service(s => s.CancelBookingAsync(booking.Id, a, "test", enforceNotice: true, CancellationToken.None));
        Assert.True(cancel.Success);
        Assert.Equal(BookingStatus.Cancelled, cancel.Booking!.Status);
        Assert.NotNull(cancel.Booking.CancelledAtUtc);

        var again = await Book(await App.CreateCustomerAsync("B"), Saturday, Two, 2);
        Assert.True(again.Success);

        var ownerTypes = await App.Db(db => db.OutboxMessages.Where(o => o.EventType == OutboxEventTypes.OwnerNotification).Select(o => o.Payload).ToListAsync());
        Assert.Contains(ownerTypes, p => p.Contains("BOOKING CANCELLED"));
    }

    [Fact]
    public async Task Cancellation_is_refused_for_another_customer_and_inside_notice_period()
    {
        var owner = await App.CreateCustomerAsync("A");
        var other = await App.CreateCustomerAsync("B");
        var booking = (await Book(owner, new DateOnly(2026, 11, 2), new TimeOnly(9, 0))).Booking!; // starts in 1 hour

        Assert.Equal(BookingError.NotOwner, (await Service(s => s.CancelBookingAsync(booking.Id, other, "x", true, CancellationToken.None))).Error);
        Assert.Equal(BookingError.CancellationTooLate, (await Service(s => s.CancelBookingAsync(booking.Id, owner, "x", true, CancellationToken.None))).Error);
        Assert.True((await Service(s => s.CancelBookingAsync(booking.Id, null, "shop", enforceNotice: false, CancellationToken.None))).Success);
    }

    [Fact]
    public async Task Rescheduling_keeps_reference_and_frees_the_old_slot()
    {
        var a = await App.CreateCustomerAsync("A");
        var booking = (await Book(a, Saturday, Two, 2)).Booking!;

        var moved = await Service(s => s.RescheduleBookingAsync(booking.Id, a, Saturday.AddDays(1), new TimeOnly(10, 0), null, true, CancellationToken.None));
        Assert.True(moved.Success);
        Assert.Equal(booking.BookingReference, moved.Booking!.BookingReference);
        Assert.Equal(2, moved.Booking.BarberId); // current barber preferred
        Assert.Equal(new DateTime(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc), moved.Booking.StartAtUtc);

        Assert.True((await Book(await App.CreateCustomerAsync("B"), Saturday, Two, 2)).Success);
    }

    [Fact]
    public async Task Failed_reschedule_leaves_the_original_booking_intact()
    {
        var a = await App.CreateCustomerAsync("A");
        var b = await App.CreateCustomerAsync("B");
        var mine = (await Book(a, Saturday, Two, 2)).Booking!;
        await Book(b, Saturday, new TimeOnly(15, 0), 3);

        var result = await Service(s => s.RescheduleBookingAsync(mine.Id, a, Saturday, new TimeOnly(15, 0), 3, true, CancellationToken.None));
        Assert.Equal(BookingError.BarberUnavailable, result.Error);

        var stored = await App.Db(db => db.Bookings.AsNoTracking().SingleAsync(x => x.Id == mine.Id));
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(2, stored.BarberId);
        Assert.Equal(mine.StartAtUtc, stored.StartAtUtc);
    }

    [Fact]
    public async Task Database_constraint_rejects_overlapping_active_bookings_even_without_the_service()
    {
        var c = await App.CreateCustomerAsync("A");
        await Book(c, Saturday, Two, 1);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => App.Db(async db =>
        {
            db.Bookings.Add(new Booking
            {
                BookingReference = "VF-2026-999999", CustomerId = c, BarberId = 1, Status = BookingStatus.Confirmed, SourcePlatform = BookingSource.Admin,
                StartAtUtc = new DateTime(2026, 11, 7, 12, 30, 0, DateTimeKind.Utc), EndAtUtc = new DateTime(2026, 11, 7, 13, 30, 0, DateTimeKind.Utc),
                CreatedAtUtc = Now, UpdatedAtUtc = Now
            });
            return await db.SaveChangesAsync();
        }));
        Assert.Contains("EX_Bookings_Barber_NoOverlap", ex.InnerException!.Message);
    }

    [Fact]
    public async Task Simultaneous_requests_for_the_same_barber_produce_exactly_one_booking()
    {
        var customers = new List<long>();
        for (var i = 0; i < 10; i++) customers.Add(await App.CreateCustomerAsync($"C{i}"));

        var results = await Task.WhenAll(customers.Select(c => Task.Run(() => Book(c, Saturday, Two, barberId: 1))));

        Assert.Single(results, r => r.Success);
        Assert.All(results.Where(r => !r.Success), r => Assert.Contains(r.Error, new[] { BookingError.SlotTaken, BookingError.BarberUnavailable }));
        Assert.Equal(1, await App.Db(db => db.Bookings.CountAsync(b => b.BarberId == 1 && b.Status == BookingStatus.Confirmed)));
    }

    [Fact]
    public async Task Simultaneous_any_barber_requests_never_exceed_four_bookings_per_slot()
    {
        var customers = new List<long>();
        for (var i = 0; i < 12; i++) customers.Add(await App.CreateCustomerAsync($"C{i}"));

        var results = await Task.WhenAll(customers.Select(c => Task.Run(() => Book(c, Saturday, Two))));

        Assert.Equal(4, results.Count(r => r.Success));
        Assert.Equal(4, results.Where(r => r.Success).Select(r => r.Booking!.BarberId).Distinct().Count());
        Assert.Equal(4, await App.Db(db => db.Bookings.CountAsync(b => b.Status == BookingStatus.Confirmed)));
    }

    [Fact]
    public async Task Hold_blocks_others_until_it_expires_then_is_released()
    {
        var a = await App.CreateCustomerAsync("A");
        var b = await App.CreateCustomerAsync("B");
        var hold = await Book(a, Saturday, Two, 1, hold: true);
        Assert.Equal(BookingStatus.Pending, hold.Booking!.Status);
        Assert.NotNull(hold.Booking.HoldExpiresAtUtc);

        Assert.Equal(BookingError.BarberUnavailable, (await Book(b, Saturday, Two, 1)).Error);

        App.Time.Advance(TimeSpan.FromMinutes(11));
        var taken = await Book(b, Saturday, Two, 1);
        Assert.True(taken.Success);

        var expired = await App.Db(db => db.Bookings.AsNoTracking().SingleAsync(x => x.Id == hold.Booking.Id));
        Assert.Equal(BookingStatus.Cancelled, expired.Status);
        Assert.Equal(BookingService.HoldExpiredReason, expired.CancellationReason);

        var confirmLate = await Service(s => s.ConfirmBookingAsync(hold.Booking.Id, CancellationToken.None));
        Assert.Equal(BookingError.SlotTaken, confirmLate.Error);
    }

    [Fact]
    public async Task Expired_hold_is_reacquired_on_confirm_when_still_free()
    {
        var a = await App.CreateCustomerAsync("A");
        var hold = (await Book(a, Saturday, Two, 1, hold: true)).Booking!;
        App.Time.Advance(TimeSpan.FromMinutes(30));
        await Service(s => s.ReleaseExpiredHoldsAsync(CancellationToken.None));

        var confirmed = await Service(s => s.ConfirmBookingAsync(hold.Id, CancellationToken.None));
        Assert.True(confirmed.Success);
        Assert.Equal(BookingStatus.Confirmed, confirmed.Booking!.Status);
        Assert.Equal(hold.BookingReference, confirmed.Booking.BookingReference);
    }

    [Fact]
    public async Task Hours_are_evaluated_in_windhoek_time_across_midnight()
    {
        // 22:30 UTC on Tuesday 3 Nov is 00:30 on Wednesday 4 Nov in Windhoek.
        App.Time.SetUtcNow(new DateTimeOffset(2026, 11, 3, 22, 30, 0, TimeSpan.Zero));
        var c = await App.CreateCustomerAsync("Night");

        var slots = await Service(s => s.GetAvailableSlotsAsync(new DateOnly(2026, 11, 4), null, null, CancellationToken.None));
        Assert.Equal(40, slots.Count);
        Assert.Equal(new TimeOnly(9, 0), slots.Min(s => s.Time));

        var earlyToday = await Book(c, new DateOnly(2026, 11, 4), new TimeOnly(9, 0));
        Assert.Equal(new DateTime(2026, 11, 4, 7, 0, 0, DateTimeKind.Utc), earlyToday.Booking!.StartAtUtc);

        var yesterday = await Book(c, new DateOnly(2026, 11, 3), new TimeOnly(18, 0));
        Assert.Equal(SlotRejection.InPast, yesterday.Rejection);
    }
}
