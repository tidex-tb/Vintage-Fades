using Microsoft.EntityFrameworkCore;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Domain;

namespace VintageFades.Application.Operations;

public sealed record ServiceLine(string Name, decimal Price);

public sealed record CompleteRequest(int? BarberId, List<ServiceLine> Services);

public sealed record SaleRequest(int BarberId, List<ServiceLine> Services, string? CustomerName, DateOnly? Date);

public sealed record RecordedService(long Id, string Name, decimal Price, int BarberId, string Barber);

public sealed record ScheduleItem(
    string Reference, string Status, string Time, string EndTime, int BarberId, string Barber, string? Customer, string? Phone,
    string Source, string? Notes, decimal Total, IReadOnlyList<RecordedService> Services);

public sealed record WalkInItem(string Time, string? Customer, decimal Total, IReadOnlyList<RecordedService> Services);

public sealed record DaySchedule(string Date, IReadOnlyList<ScheduleItem> Appointments, IReadOnlyList<WalkInItem> WalkIns, decimal Revenue);

public sealed record BarberReport(int BarberId, string Barber, decimal Revenue, int Services, int Visits);

public sealed record ServiceReport(string Service, int Count, decimal Revenue);

public sealed record DayReport(string Date, decimal Revenue, int Services);

public sealed record Report(
    string From, string To, decimal Revenue, int Services, int Visits, decimal AverageVisit, int NoShows, int Cancelled,
    IReadOnlyList<BarberReport> ByBarber, IReadOnlyList<ServiceReport> ByService, IReadOnlyList<DayReport> ByDay);

public enum DashboardError
{
    None,
    NotFound,
    Invalid,
    NotConfirmed,
    TooEarly
}

public sealed record DashboardResult(DashboardError Error, string? Message = null)
{
    public bool Success => Error == DashboardError.None;
    public static readonly DashboardResult Ok = new(DashboardError.None);
}

/// <summary>
/// What the owner does after the appointment: record which services were done and by whom, mark no-shows, add walk-ins
/// that never had a booking, and read the revenue reports. Appointments themselves (including phone bookings) still go
/// through <see cref="BookingService"/>, so the bot sees every slot the owner fills.
/// </summary>
public sealed class DashboardService(IAppDbContext db, ShopSettingsProvider settings, TimeProvider time)
{
    public const int MaxServicesPerEntry = 10;
    public const decimal MaxPrice = 100_000m;

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- the day

    public async Task<DaySchedule> GetDayAsync(DateOnly date, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var (from, to) = calendar.DayRangeUtc(date);

        var bookings = await db.Bookings.AsNoTracking().Include(b => b.Barber).Include(b => b.Customer)
            .Where(b => b.StartAtUtc >= from && b.StartAtUtc < to && b.Status != BookingStatus.Pending)
            .OrderBy(b => b.StartAtUtc).ThenBy(b => b.BarberId).ToListAsync(ct);
        var records = await db.ServiceRecords.AsNoTracking().Include(r => r.Barber)
            .Where(r => r.VoidedAtUtc == null && r.PerformedAtUtc >= from && r.PerformedAtUtc < to).ToListAsync(ct);

        var byBooking = records.Where(r => r.BookingId != null).ToLookup(r => r.BookingId!.Value);
        var appointments = bookings.Select(b =>
        {
            var local = calendar.ToLocal(b.StartAtUtc);
            var services = byBooking[b.Id].Select(ToRecorded).ToList();
            return new ScheduleItem(b.BookingReference, b.Status.ToString(), Format.Time(TimeOnly.FromDateTime(local)),
                Format.Time(TimeOnly.FromDateTime(calendar.ToLocal(b.EndAtUtc))), b.BarberId, b.Barber?.DisplayName ?? $"#{b.BarberId}",
                b.Customer?.FullName, b.Customer?.PhoneNumber, b.SourcePlatform.ToString(), b.CustomerNotes, services.Sum(s => s.Price), services);
        }).ToList();

        var walkIns = records.Where(r => r.BookingId == null)
            .GroupBy(r => r.VisitId)
            .OrderBy(g => g.Min(r => r.PerformedAtUtc)).ThenBy(g => g.Min(r => r.Id))
            .Select(g => new WalkInItem(Format.Time(TimeOnly.FromDateTime(calendar.ToLocal(g.Min(r => r.PerformedAtUtc)))), g.First().CustomerName,
                g.Sum(r => r.Price), g.Select(ToRecorded).ToList()))
            .ToList();

        return new DaySchedule(Format.Date(date), appointments, walkIns, records.Sum(r => r.Price));
    }

    // ---------------------------------------------------------------- recording what was done

    /// <summary>Records the services done for an appointment and marks it Completed. Calling it again for a completed
    /// appointment adds more services (after voiding a wrong one).</summary>
    public async Task<DashboardResult> CompleteAsync(string reference, CompleteRequest request, CancellationToken ct)
    {
        if (Validate(request.Services) is { } invalid) return invalid;
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.BookingReference == reference, ct);
        if (booking is null) return new DashboardResult(DashboardError.NotFound);
        if (booking.Status is not (BookingStatus.Confirmed or BookingStatus.Completed))
            return new DashboardResult(DashboardError.NotConfirmed, $"This appointment is {booking.Status}.");
        // A slot stops blocking the barber once the booking is Completed, so only close appointments that have started
        // (or are about to) and never free a future slot by mistake.
        if (booking.Status == BookingStatus.Confirmed && booking.StartAtUtc > UtcNow.AddMinutes(30))
            return new DashboardResult(DashboardError.TooEarly, "This appointment hasn't started yet.");

        var barberId = request.BarberId ?? booking.BarberId;
        if (!await db.Barbers.AnyAsync(b => b.Id == barberId, ct)) return new DashboardResult(DashboardError.Invalid, "Unknown barber.");

        var customerName = await db.Customers.Where(c => c.Id == booking.CustomerId).Select(c => c.FullName).FirstOrDefaultAsync(ct);
        var visitId = Guid.NewGuid();
        await db.InTransactionAsync(async () =>
        {
            foreach (var line in request.Services) db.ServiceRecords.Add(NewRecord(line, barberId, booking.StartAtUtc, booking.Id, customerName, visitId));
            booking.Status = BookingStatus.Completed;
            booking.UpdatedAtUtc = UtcNow;
            return await db.SaveChangesAsync(ct);
        }, ct);
        return DashboardResult.Ok;
    }

    public async Task<DashboardResult> MarkNoShowAsync(string reference, CancellationToken ct)
    {
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.BookingReference == reference, ct);
        if (booking is null) return new DashboardResult(DashboardError.NotFound);
        if (booking.Status != BookingStatus.Confirmed) return new DashboardResult(DashboardError.NotConfirmed, $"This appointment is {booking.Status}.");
        if (booking.StartAtUtc > UtcNow.AddMinutes(30)) return new DashboardResult(DashboardError.TooEarly, "This appointment hasn't started yet.");
        booking.Status = BookingStatus.NoShow;
        booking.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(ct);
        return DashboardResult.Ok;
    }

    /// <summary>A customer who walked in without a booking.</summary>
    public async Task<DashboardResult> AddSaleAsync(SaleRequest request, CancellationToken ct)
    {
        if (Validate(request.Services) is { } invalid) return invalid;
        if (request.CustomerName is { Length: > 100 }) return new DashboardResult(DashboardError.Invalid, "Customer name is limited to 100 characters.");
        if (!await db.Barbers.AnyAsync(b => b.Id == request.BarberId, ct)) return new DashboardResult(DashboardError.Invalid, "Unknown barber.");

        var calendar = await settings.GetCalendarAsync(ct);
        var now = UtcNow;
        // Today's walk-ins are stamped with the current time; an earlier date (catching up on the paper book) with midday.
        var performed = request.Date is { } date && date != calendar.Today(now) ? calendar.ToUtc(date, new TimeOnly(12, 0)) : now;
        if (performed > now.AddMinutes(5)) return new DashboardResult(DashboardError.Invalid, "A sale can't be in the future.");

        var name = string.IsNullOrWhiteSpace(request.CustomerName) ? null : request.CustomerName.Trim();
        var visitId = Guid.NewGuid();
        foreach (var line in request.Services) db.ServiceRecords.Add(NewRecord(line, request.BarberId, performed, null, name, visitId));
        await db.SaveChangesAsync(ct);
        return DashboardResult.Ok;
    }

    /// <summary>Removes a wrongly entered record from every report. The row is kept, marked as voided.</summary>
    public async Task<bool> VoidAsync(long recordId, CancellationToken ct) =>
        await db.ServiceRecords.Where(r => r.Id == recordId && r.VoidedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.VoidedAtUtc, UtcNow), ct) > 0;

    // ---------------------------------------------------------------- reports

    public async Task<Report> GetReportAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        var calendar = await settings.GetCalendarAsync(ct);
        var from = calendar.DayRangeUtc(fromDate).StartUtc;
        var to = calendar.DayRangeUtc(toDate).EndUtc;

        var records = await db.ServiceRecords.AsNoTracking().Include(r => r.Barber)
            .Where(r => r.VoidedAtUtc == null && r.PerformedAtUtc >= from && r.PerformedAtUtc < to).ToListAsync(ct);
        var noShows = await db.Bookings.CountAsync(b => b.Status == BookingStatus.NoShow && b.StartAtUtc >= from && b.StartAtUtc < to, ct);
        var cancelled = await db.Bookings.CountAsync(b => b.Status == BookingStatus.Cancelled && b.StartAtUtc >= from && b.StartAtUtc < to, ct);

        // One customer visit = one booking, or one walk-in entry (same time and customer).
        string VisitKey(ServiceRecord r) => r.BookingId is { } b ? $"b{b}" : $"v{r.VisitId}";

        var byBarber = records.GroupBy(r => r.BarberId)
            .Select(g => new BarberReport(g.Key, g.First().Barber?.DisplayName ?? $"#{g.Key}", g.Sum(r => r.Price), g.Count(), g.Select(VisitKey).Distinct().Count()))
            .OrderByDescending(b => b.Revenue).ToList();
        var byService = records.GroupBy(r => r.ServiceName)
            .Select(g => new ServiceReport(g.Key, g.Count(), g.Sum(r => r.Price))).OrderByDescending(s => s.Revenue).ThenBy(s => s.Service).ToList();
        var byDay = records.GroupBy(r => DateOnly.FromDateTime(calendar.ToLocal(r.PerformedAtUtc)))
            .OrderBy(g => g.Key).Select(g => new DayReport(g.Key.ToString("yyyy-MM-dd"), g.Sum(r => r.Price), g.Count())).ToList();

        var revenue = records.Sum(r => r.Price);
        var visits = records.Select(VisitKey).Distinct().Count();
        return new Report(fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"), revenue, records.Count, visits,
            visits == 0 ? 0 : Math.Round(revenue / visits, 2), noShows, cancelled, byBarber, byService, byDay);
    }

    // ---------------------------------------------------------------- helpers

    private static DashboardResult? Validate(List<ServiceLine>? services)
    {
        if (services is null or { Count: 0 }) return new DashboardResult(DashboardError.Invalid, "Pick at least one service.");
        if (services.Count > MaxServicesPerEntry) return new DashboardResult(DashboardError.Invalid, $"At most {MaxServicesPerEntry} services at once.");
        foreach (var s in services)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 200) return new DashboardResult(DashboardError.Invalid, "Each service needs a name (up to 200 characters).");
            if (s.Price < 0 || s.Price > MaxPrice) return new DashboardResult(DashboardError.Invalid, $"A price must be between 0 and {MaxPrice:0}.");
        }
        return null;
    }

    private ServiceRecord NewRecord(ServiceLine line, int barberId, DateTime performedAtUtc, long? bookingId, string? customerName, Guid visitId) => new()
    {
        VisitId = visitId,
        ServiceName = line.Name.Trim(),
        Price = Math.Round(line.Price, 2),
        BarberId = barberId,
        BookingId = bookingId,
        CustomerName = customerName,
        PerformedAtUtc = performedAtUtc,
        CreatedAtUtc = UtcNow
    };

    private static RecordedService ToRecorded(ServiceRecord r) => new(r.Id, r.ServiceName, r.Price, r.BarberId, r.Barber?.DisplayName ?? $"#{r.BarberId}");
}
