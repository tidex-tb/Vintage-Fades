using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Operations;
using VintageFades.Web.Security;

namespace VintageFades.Web.Controllers;

/// <summary>Owner/operator API (not a dashboard). Requires the X-Admin-Key header.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(AuthenticationSchemes = AdminKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting("admin")]
public sealed class AdminController(AdminService admin, OperationalStatusService status, IWorkSignal signal) : ControllerBase
{
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
