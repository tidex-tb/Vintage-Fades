using Microsoft.EntityFrameworkCore;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Application.Bookings;

public sealed record PublicBarber(int Id, string Name);

public sealed record PublicDayAvailability(string Date, int FreeSlots);

public sealed record PublicSlot(string Time, IReadOnlyList<PublicBarber> Barbers);

public sealed record PublicBookingRequest(string? CustomerName, string? Phone, DateOnly Date, TimeOnly Time, int? BarberId, string? Notes);

public sealed record PublicBookingConfirmation(string Reference, string Date, string Time, string EndTime, string Barber, string Customer);

public enum PublicBookingError
{
    None,
    InvalidName,
    InvalidPhone,
    SlotUnavailable,
    TooManyUpcoming,
    NotFound,
    CannotCancel
}

public sealed record PublicBookingResult(PublicBookingError Error, PublicBookingConfirmation? Confirmation = null)
{
    public bool Success => Error == PublicBookingError.None;
}

/// <summary>
/// What the shop's public website may do: look at open slots and book or cancel. It goes through <see cref="BookingService"/>,
/// the same engine the chatbot and the dashboard use, so a slot taken anywhere is taken everywhere and the database's
/// exclusion constraint still has the last word.
/// </summary>
public sealed class PublicBookingPortal(IAppDbContext db, BookingService bookings, ShopSettingsProvider settings, TimeProvider time)
{
    /// <summary>One person cannot hold more future appointments than this through the website.</summary>
    public const int MaxUpcomingPerPhone = 3;

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PublicBarber>> GetBarbersAsync(CancellationToken ct) =>
        await db.Barbers.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.StationNumber)
            .Select(b => new PublicBarber(b.Id, b.DisplayName)).ToListAsync(ct);

    /// <summary>How many slots are still open on each bookable day, for the date picker. A barberId narrows it to one barber.</summary>
    public async Task<IReadOnlyList<PublicDayAvailability>> GetAvailabilityAsync(int? barberId, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var today = calendar.Today(UtcNow);
        var last = calendar.LastBookableDate(UtcNow);
        var days = new List<PublicDayAvailability>();
        for (var day = today; day <= last; day = day.AddDays(1))
        {
            var slots = await bookings.GetAvailableSlotsAsync(day, barberId, null, ct);
            days.Add(new PublicDayAvailability(day.ToString("yyyy-MM-dd"), slots.Count));
        }
        return days;
    }

    /// <summary>Open times on a day with the barbers free at each time.</summary>
    public async Task<IReadOnlyList<PublicSlot>> GetSlotsAsync(DateOnly date, int? barberId, CancellationToken ct) =>
        (await bookings.GetAvailableSlotsAsync(date, barberId, null, ct))
            .GroupBy(s => s.Time).OrderBy(g => g.Key)
            .Select(g => new PublicSlot(Format.Time(g.Key), g.Select(s => new PublicBarber(s.BarberId, s.BarberName)).ToList()))
            .ToList();

    public async Task<PublicBookingResult> BookAsync(PublicBookingRequest request, CancellationToken ct)
    {
        var name = request.CustomerName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return new(PublicBookingError.InvalidName);
        var phone = PhoneNumbers.ToInternational(request.Phone);
        if (phone is null) return new(PublicBookingError.InvalidPhone);
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()[..Math.Min(500, request.Notes.Trim().Length)];

        var now = UtcNow;
        var upcoming = await db.Bookings.AsNoTracking().CountAsync(b =>
            b.Customer!.PhoneNumber == phone && b.StartAtUtc > now &&
            (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Pending), ct);
        if (upcoming >= MaxUpcomingPerPhone) return new(PublicBookingError.TooManyUpcoming);

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.PhoneNumber == phone, ct);
        if (customer is null)
        {
            customer = new Customer { FullName = name, PhoneNumber = phone, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);
        }
        else if (string.IsNullOrWhiteSpace(customer.FullName))
        {
            customer.FullName = name;
            customer.UpdatedAtUtc = now;
        }

        var result = await bookings.CreateBookingAsync(new CreateBookingRequest(customer.Id, request.Date, request.Time, request.BarberId,
            BookingSource.Web, HoldOnly: false, notes), ct);
        if (!result.Success) return new(PublicBookingError.SlotUnavailable);

        var calendar = await settings.GetCalendarAsync(ct);
        var booking = result.Booking!;
        var local = calendar.ToLocal(booking.StartAtUtc);
        var end = calendar.ToLocal(booking.EndAtUtc);
        return new(PublicBookingError.None, new PublicBookingConfirmation(
            booking.BookingReference,
            local.ToString("yyyy-MM-dd"),
            Format.Time(TimeOnly.FromDateTime(local)),
            Format.Time(TimeOnly.FromDateTime(end)),
            booking.Barber?.DisplayName ?? "",
            customer.FullName ?? name));
    }

    /// <summary>Looks a booking up by reference and the phone number it was made with (both must match).</summary>
    public async Task<PublicBookingConfirmation?> FindAsync(string reference, string? phone, CancellationToken ct)
    {
        var booking = await FindOwnedAsync(reference, phone, ct);
        if (booking is null || booking.Status is not (BookingStatus.Confirmed or BookingStatus.Pending)) return null;
        var calendar = await settings.GetCalendarAsync(ct);
        var local = calendar.ToLocal(booking.StartAtUtc);
        return new PublicBookingConfirmation(booking.BookingReference, local.ToString("yyyy-MM-dd"),
            Format.Time(TimeOnly.FromDateTime(local)), Format.Time(TimeOnly.FromDateTime(calendar.ToLocal(booking.EndAtUtc))),
            booking.Barber?.DisplayName ?? "", booking.Customer?.FullName ?? "");
    }

    public async Task<PublicBookingError> CancelAsync(string reference, string? phone, CancellationToken ct)
    {
        var booking = await FindOwnedAsync(reference, phone, ct);
        if (booking is null) return PublicBookingError.NotFound;
        var result = await bookings.CancelBookingAsync(booking.Id, booking.CustomerId, "Cancelled by the customer on the website", enforceNotice: true, ct);
        return result.Success ? PublicBookingError.None : result.Error == BookingError.NotFound ? PublicBookingError.NotFound : PublicBookingError.CannotCancel;
    }

    private async Task<Booking?> FindOwnedAsync(string reference, string? phone, CancellationToken ct)
    {
        var normalized = PhoneNumbers.ToInternational(phone);
        if (normalized is null || string.IsNullOrWhiteSpace(reference)) return null;
        var booking = await bookings.GetBookingByReferenceAsync(reference.Trim().ToUpperInvariant(), ct);
        if (booking is null) return null;
        var customer = booking.Customer ?? await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == booking.CustomerId, ct);
        if (customer?.PhoneNumber != normalized) return null;
        booking.Customer = customer;
        booking.Barber ??= await db.Barbers.AsNoTracking().FirstOrDefaultAsync(b => b.Id == booking.BarberId, ct);
        return booking;
    }
}
