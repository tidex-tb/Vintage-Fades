using Microsoft.EntityFrameworkCore;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Domain;

namespace VintageFades.Application.Operations;

public sealed record BookingSummary(
    string Reference, string Status, string Date, string Time, string Barber, string? Customer, string? Phone, string Source);

public sealed record ManualBookingRequest(string CustomerName, string? PhoneNumber, DateOnly Date, TimeOnly Time, int? BarberId, string? Notes);

public sealed record BarberUpdate(string? FirstName, string? LastName, string? DisplayName, bool? IsActive);

public sealed record TimeOffRequest(DateOnly StartDate, TimeOnly StartTime, DateOnly EndDate, TimeOnly EndTime, string? Reason);

/// <summary>Operational actions for the owner (manual bookings during downtime, barber changes, handover resume).
/// Every booking change still goes through <see cref="BookingService"/>.</summary>
public sealed class AdminService(IAppDbContext db, BookingService bookings, ShopSettingsProvider settings, TimeProvider time)
{
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<BookingSummary>> GetBookingsForDayAsync(DateOnly date, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var (from, to) = calendar.DayRangeUtc(date);
        var rows = await db.Bookings.AsNoTracking().Include(b => b.Barber).Include(b => b.Customer)
            .Where(b => b.StartAtUtc >= from && b.StartAtUtc < to).OrderBy(b => b.StartAtUtc).ThenBy(b => b.BarberId).ToListAsync(ct);
        return rows.Select(b => Summarize(b, calendar)).ToList();
    }

    public async Task<(BookingResult Result, BookingSummary? Summary)> CreateManualBookingAsync(ManualBookingRequest request, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var phone = PhoneNumbers.Normalize(request.PhoneNumber) is { } digits ? "+" + digits : null;
        var customer = phone is null ? null : await db.Customers.FirstOrDefaultAsync(c => c.PhoneNumber == phone, ct);
        if (customer is null)
        {
            customer = new Customer { FullName = request.CustomerName.Trim(), PhoneNumber = phone, CreatedAtUtc = UtcNow, UpdatedAtUtc = UtcNow };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);
        }
        var result = await bookings.CreateBookingAsync(new CreateBookingRequest(customer.Id, request.Date, request.Time, request.BarberId,
            BookingSource.Admin, HoldOnly: false, request.Notes), ct);
        if (result.Success) result.Booking!.Customer = customer;
        return (result, result.Success ? Summarize(result.Booking!, calendar) : null);
    }

    public async Task<BookingResult> CancelAsync(string reference, string? reason, CancellationToken ct)
    {
        var booking = await bookings.GetBookingByReferenceAsync(reference, ct);
        return booking is null
            ? BookingResult.Fail(BookingError.NotFound)
            : await bookings.CancelBookingAsync(booking.Id, null, reason ?? "Cancelled by the shop", enforceNotice: false, ct);
    }

    public async Task<bool> ResumeConversationAsync(long conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return false;
        conversation.RequiresAttention = false;
        conversation.CurrentState = ConversationState.Idle;
        conversation.CurrentBookingIntent = null;
        conversation.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<object>> GetConversationsAwaitingHumanAsync(CancellationToken ct) =>
        await db.Conversations.AsNoTracking().Include(c => c.Customer).Where(c => c.RequiresAttention)
            .OrderBy(c => c.UpdatedAtUtc)
            .Select(c => (object)new { c.Id, Platform = c.Platform.ToString(), Customer = c.Customer!.FullName, c.LastMessageAtUtc })
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Barber>> GetBarbersAsync(CancellationToken ct) =>
        await db.Barbers.AsNoTracking().Include(b => b.WorkingHours).OrderBy(b => b.StationNumber).ToListAsync(ct);

    public async Task<Barber?> UpdateBarberAsync(int id, BarberUpdate update, CancellationToken ct)
    {
        var barber = await db.Barbers.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (barber is null) return null;
        if (!string.IsNullOrWhiteSpace(update.FirstName)) barber.FirstName = update.FirstName.Trim();
        if (update.LastName is not null) barber.LastName = update.LastName.Trim().Length == 0 ? null : update.LastName.Trim();
        if (!string.IsNullOrWhiteSpace(update.DisplayName)) barber.DisplayName = update.DisplayName.Trim();
        if (update.IsActive is bool active) barber.IsActive = active;
        barber.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(ct);
        return barber;
    }

    /// <summary>Adds time off. Existing bookings are not cancelled automatically; the response lists any that overlap.</summary>
    public async Task<(BarberTimeOff? TimeOff, IReadOnlyList<string> OverlappingBookings)> AddTimeOffAsync(int barberId, TimeOffRequest request, CancellationToken ct)
    {
        if (!await db.Barbers.AnyAsync(b => b.Id == barberId, ct)) return (null, []);
        var calendar = await settings.GetCalendarAsync(ct);
        var start = calendar.ToUtc(request.StartDate, request.StartTime);
        var end = calendar.ToUtc(request.EndDate, request.EndTime);
        if (end <= start) throw new ArgumentException("End must be after start.");

        var timeOff = new BarberTimeOff { BarberId = barberId, StartAtUtc = start, EndAtUtc = end, Reason = request.Reason, CreatedAtUtc = UtcNow };
        db.BarberTimeOff.Add(timeOff);
        await db.SaveChangesAsync(ct);
        var overlapping = await db.Bookings.AsNoTracking()
            .Where(b => b.BarberId == barberId && b.Status == BookingStatus.Confirmed && b.StartAtUtc < end && b.EndAtUtc > start)
            .Select(b => b.BookingReference).ToListAsync(ct);
        return (timeOff, overlapping);
    }

    public async Task<bool> DeleteTimeOffAsync(long id, CancellationToken ct) =>
        await db.BarberTimeOff.Where(t => t.Id == id).ExecuteDeleteAsync(ct) > 0;

    public async Task<bool> RetryOutboxAsync(long id, CancellationToken ct) =>
        await db.OutboxMessages.Where(o => o.Id == id && o.Status == OutboxStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OutboxStatus.Pending)
                .SetProperty(o => o.RetryCount, 0)
                .SetProperty(o => o.NextAttemptAtUtc, UtcNow), ct) > 0;

    public async Task<IReadOnlyList<object>> GetFailedOutboxAsync(CancellationToken ct) =>
        await db.OutboxMessages.AsNoTracking().Where(o => o.Status == OutboxStatus.Failed)
            .OrderByDescending(o => o.Id).Take(50)
            .Select(o => (object)new { o.Id, o.EventType, o.CreatedAtUtc, o.RetryCount, o.LastError })
            .ToListAsync(ct);

    private static BookingSummary Summarize(Booking b, ShopCalendar calendar)
    {
        var local = calendar.ToLocal(b.StartAtUtc);
        return new BookingSummary(b.BookingReference, b.Status.ToString(), Format.Date(DateOnly.FromDateTime(local)),
            Format.Time(TimeOnly.FromDateTime(local)), b.Barber?.DisplayName ?? $"#{b.BarberId}", b.Customer?.FullName, b.Customer?.PhoneNumber,
            b.SourcePlatform.ToString());
    }
}
