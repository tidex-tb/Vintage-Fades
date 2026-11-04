using VintageFades.Domain;

namespace VintageFades.UnitTests;

public class ShopCalendarTests
{
    private static ShopSettings Settings() => new()
    {
        ShopName = "VINTAGE FADES BARBER SHOP",
        TimeZoneId = "Africa/Windhoek",
        OpeningTime = new TimeOnly(9, 0),
        ClosingTime = new TimeOnly(19, 0),
        AppointmentDurationMinutes = 60,
        BookingAdvanceDays = 30,
        CancellationNoticeHours = 2
    };

    // 06:00 UTC = 08:00 in Windhoek (UTC+2), Monday 2 November 2026
    private static readonly DateTime Now = new(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Generates_ten_hourly_slots_with_last_start_at_18()
    {
        var calendar = new ShopCalendar(Settings());
        Assert.Equal(10, calendar.SlotStartTimes.Count);
        Assert.Equal(new TimeOnly(9, 0), calendar.SlotStartTimes[0]);
        Assert.Equal(new TimeOnly(18, 0), calendar.SlotStartTimes[^1]);
    }

    [Theory]
    [InlineData("2026-11-03", "09:00", SlotRejection.None)]
    [InlineData("2026-11-03", "18:00", SlotRejection.None)]
    [InlineData("2026-11-01", "10:00", SlotRejection.InPast)]
    [InlineData("2026-11-03", "08:00", SlotRejection.BeforeOpening)]
    [InlineData("2026-11-03", "19:00", SlotRejection.AfterClosing)]
    [InlineData("2026-11-03", "18:30", SlotRejection.EndsAfterClosing)]
    [InlineData("2026-11-03", "14:30", SlotRejection.NotOnSlotBoundary)]
    [InlineData("2026-12-03", "10:00", SlotRejection.BeyondAdvanceWindow)]
    public void Validates_slots(string date, string time, SlotRejection expected) =>
        Assert.Equal(expected, new ShopCalendar(Settings()).ValidateSlot(DateOnly.Parse(date), TimeOnly.Parse(time), Now));

    [Fact]
    public void Rejects_a_slot_earlier_today()
    {
        var calendar = new ShopCalendar(Settings());
        var noonLocal = new DateTime(2026, 11, 2, 10, 30, 0, DateTimeKind.Utc); // 12:30 local
        Assert.Equal(SlotRejection.InPast, calendar.ValidateSlot(new DateOnly(2026, 11, 2), new TimeOnly(12, 0), noonLocal));
        Assert.Equal(SlotRejection.None, calendar.ValidateSlot(new DateOnly(2026, 11, 2), new TimeOnly(13, 0), noonLocal));
    }

    [Fact]
    public void Converts_windhoek_local_time_to_utc()
    {
        var calendar = new ShopCalendar(Settings());
        Assert.Equal(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc), calendar.ToUtc(new DateOnly(2026, 11, 7), new TimeOnly(14, 0)));
        Assert.Equal(new DateTime(2026, 11, 7, 14, 0, 0), calendar.ToLocal(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Today_follows_the_shop_time_zone_around_midnight()
    {
        var calendar = new ShopCalendar(Settings());
        // 22:30 UTC on 2 Nov is already 00:30 on 3 Nov in Windhoek.
        Assert.Equal(new DateOnly(2026, 11, 3), calendar.Today(new DateTime(2026, 11, 2, 22, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 11, 2), calendar.Today(new DateTime(2026, 11, 2, 21, 59, 0, DateTimeKind.Utc)));
        var (start, end) = calendar.DayRangeUtc(new DateOnly(2026, 11, 3));
        Assert.Equal(new DateTime(2026, 11, 2, 22, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 11, 3, 22, 0, 0, DateTimeKind.Utc), end);
    }
}

public class BarberAvailabilityTests
{
    private static readonly DateOnly Date = new(2026, 11, 7); // Saturday
    private static readonly DateTime Start = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc);

    private static Barber Barber(bool active = true, bool working = true) => new()
    {
        Id = 1, FirstName = "Barber", DisplayName = "Barber 1", StationNumber = 1, IsActive = active,
        WorkingHours = [new BarberWorkingHours { BarberId = 1, DayOfWeek = DayOfWeek.Saturday, OpeningTime = new(9, 0), ClosingTime = new(19, 0), IsWorking = working }]
    };

    private static BarberUnavailableReason Check(Barber barber, IEnumerable<BarberTimeOff>? timeOff = null, IEnumerable<Booking>? bookings = null) =>
        BarberAvailability.Check(barber, Date, new TimeOnly(14, 0), new TimeOnly(15, 0), Start, Start.AddHours(1), timeOff ?? [], bookings ?? [], Now);

    private static Booking Booking(BookingStatus status, DateTime? holdExpires = null) => new()
    {
        Id = 9, BookingReference = "VF-2026-000009", BarberId = 1, StartAtUtc = Start, EndAtUtc = Start.AddHours(1), Status = status, HoldExpiresAtUtc = holdExpires
    };

    [Fact] public void Available_when_free() => Assert.Equal(BarberUnavailableReason.None, Check(Barber()));
    [Fact] public void Inactive_barber() => Assert.Equal(BarberUnavailableReason.Inactive, Check(Barber(active: false)));
    [Fact] public void Not_working_that_day() => Assert.Equal(BarberUnavailableReason.NotWorking, Check(Barber(working: false)));

    [Fact]
    public void Time_off_blocks_overlapping_slot() =>
        Assert.Equal(BarberUnavailableReason.TimeOff, Check(Barber(), [new BarberTimeOff { BarberId = 1, StartAtUtc = Start.AddMinutes(-30), EndAtUtc = Start.AddMinutes(30) }]));

    [Fact] public void Confirmed_booking_blocks() => Assert.Equal(BarberUnavailableReason.Booked, Check(Barber(), bookings: [Booking(BookingStatus.Confirmed)]));
    [Fact] public void Active_hold_blocks() => Assert.Equal(BarberUnavailableReason.Booked, Check(Barber(), bookings: [Booking(BookingStatus.Pending, Now.AddMinutes(5))]));
    [Fact] public void Expired_hold_does_not_block() => Assert.Equal(BarberUnavailableReason.None, Check(Barber(), bookings: [Booking(BookingStatus.Pending, Now.AddMinutes(-1))]));
    [Fact] public void Cancelled_booking_does_not_block() => Assert.Equal(BarberUnavailableReason.None, Check(Barber(), bookings: [Booking(BookingStatus.Cancelled)]));
}
