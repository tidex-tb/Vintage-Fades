using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Messaging;
using VintageFades.Domain;

namespace VintageFades.Application.Bookings;

/// <summary>
/// The single booking engine used by every channel. Availability is computed from the database and the
/// PostgreSQL exclusion constraint on Bookings is the final guard against double booking: a lost race
/// surfaces as <see cref="BookingError.SlotTaken"/>, never as a second booking.
/// </summary>
public sealed class BookingService(
    IAppDbContext db,
    ShopSettingsProvider settings,
    NotificationService notifications,
    TimeProvider time,
    IOptions<BookingOptions> options,
    ILogger<BookingService> logger)
{
    public const string HoldExpiredReason = "Hold expired";
    public const string HoldDeclinedReason = "Declined by customer";

    private readonly BookingOptions _options = options.Value;
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public Task<ShopCalendar> GetCalendarAsync(CancellationToken ct) => settings.GetCalendarAsync(ct);

    public async Task<IReadOnlyList<SlotOption>> GetAvailableSlotsAsync(DateOnly date, int? barberId, long? ignoreBookingId, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var snapshot = await LoadDayAsync(calendar, date, ct);
        return snapshot.AvailableSlots(UtcNow, barberId, ignoreBookingId).ToList();
    }

    public async Task<IReadOnlyList<Barber>> GetAvailableBarbersAsync(DateOnly date, TimeOnly time, long? ignoreBookingId, CancellationToken ct) =>
        (await GetSlotStatusAsync(date, time, null, ignoreBookingId, ct)).AvailableBarbers;

    public async Task<SlotStatus> GetSlotStatusAsync(DateOnly date, TimeOnly time, int? barberId, long? ignoreBookingId, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var now = UtcNow;
        var rejection = calendar.ValidateSlot(date, time, now);
        if (rejection != SlotRejection.None)
            return new SlotStatus(rejection, [], null, BarberUnavailableReason.None);

        var snapshot = await LoadDayAsync(calendar, date, ct);
        var available = snapshot.AvailableBarbers(time, now, ignoreBookingId);
        var requested = barberId is null ? null : snapshot.Barbers.FirstOrDefault(b => b.Id == barberId);
        var reason = requested is null ? BarberUnavailableReason.None : snapshot.Check(requested, time, now, ignoreBookingId);
        return new SlotStatus(SlotRejection.None, available, requested, reason);
    }

    /// <summary>
    /// Closest open slots to the requested time: same day first (nearest, earlier wins ties, preferred barber first,
    /// at most two barbers per time), then the following days.
    /// </summary>
    public async Task<IReadOnlyList<SlotOption>> GetAlternativeSlotsAsync(
        DateOnly date, TimeOnly requestedTime, int? preferredBarberId, long? ignoreBookingId, int? max, CancellationToken ct)
    {
        var limit = max ?? _options.MaxAlternatives;
        var calendar = await settings.GetCalendarAsync(ct);
        var now = UtcNow;
        var results = new List<SlotOption>();

        var sameDay = (await LoadDayAsync(calendar, date, ct)).AvailableSlots(now, null, ignoreBookingId)
            .Where(s => s.Time != requestedTime || s.BarberId != preferredBarberId)
            .OrderBy(s => Math.Abs((s.Time.ToTimeSpan() - requestedTime.ToTimeSpan()).TotalMinutes))
            .ThenBy(s => s.Time)
            .ThenBy(s => s.BarberId == preferredBarberId ? 0 : 1);
        results.AddRange(TakeDiverse(sameDay, limit));

        var lastDate = calendar.LastBookableDate(now);
        for (var day = date.AddDays(1); results.Count < limit && day <= lastDate && day <= date.AddDays(7); day = day.AddDays(1))
        {
            var slots = (await LoadDayAsync(calendar, day, ct)).AvailableSlots(now, null, ignoreBookingId)
                .OrderBy(s => Math.Abs((s.Time.ToTimeSpan() - requestedTime.ToTimeSpan()).TotalMinutes))
                .ThenBy(s => s.Time)
                .ThenBy(s => s.BarberId == preferredBarberId ? 0 : 1);
            results.AddRange(TakeDiverse(slots, limit - results.Count));
        }
        return results;

        static List<SlotOption> TakeDiverse(IEnumerable<SlotOption> ordered, int count)
        {
            var perTime = new Dictionary<TimeOnly, int>();
            var taken = new List<SlotOption>();
            foreach (var slot in ordered)
            {
                if (taken.Count >= count) break;
                var used = perTime.GetValueOrDefault(slot.Time);
                if (used >= 2) continue;
                perTime[slot.Time] = used + 1;
                taken.Add(slot);
            }
            return taken;
        }
    }

    public async Task<BookingResult> CreateBookingAsync(CreateBookingRequest request, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var now = UtcNow;
        var rejection = calendar.ValidateSlot(request.Date, request.Time, now);
        if (rejection != SlotRejection.None) return BookingResult.InvalidSlot(rejection);

        return await db.InTransactionAsync(async () =>
        {
            var snapshot = await LoadDayAsync(calendar, request.Date, ct);
            List<Barber> candidates;
            if (request.BarberId is int barberId)
            {
                var barber = snapshot.Barbers.FirstOrDefault(b => b.Id == barberId);
                if (barber is null) return BookingResult.Fail(BookingError.BarberNotFound);
                var reason = snapshot.Check(barber, request.Time, now, null);
                if (reason != BarberUnavailableReason.None) return BookingResult.Unavailable(reason);
                candidates = [barber];
            }
            else
            {
                candidates = snapshot.AvailableBarbers(request.Time, now, null);
                if (candidates.Count == 0) return BookingResult.Fail(BookingError.NoAvailability);
            }

            var startUtc = calendar.ToUtc(request.Date, request.Time);
            var endUtc = startUtc + calendar.Duration;

            foreach (var candidate in candidates)
            {
                await ReleaseExpiredHoldsAsync(candidate.Id, startUtc, endUtc, now, ct);
                var booking = new Booking
                {
                    BookingReference = await NextReferenceAsync(calendar, now, ct),
                    CustomerId = request.CustomerId,
                    BarberId = candidate.Id,
                    StartAtUtc = startUtc,
                    EndAtUtc = endUtc,
                    Status = request.HoldOnly ? BookingStatus.Pending : BookingStatus.Confirmed,
                    HoldExpiresAtUtc = request.HoldOnly ? now.AddMinutes(_options.PendingHoldMinutes) : null,
                    SourcePlatform = request.Source,
                    CustomerNotes = request.CustomerNotes,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.Bookings.Add(booking);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (db.IsBookingConflict(ex))
                {
                    db.Discard(booking);
                    logger.LogInformation("Booking race lost for barber {BarberId} at {StartAtUtc}", candidate.Id, startUtc);
                    continue;
                }

                booking.Barber = await db.Barbers.FindAsync([candidate.Id], ct);
                if (booking.Status == BookingStatus.Confirmed)
                {
                    await NotifyOwnerAsync(NotificationTypes.NewBooking, booking, calendar, ct);
                    await db.SaveChangesAsync(ct);
                }
                logger.LogInformation("Booking {Reference} created as {Status} for barber {BarberId}",
                    booking.BookingReference, booking.Status, booking.BarberId);
                return BookingResult.Ok(booking);
            }
            return BookingResult.Fail(BookingError.SlotTaken);
        }, ct);
    }

    /// <summary>Confirms a hold. Idempotent; an expired hold is re-acquired when the slot is still free.</summary>
    public async Task<BookingResult> ConfirmBookingAsync(long bookingId, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var now = UtcNow;
        return await db.InTransactionAsync(async () =>
        {
            var booking = await db.Bookings.Include(b => b.Barber).Include(b => b.Customer).FirstOrDefaultAsync(b => b.Id == bookingId, ct);
            if (booking is null) return BookingResult.Fail(BookingError.NotFound);
            if (booking.Status == BookingStatus.Confirmed) return BookingResult.Ok(booking);

            var reacquirable = booking.Status == BookingStatus.Pending ||
                               (booking.Status == BookingStatus.Cancelled && booking.CancellationReason == HoldExpiredReason);
            if (!reacquirable) return BookingResult.Fail(BookingError.InvalidStatus, booking);
            if (booking.StartAtUtc <= now) return BookingResult.InvalidSlot(SlotRejection.InPast);

            var local = calendar.ToLocal(booking.StartAtUtc);
            var snapshot = await LoadDayAsync(calendar, DateOnly.FromDateTime(local), ct);
            var barber = snapshot.Barbers.First(b => b.Id == booking.BarberId);
            var reason = snapshot.Check(barber, TimeOnly.FromDateTime(local), now, booking.Id);
            if (reason == BarberUnavailableReason.Booked) return BookingResult.Fail(BookingError.SlotTaken);
            if (reason != BarberUnavailableReason.None) return BookingResult.Unavailable(reason);

            booking.Status = BookingStatus.Confirmed;
            booking.HoldExpiresAtUtc = null;
            booking.CancelledAtUtc = null;
            booking.CancellationReason = null;
            booking.UpdatedAtUtc = now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (db.IsBookingConflict(ex))
            {
                db.Discard(booking);
                return BookingResult.Fail(BookingError.SlotTaken);
            }

            await NotifyOwnerAsync(NotificationTypes.NewBooking, booking, calendar, ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Booking {Reference} confirmed", booking.BookingReference);
            return BookingResult.Ok(booking);
        }, ct);
    }

    /// <summary>Releases a hold the customer declined. No-op for anything that is not a pending hold.</summary>
    public async Task ReleaseHoldAsync(long bookingId, CancellationToken ct)
    {
        var now = UtcNow;
        await db.Bookings.Where(b => b.Id == bookingId && b.Status == BookingStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Cancelled)
                .SetProperty(b => b.CancelledAtUtc, now)
                .SetProperty(b => b.CancellationReason, HoldDeclinedReason)
                .SetProperty(b => b.UpdatedAtUtc, now), ct);
    }

    public async Task<int> ReleaseExpiredHoldsAsync(CancellationToken ct)
    {
        var now = UtcNow;
        return await db.Bookings.Where(b => b.Status == BookingStatus.Pending && b.HoldExpiresAtUtc <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Cancelled)
                .SetProperty(b => b.CancelledAtUtc, now)
                .SetProperty(b => b.CancellationReason, HoldExpiredReason)
                .SetProperty(b => b.UpdatedAtUtc, now), ct);
    }

    public async Task<BookingResult> CancelBookingAsync(long bookingId, long? customerId, string reason, bool enforceNotice, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var now = UtcNow;
        return await db.InTransactionAsync(async () =>
        {
            var booking = await db.Bookings.Include(b => b.Barber).Include(b => b.Customer).FirstOrDefaultAsync(b => b.Id == bookingId, ct);
            if (booking is null) return BookingResult.Fail(BookingError.NotFound);
            if (customerId is not null && booking.CustomerId != customerId) return BookingResult.Fail(BookingError.NotOwner);
            if (booking.Status is not (BookingStatus.Confirmed or BookingStatus.Pending)) return BookingResult.Fail(BookingError.InvalidStatus, booking);
            if (enforceNotice && booking.Status == BookingStatus.Confirmed && IsInsideNoticePeriod(booking, calendar, now))
                return BookingResult.Fail(BookingError.CancellationTooLate, booking);

            var wasConfirmed = booking.Status == BookingStatus.Confirmed;
            booking.Status = BookingStatus.Cancelled;
            booking.CancelledAtUtc = now;
            booking.CancellationReason = reason;
            booking.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);

            if (wasConfirmed)
            {
                await NotifyOwnerAsync(NotificationTypes.Cancellation, booking, calendar, ct);
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Booking {Reference} cancelled", booking.BookingReference);
            return BookingResult.Ok(booking);
        }, ct);
    }

    /// <summary>
    /// Moves a confirmed booking in place (same reference). The old slot is only released when the new one is
    /// secured, because both happen in one UPDATE guarded by the exclusion constraint.
    /// </summary>
    public async Task<BookingResult> RescheduleBookingAsync(
        long bookingId, long? customerId, DateOnly date, TimeOnly time, int? barberId, bool enforceNotice, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var now = UtcNow;
        var rejection = calendar.ValidateSlot(date, time, now);
        if (rejection != SlotRejection.None) return BookingResult.InvalidSlot(rejection);

        return await db.InTransactionAsync(async () =>
        {
            var booking = await db.Bookings.Include(b => b.Customer).FirstOrDefaultAsync(b => b.Id == bookingId, ct);
            if (booking is null) return BookingResult.Fail(BookingError.NotFound);
            if (customerId is not null && booking.CustomerId != customerId) return BookingResult.Fail(BookingError.NotOwner);
            if (booking.Status != BookingStatus.Confirmed) return BookingResult.Fail(BookingError.InvalidStatus, booking);
            if (enforceNotice && IsInsideNoticePeriod(booking, calendar, now)) return BookingResult.Fail(BookingError.CancellationTooLate, booking);

            var snapshot = await LoadDayAsync(calendar, date, ct);
            List<Barber> candidates;
            if (barberId is int id)
            {
                var barber = snapshot.Barbers.FirstOrDefault(b => b.Id == id);
                if (barber is null) return BookingResult.Fail(BookingError.BarberNotFound);
                var reason = snapshot.Check(barber, time, now, booking.Id);
                if (reason != BarberUnavailableReason.None) return BookingResult.Unavailable(reason);
                candidates = [barber];
            }
            else
            {
                candidates = snapshot.AvailableBarbers(time, now, booking.Id, booking.BarberId);
                if (candidates.Count == 0) return BookingResult.Fail(BookingError.NoAvailability);
            }

            var oldBarber = await db.Barbers.AsNoTracking().FirstAsync(b => b.Id == booking.BarberId, ct);
            var (oldBarberId, oldStart, oldEnd) = (booking.BarberId, booking.StartAtUtc, booking.EndAtUtc);
            var startUtc = calendar.ToUtc(date, time);

            foreach (var candidate in candidates)
            {
                await ReleaseExpiredHoldsAsync(candidate.Id, startUtc, startUtc + calendar.Duration, now, ct);
                booking.BarberId = candidate.Id;
                booking.StartAtUtc = startUtc;
                booking.EndAtUtc = startUtc + calendar.Duration;
                booking.UpdatedAtUtc = now;
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (db.IsBookingConflict(ex))
                {
                    (booking.BarberId, booking.StartAtUtc, booking.EndAtUtc) = (oldBarberId, oldStart, oldEnd);
                    continue;
                }

                booking.Barber = await db.Barbers.FindAsync([candidate.Id], ct);
                var oldLocal = calendar.ToLocal(oldStart);
                var previous = $"{Format.DateShort(DateOnly.FromDateTime(oldLocal))} {Format.Time(TimeOnly.FromDateTime(oldLocal))} with {oldBarber.DisplayName}";
                await NotifyOwnerAsync(NotificationTypes.Rescheduled, booking, calendar, ct, previous);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Booking {Reference} rescheduled", booking.BookingReference);
                return BookingResult.Ok(booking);
            }
            return BookingResult.Fail(BookingError.SlotTaken);
        }, ct);
    }

    public async Task<IReadOnlyList<Booking>> GetCustomerBookingsAsync(long customerId, CancellationToken ct)
    {
        var now = UtcNow;
        return await db.Bookings.AsNoTracking().Include(b => b.Barber)
            .Where(b => b.CustomerId == customerId && b.Status == BookingStatus.Confirmed && b.StartAtUtc > now)
            .OrderBy(b => b.StartAtUtc)
            .ToListAsync(ct);
    }

    public Task<Booking?> GetBookingByReferenceAsync(string reference, CancellationToken ct) =>
        db.Bookings.AsNoTracking().Include(b => b.Barber).Include(b => b.Customer)
            .FirstOrDefaultAsync(b => b.BookingReference == reference.Trim().ToUpperInvariant(), ct);

    private bool IsInsideNoticePeriod(Booking booking, ShopCalendar calendar, DateTime now) =>
        booking.StartAtUtc - now < TimeSpan.FromHours(calendar.Settings.CancellationNoticeHours);

    private Task ReleaseExpiredHoldsAsync(int barberId, DateTime startUtc, DateTime endUtc, DateTime now, CancellationToken ct) =>
        db.Bookings.Where(b => b.BarberId == barberId && b.Status == BookingStatus.Pending && b.HoldExpiresAtUtc <= now
                               && b.StartAtUtc < endUtc && b.EndAtUtc > startUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Cancelled)
                .SetProperty(b => b.CancelledAtUtc, now)
                .SetProperty(b => b.CancellationReason, HoldExpiredReason)
                .SetProperty(b => b.UpdatedAtUtc, now), ct);

    private async Task<string> NextReferenceAsync(ShopCalendar calendar, DateTime now, CancellationToken ct) =>
        $"VF-{calendar.Today(now).Year}-{await db.NextBookingNumberAsync(ct):D6}";

    private async Task<DaySnapshot> LoadDayAsync(ShopCalendar calendar, DateOnly date, CancellationToken ct)
    {
        var (fromUtc, toUtc) = calendar.DayRangeUtc(date);
        var barbers = await db.Barbers.AsNoTracking().Include(b => b.WorkingHours).OrderBy(b => b.StationNumber).ToListAsync(ct);
        var timeOff = await db.BarberTimeOff.AsNoTracking().Where(t => t.StartAtUtc < toUtc && t.EndAtUtc > fromUtc).ToListAsync(ct);
        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed)
                        && b.StartAtUtc < toUtc && b.EndAtUtc > fromUtc)
            .ToListAsync(ct);
        return new DaySnapshot(calendar, date, barbers, timeOff, bookings);
    }

    private async Task NotifyOwnerAsync(string type, Booking booking, ShopCalendar calendar, CancellationToken ct, string? previous = null)
    {
        var customer = booking.Customer ?? await db.Customers.AsNoTracking().FirstAsync(c => c.Id == booking.CustomerId, ct);
        var barber = booking.Barber ?? await db.Barbers.AsNoTracking().FirstAsync(b => b.Id == booking.BarberId, ct);
        var local = calendar.ToLocal(booking.StartAtUtc);
        var title = type switch
        {
            NotificationTypes.NewBooking => "NEW BOOKING – VINTAGE FADES",
            NotificationTypes.Cancellation => "BOOKING CANCELLED",
            _ => "BOOKING RESCHEDULED"
        };
        var lines = new List<string>
        {
            title,
            $"Customer: {customer.FullName ?? "(name not given)"}"
        };
        if (customer.PhoneNumber is not null) lines.Add($"Phone: {customer.PhoneNumber}");
        if (previous is not null) lines.Add($"Was: {previous}");
        lines.Add($"Date: {Format.DateShort(DateOnly.FromDateTime(local))}");
        lines.Add($"Time: {Format.Time(TimeOnly.FromDateTime(local))}");
        lines.Add($"Barber: {barber.DisplayName}");
        if (type == NotificationTypes.NewBooking) lines.Add($"Platform: {SourceName(booking.SourcePlatform)}");
        lines.Add($"Reference: {booking.BookingReference}");
        await notifications.EnqueueOwnerNotificationAsync(type, string.Join('\n', lines), null, ct);
    }

    private static string SourceName(BookingSource source) => source switch
    {
        BookingSource.WhatsApp => "WhatsApp",
        BookingSource.Instagram => "Instagram",
        BookingSource.Messenger => "Facebook Messenger",
        BookingSource.Web => "Website",
        _ => "Manual (shop)"
    };
}
