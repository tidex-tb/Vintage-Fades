using VintageFades.Domain;

namespace VintageFades.Application.Bookings;

public enum BookingError
{
    None,
    InvalidSlot,
    BarberNotFound,
    BarberUnavailable,
    NoAvailability,
    /// <summary>Lost a concurrency race: another booking committed first.</summary>
    SlotTaken,
    NotFound,
    NotOwner,
    InvalidStatus,
    CancellationTooLate
}

public sealed record SlotOption(DateOnly Date, TimeOnly Time, int BarberId, string BarberName);

public sealed record BookingResult(
    Booking? Booking,
    BookingError Error,
    SlotRejection Rejection = SlotRejection.None,
    BarberUnavailableReason BarberReason = BarberUnavailableReason.None)
{
    public bool Success => Error == BookingError.None;
    public static BookingResult Ok(Booking booking) => new(booking, BookingError.None);
    public static BookingResult Fail(BookingError error, Booking? booking = null) => new(booking, error);
    public static BookingResult InvalidSlot(SlotRejection rejection) => new(null, BookingError.InvalidSlot, rejection);
    public static BookingResult Unavailable(BarberUnavailableReason reason) => new(null, BookingError.BarberUnavailable, BarberReason: reason);
}

public sealed record CreateBookingRequest(
    long CustomerId,
    DateOnly Date,
    TimeOnly Time,
    int? BarberId,
    BookingSource Source,
    bool HoldOnly,
    string? CustomerNotes = null);

public sealed record SlotStatus(
    SlotRejection Rejection,
    IReadOnlyList<Barber> AvailableBarbers,
    Barber? RequestedBarber,
    BarberUnavailableReason RequestedBarberReason)
{
    public bool RequestedBarberAvailable => RequestedBarber is not null && RequestedBarberReason == BarberUnavailableReason.None;
}

/// <summary>Everything needed to evaluate availability for one local day, loaded in a few queries.</summary>
internal sealed class DaySnapshot(
    ShopCalendar calendar,
    DateOnly date,
    IReadOnlyList<Barber> barbers,
    IReadOnlyList<BarberTimeOff> timeOff,
    IReadOnlyList<Booking> bookings)
{
    public IReadOnlyList<Barber> Barbers => barbers;

    public BarberUnavailableReason Check(Barber barber, TimeOnly time, DateTime nowUtc, long? ignoreBookingId)
    {
        var startUtc = calendar.ToUtc(date, time);
        return BarberAvailability.Check(barber, date, time, time.Add(calendar.Duration), startUtc, startUtc + calendar.Duration,
            timeOff, bookings, nowUtc, ignoreBookingId);
    }

    /// <summary>Available barbers, least-booked first that day, then by station: deterministic load balancing.</summary>
    public List<Barber> AvailableBarbers(TimeOnly time, DateTime nowUtc, long? ignoreBookingId, int? preferBarberId = null) =>
        barbers.Where(b => Check(b, time, nowUtc, ignoreBookingId) == BarberUnavailableReason.None)
            .OrderBy(b => b.Id == preferBarberId ? 0 : 1)
            .ThenBy(b => bookings.Count(x => x.BarberId == b.Id && x.Id != ignoreBookingId && x.OccupiesSlotAt(nowUtc)))
            .ThenBy(b => b.StationNumber)
            .ToList();

    public IEnumerable<SlotOption> AvailableSlots(DateTime nowUtc, int? barberId, long? ignoreBookingId) =>
        calendar.SlotStartTimes
            .Where(t => calendar.ValidateSlot(date, t, nowUtc) == SlotRejection.None)
            .SelectMany(t => AvailableBarbers(t, nowUtc, ignoreBookingId)
                .Where(b => barberId is null || b.Id == barberId)
                .OrderBy(b => b.StationNumber)
                .Select(b => new SlotOption(date, t, b.Id, b.DisplayName)));
}
