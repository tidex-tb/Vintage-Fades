using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

/// <summary>The owner dashboard: phone bookings that block slots for the bot, recording what was done and by whom, walk-ins, no-shows and reports.</summary>
[Collection(PostgresCollection.Name)]
public class DashboardTests(TestApp app) : IntegrationTest(app)
{
    private const string Today = "2026-11-02";     // the test clock starts Monday 2 Nov 2026, 08:00 in Windhoek
    private const string Tomorrow = "2026-11-03";

    private async Task<HttpResponseMessage> CallAsync(HttpMethod method, string path, object? body = null, bool withKey = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (withKey) request.Headers.Add("X-Admin-Key", TestApp.AdminKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await App.Client.SendAsync(request);
    }

    private async Task<JsonElement> JsonAsync(string path)
    {
        var response = await CallAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<string> PhoneBookingAsync(string date, string time, int barberId, string name = "Peter")
    {
        var response = await CallAsync(HttpMethod.Post, "/api/admin/bookings", new { customerName = name, phoneNumber = "081 555 0100", date, time, barberId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("reference").GetString()!;
    }

    private static object Services(params (string Name, decimal Price)[] lines) => lines.Select(l => new { name = l.Name, price = l.Price }).ToArray();

    [Fact]
    public async Task The_dashboard_page_is_served_with_strict_headers_and_the_api_needs_the_key()
    {
        var page = await App.Client.GetAsync("/dashboard/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("<title>Vintage Fades</title>", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await App.Client.GetAsync("/dashboard/app.js")).StatusCode);

        foreach (var path in new[] { "/api/admin/day?date=" + Today, "/api/admin/catalog", "/api/admin/reports?from=" + Today + "&to=" + Today, "/api/admin/slots?date=" + Today })
            Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(HttpMethod.Get, path, withKey: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(HttpMethod.Post, "/api/admin/sales", new { barberId = 1, services = Services(("Beard trim", 30m)) }, withKey: false)).StatusCode);
    }

    [Fact]
    public async Task A_phone_booking_blocks_the_slot_for_the_bot()
    {
        await PhoneBookingAsync(Tomorrow, "14:00:00", barberId: 1);

        var reply = string.Join("\n", await App.SendAsync(Platform.WhatsApp, "264812345678", "Book me with Kapza tomorrow at 14:00"));
        Assert.Contains("Kapza is already booked at 14:00 on Tuesday, 3 November", reply);

        var slots = await JsonAsync($"/api/admin/slots?date={Tomorrow}&barberId=1");
        Assert.DoesNotContain(slots.EnumerateArray(), s => s.GetProperty("time").GetString() == "14:00");
        Assert.Contains(slots.EnumerateArray(), s => s.GetProperty("time").GetString() == "15:00");
    }

    [Fact]
    public async Task Recording_a_finished_appointment_credits_the_barber_who_did_the_work()
    {
        var reference = await PhoneBookingAsync(Today, "09:00:00", barberId: 1); // booked with Kapza
        App.Time.Advance(TimeSpan.FromMinutes(70)); // it is now 09:10 in Windhoek

        // Luther actually cut the hair: haircut + beard trim, with a small discount on the haircut.
        var complete = await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/complete",
            new { barberId = 2, services = Services(("Classic men's haircut", 140m), ("Beard trim", 30m)) });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var day = await JsonAsync($"/api/admin/day?date={Today}");
        var item = Assert.Single(day.GetProperty("appointments").EnumerateArray());
        Assert.Equal("Completed", item.GetProperty("status").GetString());
        Assert.Equal(170m, item.GetProperty("total").GetDecimal());
        Assert.All(item.GetProperty("services").EnumerateArray(), s => Assert.Equal("Luther", s.GetProperty("barber").GetString()));
        Assert.Equal(170m, day.GetProperty("revenue").GetDecimal());

        var report = await JsonAsync($"/api/admin/reports?from={Today}&to={Today}");
        Assert.Equal(170m, report.GetProperty("revenue").GetDecimal());
        Assert.Equal(1, report.GetProperty("visits").GetInt32());
        Assert.Equal(2, report.GetProperty("services").GetInt32());
        var barber = Assert.Single(report.GetProperty("byBarber").EnumerateArray());
        Assert.Equal("Luther", barber.GetProperty("barber").GetString());
        Assert.Equal(170m, barber.GetProperty("revenue").GetDecimal());
        Assert.Equal("Classic men's haircut", report.GetProperty("byService")[0].GetProperty("service").GetString());

        // Adding a forgotten service to the same appointment later is allowed.
        var more = await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/complete", new { services = Services(("Hollywood waxing", 250m)) });
        Assert.Equal(HttpStatusCode.NoContent, more.StatusCode);
        Assert.Equal(420m, (await JsonAsync($"/api/admin/reports?from={Today}&to={Today}")).GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task An_appointment_that_has_not_started_cannot_be_closed_or_marked_no_show()
    {
        var reference = await PhoneBookingAsync(Tomorrow, "11:00:00", barberId: 3);
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/complete", new { services = Services(("Beard trim", 30m)) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/no-show")).StatusCode);

        // The slot is still taken: nothing was freed by the failed attempts.
        var slots = await JsonAsync($"/api/admin/slots?date={Tomorrow}&barberId=3");
        Assert.DoesNotContain(slots.EnumerateArray(), s => s.GetProperty("time").GetString() == "11:00");
    }

    [Fact]
    public async Task No_shows_are_counted_and_never_earn_anything()
    {
        var reference = await PhoneBookingAsync(Today, "09:00:00", barberId: 4);
        App.Time.Advance(TimeSpan.FromMinutes(90));
        Assert.Equal(HttpStatusCode.NoContent, (await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/no-show")).StatusCode);

        var report = await JsonAsync($"/api/admin/reports?from={Today}&to={Today}");
        Assert.Equal(1, report.GetProperty("noShows").GetInt32());
        Assert.Equal(0m, report.GetProperty("revenue").GetDecimal());
        // A no-show cannot then be recorded as done.
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/complete", new { services = Services(("Beard trim", 30m)) })).StatusCode);
    }

    [Fact]
    public async Task Walk_in_sales_appear_in_the_day_and_the_reports_and_a_mistake_can_be_voided()
    {
        var response = await CallAsync(HttpMethod.Post, "/api/admin/sales",
            new { barberId = 3, customerName = "Walk-in Sam", services = Services(("Students haircut", 120m), ("Full black dye", 100m)), date = Today });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var day = await JsonAsync($"/api/admin/day?date={Today}");
        var walkIn = Assert.Single(day.GetProperty("walkIns").EnumerateArray());
        Assert.Equal("Walk-in Sam", walkIn.GetProperty("customer").GetString());
        Assert.Equal(220m, walkIn.GetProperty("total").GetDecimal());

        var report = await JsonAsync($"/api/admin/reports?from={Today}&to={Today}");
        Assert.Equal(220m, report.GetProperty("revenue").GetDecimal());
        Assert.Equal(1, report.GetProperty("visits").GetInt32()); // two services, one customer
        Assert.Equal("Godwin", report.GetProperty("byBarber")[0].GetProperty("barber").GetString());

        var mistakeId = walkIn.GetProperty("services")[1].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await CallAsync(HttpMethod.Delete, $"/api/admin/sales/{mistakeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(HttpMethod.Delete, $"/api/admin/sales/{mistakeId}")).StatusCode);
        Assert.Equal(120m, (await JsonAsync($"/api/admin/reports?from={Today}&to={Today}")).GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task Reports_total_a_week_by_barber_and_by_day()
    {
        for (var i = 0; i < 3; i++)
        {
            var date = $"2026-10-{26 + i}"; // earlier days, entered afterwards (catching up)
            await CallAsync(HttpMethod.Post, "/api/admin/sales", new { barberId = 1, services = Services(("Classic men's haircut", 150m)), date });
            await CallAsync(HttpMethod.Post, "/api/admin/sales", new { barberId = 2, services = Services(("Beard trim", 30m)), date });
        }

        var report = await JsonAsync("/api/admin/reports?from=2026-10-26&to=2026-11-01");
        Assert.Equal(540m, report.GetProperty("revenue").GetDecimal());
        Assert.Equal(6, report.GetProperty("visits").GetInt32());
        Assert.Equal(90m, report.GetProperty("byBarber")[1].GetProperty("revenue").GetDecimal());
        Assert.Equal("Kapza", report.GetProperty("byBarber")[0].GetProperty("barber").GetString());
        Assert.Equal(3, report.GetProperty("byDay").GetArrayLength());
        Assert.Equal(180m, report.GetProperty("byDay")[0].GetProperty("revenue").GetDecimal());
        Assert.Equal(180m, (await JsonAsync("/api/admin/reports?from=2026-10-27&to=2026-10-27")).GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task Bad_input_is_rejected()
    {
        var reference = await PhoneBookingAsync(Today, "09:00:00", barberId: 1);
        App.Time.Advance(TimeSpan.FromMinutes(70));

        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/complete", new { services = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/complete", new { services = Services(("Beard trim", -5m)) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(HttpMethod.Post, $"/api/admin/bookings/{reference}/complete", new { barberId = 99, services = Services(("Beard trim", 30m)) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(HttpMethod.Post, "/api/admin/bookings/VF-0000-000000/complete", new { services = Services(("Beard trim", 30m)) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(HttpMethod.Post, "/api/admin/sales", new { barberId = 1, services = Services(("Beard trim", 30m)), date = "2026-12-25" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(HttpMethod.Get, "/api/admin/reports?from=2026-11-05&to=2026-11-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(HttpMethod.Get, "/api/admin/reports?from=2020-01-01&to=2026-11-01")).StatusCode);

        // Nothing was recorded by any of the rejected calls.
        Assert.Equal(0m, (await JsonAsync($"/api/admin/reports?from={Today}&to={Today}")).GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task The_catalog_endpoint_gives_the_price_list_with_numeric_prices()
    {
        var catalog = await JsonAsync("/api/admin/catalog");
        var items = catalog.EnumerateArray().SelectMany(c => c.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(26, items.Count);
        var beard = items.Single(i => i.GetProperty("name").GetString() == "Beard trim");
        Assert.Equal(30m, beard.GetProperty("price").GetDecimal());
        Assert.Equal("N$30", beard.GetProperty("display").GetString());
    }
}
