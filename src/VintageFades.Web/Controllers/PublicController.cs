using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VintageFades.Application;
using VintageFades.Application.Bookings;

namespace VintageFades.Web.Controllers;

/// <summary>What the shop's public website uses. No login: it can only read open slots and make or cancel an appointment.</summary>
[ApiController]
[Route("api/public")]
[EnableRateLimiting("public")]
public sealed partial class PublicController(
    PublicBookingPortal publicBookings,
    BookingService bookingService,
    IOptions<ShopInfoOptions> shopInfo,
    TimeProvider time) : ControllerBase
{
    [HttpGet("info")]
    public async Task<IActionResult> Info(CancellationToken ct)
    {
        var calendar = await bookingService.GetCalendarAsync(ct);
        var s = calendar.Settings;
        var info = shopInfo.Value;
        var now = time.GetUtcNow().UtcDateTime;
        return Ok(new
        {
            shopName = s.ShopName,
            address = info.Address,
            phone = info.Phone,
            email = info.Email,
            instagram = info.Instagram,
            hours = string.IsNullOrWhiteSpace(info.Hours) ? $"{Format.Time(s.OpeningTime)} – {Format.Time(s.ClosingTime)}, every day" : info.Hours,
            durationMinutes = s.AppointmentDurationMinutes,
            today = calendar.Today(now).ToString("yyyy-MM-dd"),
            lastBookableDate = calendar.LastBookableDate(now).ToString("yyyy-MM-dd"),
            cancellationNoticeHours = s.CancellationNoticeHours,
            barbers = await publicBookings.GetBarbersAsync(ct),
            notes = info.Notes,
            catalog = info.Catalog.Select(c => new { category = c.Name, note = c.Note, items = c.Items.Select(i => new { name = i.Name, price = i.Price, details = i.Details }) })
        });
    }

    /// <summary>Open slot count per day, for the date picker.</summary>
    [HttpGet("availability")]
    public async Task<IActionResult> Availability([FromQuery] int? barberId, CancellationToken ct) =>
        Ok(await publicBookings.GetAvailabilityAsync(barberId, ct));

    /// <summary>Open times on one day. This is read straight from the database, so a slot booked on WhatsApp, Instagram,
    /// by phone or on this site is already gone.</summary>
    [HttpGet("slots")]
    public async Task<IActionResult> Slots([FromQuery] DateOnly date, [FromQuery] int? barberId, CancellationToken ct) =>
        Ok(await publicBookings.GetSlotsAsync(date, barberId, ct));

    [HttpPost("bookings")]
    [EnableRateLimiting("public-book")]
    public async Task<IActionResult> Book(BookRequest request, CancellationToken ct)
    {
        // Hidden field that real visitors never fill in; scripts that fill every field are dropped quietly.
        if (!string.IsNullOrEmpty(request.Website)) return Ok(new { reference = "VF-0000-000000" });
        var result = await publicBookings.BookAsync(
            new PublicBookingRequest(request.Name, request.Phone, request.Date, request.Time, request.BarberId, request.Notes), ct);
        return result.Success ? Ok(result.Confirmation) : Fail(result.Error);
    }

    [HttpPost("bookings/lookup")]
    [EnableRateLimiting("public-book")]
    public async Task<IActionResult> Lookup(LookupRequest request, CancellationToken ct) =>
        await publicBookings.FindAsync(request.Reference ?? "", request.Phone, ct) is { } found ? Ok(found) : Fail(PublicBookingError.NotFound);

    [HttpPost("bookings/cancel")]
    [EnableRateLimiting("public-book")]
    public async Task<IActionResult> Cancel(LookupRequest request, CancellationToken ct)
    {
        var error = await publicBookings.CancelAsync(request.Reference ?? "", request.Phone, ct);
        return error == PublicBookingError.None ? NoContent() : Fail(error);
    }

    public sealed record BookRequest(string? Name, string? Phone, DateOnly Date, TimeOnly Time, int? BarberId, string? Notes, string? Website);

    public sealed record LookupRequest(string? Reference, string? Phone);

    private IActionResult Fail(PublicBookingError error) => error switch
    {
        PublicBookingError.InvalidName => BadRequest(new { error = "InvalidName", message = "Please type your name." }),
        PublicBookingError.InvalidPhone => BadRequest(new { error = "InvalidPhone", message = "Please enter a valid phone number, for example 081 234 5678." }),
        PublicBookingError.SlotUnavailable => Conflict(new { error = "SlotUnavailable", message = "Sorry, that time was just taken. Please pick another." }),
        PublicBookingError.TooManyUpcoming => Conflict(new { error = "TooManyUpcoming", message = $"You already have {PublicBookingPortal.MaxUpcomingPerPhone} upcoming appointments. Please call the shop to change them." }),
        PublicBookingError.CannotCancel => Conflict(new { error = "CannotCancel", message = "This appointment is too close to cancel online. Please call the shop." }),
        _ => NotFound(new { error = "NotFound", message = "We could not find a booking with that reference and phone number." })
    };
}
