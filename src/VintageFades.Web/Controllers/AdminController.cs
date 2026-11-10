using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VintageFades.Application;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Operations;
using VintageFades.Web.Security;

namespace VintageFades.Web.Controllers;

/// <summary>Owner/operator API, used by the dashboard page at /dashboard and by scripts. Requires the X-Admin-Key header.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(AuthenticationSchemes = AdminKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting("admin")]
public sealed partial class AdminController(
    AdminService admin,
    DashboardService dashboard,
    BotSwitch botSwitch,
    BookingService bookingService,
    IOptions<ShopInfoOptions> shopInfo,
    OperationalStatusService status,
    IWorkSignal signal) : ControllerBase
{
    private const int MaxReportDays = 400;

    /// <summary>The price list as the dashboard offers it when recording what was done.</summary>
    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(shopInfo.Value.Catalog.Select(c => new
    {
        Category = c.Name,
        Items = c.Items.Select(i => new { i.Name, Price = ParsePrice(i.Price), Display = i.Price })
    }));

    /// <summary>Open slots for the phone-booking form. With no barberId, every barber's open slots are listed.</summary>
    [HttpGet("slots")]
    public async Task<IActionResult> Slots([FromQuery] DateOnly date, [FromQuery] int? barberId, CancellationToken ct) =>
        Ok((await bookingService.GetAvailableSlotsAsync(date, barberId, null, ct))
            .Select(s => new { Time = Format.Time(s.Time), s.BarberId, Barber = s.BarberName }));

    /// <summary>One day: every appointment with what was recorded for it, plus walk-ins and the day's revenue.</summary>
    [HttpGet("day")]
    public async Task<DaySchedule> Day([FromQuery] DateOnly date, CancellationToken ct) => await dashboard.GetDayAsync(date, ct);

    /// <summary>The master switch for the chatbot on every platform.</summary>
    [HttpGet("bot")]
    public async Task<BotStatus> GetBot(CancellationToken ct) => await botSwitch.GetAsync(ct);

    [HttpPut("bot")]
    public async Task<BotStatus> SetBot(BotRequest request, CancellationToken ct) => await botSwitch.SetPausedAsync(request.Paused, ct);

    public sealed record BotRequest(bool Paused);

    /// <summary>Seven days from the given date, for the dashboard's calendar grid.</summary>
    [HttpGet("week")]
    public async Task<IReadOnlyList<DaySchedule>> Week([FromQuery] DateOnly from, CancellationToken ct)
    {
        var days = new List<DaySchedule>(7);
        for (var i = 0; i < 7; i++) days.Add(await dashboard.GetDayAsync(from.AddDays(i), ct));
        return days;
    }

    [HttpPost("bookings/{reference}/complete")]
    public async Task<IActionResult> Complete(string reference, CompleteRequest request, CancellationToken ct) =>
        ToResponse(await dashboard.CompleteAsync(reference, request, ct));

    [HttpPost("bookings/{reference}/no-show")]
    public async Task<IActionResult> NoShow(string reference, CancellationToken ct) => ToResponse(await dashboard.MarkNoShowAsync(reference, ct));

    /// <summary>A customer who walked in without a booking.</summary>
    [HttpPost("sales")]
    public async Task<IActionResult> AddSale(SaleRequest request, CancellationToken ct) => ToResponse(await dashboard.AddSaleAsync(request, ct));

    [HttpDelete("sales/{id:long}")]
    public async Task<IActionResult> VoidSale(long id, CancellationToken ct) => await dashboard.VoidAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("reports")]
    public async Task<IActionResult> Reports([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        if (to < from) return ValidationProblem("The end date must not be before the start date.");
        if (to.DayNumber - from.DayNumber > MaxReportDays) return ValidationProblem($"Reports cover at most {MaxReportDays} days.");
        return Ok(await dashboard.GetReportAsync(from, to, ct));
    }

    private IActionResult ToResponse(DashboardResult result) => result.Error switch
    {
        DashboardError.None => NoContent(),
        DashboardError.NotFound => NotFound(),
        DashboardError.Invalid => ValidationProblem(result.Message ?? "Invalid request."),
        _ => Conflict(new { error = result.Error.ToString(), message = result.Message })
    };

    private static decimal ParsePrice(string display) =>
        decimal.TryParse(PriceDigits().Match(display).Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0m;

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial System.Text.RegularExpressions.Regex PriceDigits();

    [HttpGet("status")]
    public async Task<OperationalStatus> Status(CancellationToken ct) => await status.GetAsync(ct);

    [HttpGet("bookings")]
    public async Task<IReadOnlyList<BookingSummary>> Bookings([FromQuery] DateOnly date, CancellationToken ct) =>
        await admin.GetBookingsForDayAsync(date, ct);

    /// <summary>Manual booking (e.g. walk-in, phone call, or while a messaging channel is down). Uses the same booking engine.</summary>
    [HttpPost("bookings")]
    public async Task<IActionResult> CreateBooking(ManualBookingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerName) || request.CustomerName.Length > 100)
            return ValidationProblem("CustomerName is required (max 100 characters).");
        var (result, summary) = await admin.CreateManualBookingAsync(request, ct);
        signal.Notify(WorkQueue.Outbox);
        return result.Success ? Ok(summary) : Conflict(Describe(result));
    }

    [HttpPost("bookings/{reference}/cancel")]
    public async Task<IActionResult> CancelBooking(string reference, [FromBody] CancelRequest? request, CancellationToken ct)
    {
        var result = await admin.CancelAsync(reference, request?.Reason, ct);
        signal.Notify(WorkQueue.Outbox);
        return result.Success ? NoContent() : result.Error == BookingError.NotFound ? NotFound() : Conflict(Describe(result));
    }

    [HttpGet("conversations/attention")]
    public async Task<IReadOnlyList<object>> ConversationsAwaitingHuman(CancellationToken ct) => await admin.GetConversationsAwaitingHumanAsync(ct);

    /// <summary>Hands a conversation back to the bot after human handover.</summary>
    [HttpPost("conversations/{id:long}/resume")]
    public async Task<IActionResult> Resume(long id, CancellationToken ct) => await admin.ResumeConversationAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("barbers")]
    public async Task<IActionResult> Barbers(CancellationToken ct) => Ok((await admin.GetBarbersAsync(ct)).Select(b => new
    {
        b.Id, b.FirstName, b.LastName, b.DisplayName, b.IsActive, b.StationNumber,
        WorkingHours = b.WorkingHours.OrderBy(h => h.DayOfWeek).Select(h => new { Day = h.DayOfWeek.ToString(), h.OpeningTime, h.ClosingTime, h.IsWorking })
    }));

    [HttpPut("barbers/{id:int}")]
    public async Task<IActionResult> UpdateBarber(int id, BarberUpdate update, CancellationToken ct)
    {
        if (update.DisplayName is { Length: > 100 } || update.FirstName is { Length: > 100 } || update.LastName is { Length: > 100 })
            return ValidationProblem("Names are limited to 100 characters.");
        var barber = await admin.UpdateBarberAsync(id, update, ct);
        return barber is null ? NotFound() : Ok(new { barber.Id, barber.FirstName, barber.LastName, barber.DisplayName, barber.IsActive });
    }

    [HttpPost("barbers/{id:int}/time-off")]
    public async Task<IActionResult> AddTimeOff(int id, TimeOffRequest request, CancellationToken ct)
    {
        try
        {
            var (timeOff, overlapping) = await admin.AddTimeOffAsync(id, request, ct);
            return timeOff is null ? NotFound() : Ok(new { timeOff.Id, OverlappingConfirmedBookings = overlapping });
        }
        catch (ArgumentException ex)
        {
            return ValidationProblem(ex.Message);
        }
    }

    [HttpDelete("time-off/{id:long}")]
    public async Task<IActionResult> DeleteTimeOff(long id, CancellationToken ct) => await admin.DeleteTimeOffAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("outbox/failed")]
    public async Task<IReadOnlyList<object>> FailedOutbox(CancellationToken ct) => await admin.GetFailedOutboxAsync(ct);

    [HttpPost("outbox/{id:long}/retry")]
    public async Task<IActionResult> RetryOutbox(long id, CancellationToken ct)
    {
        var retried = await admin.RetryOutboxAsync(id, ct);
        if (retried) signal.Notify(WorkQueue.Outbox);
        return retried ? NoContent() : NotFound();
    }

    public sealed record CancelRequest(string? Reason);

    private static object Describe(BookingResult result) => new
    {
        error = result.Error.ToString(),
        rejection = result.Rejection == Domain.SlotRejection.None ? null : result.Rejection.ToString(),
        barber = result.BarberReason == Domain.BarberUnavailableReason.None ? null : result.BarberReason.ToString()
    };
}
