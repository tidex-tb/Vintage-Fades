namespace VintageFades.Domain;

public enum SlotRejection
{
    None,
    InPast,
    BeyondAdvanceWindow,
    BeforeOpening,
    AfterClosing,
    EndsAfterClosing,
    NotOnSlotBoundary
}

public enum BarberUnavailableReason
{
    None,
    Inactive,
    NotWorking,
    TimeOff,
    Booked
}

/// <summary>
/// Shop calendar rules derived from <see cref="ShopSettings"/>: local/UTC conversion and slot generation.
/// All business rules about "when can an appointment start" live here, independent of any channel.
/// </summary>
public sealed class ShopCalendar
{
    public ShopCalendar(ShopSettings settings)
    {
        if (settings.AppointmentDurationMinutes <= 0)
            throw new ArgumentException("Appointment duration must be positive.", nameof(settings));
        if (settings.ClosingTime <= settings.OpeningTime)
            throw new ArgumentException("Closing time must be after opening time.", nameof(settings));

        Settings = settings;
        Zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        Duration = TimeSpan.FromMinutes(settings.AppointmentDurationMinutes);
        SlotStartTimes = BuildSlots(settings.OpeningTime, settings.ClosingTime, Duration);
    }

    public ShopSettings Settings { get; }
    public TimeZoneInfo Zone { get; }
    public TimeSpan Duration { get; }
    public IReadOnlyList<TimeOnly> SlotStartTimes { get; }

    public DateTime ToUtc(DateOnly date, TimeOnly time) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), Zone);

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public DateOnly Today(DateTime nowUtc) => DateOnly.FromDateTime(ToLocal(nowUtc));

    public DateOnly LastBookableDate(DateTime nowUtc) => Today(nowUtc).AddDays(Settings.BookingAdvanceDays);

    /// <summary>UTC range [start, end) covering one local calendar day.</summary>
    public (DateTime StartUtc, DateTime EndUtc) DayRangeUtc(DateOnly date) =>
        (ToUtc(date, TimeOnly.MinValue), ToUtc(date.AddDays(1), TimeOnly.MinValue));

    public SlotRejection ValidateSlot(DateOnly date, TimeOnly time, DateTime nowUtc)
    {
        if (date < Today(nowUtc)) return SlotRejection.InPast;
        if (date > LastBookableDate(nowUtc)) return SlotRejection.BeyondAdvanceWindow;
        if (time < Settings.OpeningTime) return SlotRejection.BeforeOpening;
        if (time >= Settings.ClosingTime) return SlotRejection.AfterClosing;
        if (time.Add(Duration) > Settings.ClosingTime || time.Add(Duration) < time) return SlotRejection.EndsAfterClosing;
        if (!SlotStartTimes.Contains(time)) return SlotRejection.NotOnSlotBoundary;
        if (ToUtc(date, time) <= nowUtc) return SlotRejection.InPast;
        return SlotRejection.None;
    }

    private static List<TimeOnly> BuildSlots(TimeOnly opening, TimeOnly closing, TimeSpan duration)
    {
        var slots = new List<TimeOnly>();
        var start = opening.ToTimeSpan();
        while (start + duration <= closing.ToTimeSpan())
        {
            slots.Add(TimeOnly.FromTimeSpan(start));
            start += duration;
        }
        return slots;
    }
}

/// <summary>Pure availability rule for one barber and one slot. The database exclusion constraint is the final guard.</summary>
public static class BarberAvailability
{
    public static BarberUnavailableReason Check(
        Barber barber,
        DateOnly localDate,
        TimeOnly localStart,
        TimeOnly localEnd,
        DateTime startUtc,
        DateTime endUtc,
        IEnumerable<BarberTimeOff> timeOff,
        IEnumerable<Booking> bookings,
        DateTime nowUtc,
        long? ignoreBookingId = null)
    {
        if (!barber.IsActive) return BarberUnavailableReason.Inactive;

        var hours = barber.WorkingHours.FirstOrDefault(h => h.DayOfWeek == localDate.DayOfWeek);
        if (hours is null || !hours.IsWorking || localStart < hours.OpeningTime || localEnd > hours.ClosingTime)
            return BarberUnavailableReason.NotWorking;

        if (timeOff.Any(t => t.BarberId == barber.Id && t.StartAtUtc < endUtc && t.EndAtUtc > startUtc))
            return BarberUnavailableReason.TimeOff;

        if (bookings.Any(b => b.BarberId == barber.Id && b.Id != ignoreBookingId && b.OccupiesSlotAt(nowUtc)
                              && b.StartAtUtc < endUtc && b.EndAtUtc > startUtc))
            return BarberUnavailableReason.Booked;

        return BarberUnavailableReason.None;
    }
}
