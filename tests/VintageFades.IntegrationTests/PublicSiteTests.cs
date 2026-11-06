using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

/// <summary>The customer website: it shows only free slots, books through the same engine as the bot and the dashboard,
/// and never double books.</summary>
[Collection(PostgresCollection.Name)]
public class PublicSiteTests(TestApp app) : IntegrationTest(app)
{
    private const string Today = "2026-11-02";     // the test clock starts Monday 2 Nov 2026, 08:00 in Windhoek
    private const string Tomorrow = "2026-11-03";

    // The site limits bookings per visitor address; every test uses its own address so they cannot affect each other.
    private static int _visitor;
    private readonly string _ip = "203.0.113." + Interlocked.Increment(ref _visitor);

    private Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Forwarded-For", _ip);
        return App.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> BookAsync(string date, string time, int? barberId, string name = "Tendai", string phone = "081 234 5678") =>
        PostAsync("/api/public/bookings", new { name, phone, date, time, barberId });

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", _ip);
        var response = await App.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task The_home_page_is_the_customer_site_with_strict_headers()
    {
        var page = await App.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Vintage Fades", await page.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await App.Client.GetAsync("/site.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await App.Client.GetAsync("/privacy.html")).StatusCode);
    }

    [Fact]
    public async Task Info_lists_the_barbers_and_the_price_list()
    {
        var info = await GetJsonAsync("/api/public/info");
        Assert.Equal(["Kapza", "Luther", "Godwin", "Tevohn"], info.GetProperty("barbers").EnumerateArray().Select(b => b.GetProperty("name").GetString()!));
        Assert.True(info.GetProperty("catalog").GetArrayLength() >= 6);
        Assert.Equal(Today, info.GetProperty("today").GetString());
    }

    [Fact]
    public async Task A_website_booking_blocks_the_slot_for_the_bot_and_for_other_visitors()
    {
        var response = await BookAsync(Tomorrow, "14:00", barberId: 1);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var confirmation = await BodyAsync(response);
        Assert.StartsWith("VF-2026-", confirmation.GetProperty("reference").GetString());
        Assert.Equal("Kapza", confirmation.GetProperty("barber").GetString());
        Assert.Equal("15:00", confirmation.GetProperty("endTime").GetString());

        // The bot sees it as taken.
        var reply = string.Join("\n", await App.SendAsync(Platform.WhatsApp, "264812345678", "Book me with Kapza tomorrow at 14:00"));
        Assert.Contains("Kapza is already booked at 14:00 on Tuesday, 3 November", reply);

        // Other visitors no longer see it for Kapza, but still see it for the other barbers.
        var kapza = await GetJsonAsync($"/api/public/slots?date={Tomorrow}&barberId=1");
        Assert.DoesNotContain(kapza.EnumerateArray(), s => s.GetProperty("time").GetString() == "14:00");
        var anyone = await GetJsonAsync($"/api/public/slots?date={Tomorrow}");
        var at14 = anyone.EnumerateArray().Single(s => s.GetProperty("time").GetString() == "14:00");
        Assert.DoesNotContain(at14.GetProperty("barbers").EnumerateArray(), b => b.GetProperty("name").GetString() == "Kapza");

        // And the same slot cannot be booked again.
        var again = await BookAsync(Tomorrow, "14:00", barberId: 1, name: "Someone else", phone: "081 999 0000");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_slot_booked_in_the_chat_disappears_from_the_website()
    {
        await App.SendAsync(Platform.WhatsApp, "264811112222", "Book me with Godwin tomorrow at 10:00");
        // The bot only holds the slot until confirmed; a held slot is already unavailable to the website.
        var slots = await GetJsonAsync($"/api/public/slots?date={Tomorrow}&barberId=3");
        Assert.DoesNotContain(slots.EnumerateArray(), s => s.GetProperty("time").GetString() == "10:00");
    }

    [Fact]
    public async Task Two_people_racing_for_the_same_slot_get_one_booking()
    {
        var results = await Task.WhenAll(
            BookAsync(Tomorrow, "11:00", 2, "A", "081 111 1111"),
            BookAsync(Tomorrow, "11:00", 2, "B", "081 222 2222"));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Availability_counts_open_slots_per_day_and_a_full_day_shows_zero()
    {
        var before = await GetJsonAsync("/api/public/availability?barberId=4");
        var day = before.EnumerateArray().Single(d => d.GetProperty("date").GetString() == Tomorrow);
        Assert.Equal(10, day.GetProperty("freeSlots").GetInt32());

        await BookAsync(Tomorrow, "09:00", 4, "Phil", "081 333 3333");
        var after = await GetJsonAsync("/api/public/availability?barberId=4");
        Assert.Equal(9, after.EnumerateArray().Single(d => d.GetProperty("date").GetString() == Tomorrow).GetProperty("freeSlots").GetInt32());
    }

    [Fact]
    public async Task Bad_input_is_rejected_and_the_hidden_field_traps_scripts()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(Tomorrow, "13:00", 1, name: " ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(Tomorrow, "13:00", 1, phone: "12")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(Today, "07:00", 1)).StatusCode); // before opening
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(Tomorrow, "13:30", 1)).StatusCode); // not on the hour

        var trapped = await PostAsync("/api/public/bookings", new { name = "Bot", phone = "081 444 4444", date = Tomorrow, time = "16:00", barberId = 1, website = "http://spam" });
        Assert.Equal(HttpStatusCode.OK, trapped.StatusCode);
        var slots = await GetJsonAsync($"/api/public/slots?date={Tomorrow}&barberId=1");
        Assert.Contains(slots.EnumerateArray(), s => s.GetProperty("time").GetString() == "16:00"); // nothing was booked
    }

    [Fact]
    public async Task One_phone_number_cannot_hold_more_than_three_upcoming_bookings()
    {
        foreach (var time in new[] { "09:00", "10:00", "11:00" })
            Assert.Equal(HttpStatusCode.OK, (await BookAsync(Tomorrow, time, 1, "Greedy", "081 777 7777")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(Tomorrow, "12:00", 1, "Greedy", "081 777 7777")).StatusCode);
    }

    [Fact]
    public async Task A_customer_can_look_up_and_cancel_only_with_the_matching_phone_number()
    {
        var reference = (await BodyAsync(await BookAsync(Tomorrow, "15:00", 3, "Lena", "0812 345 600"))).GetProperty("reference").GetString()!;

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/public/bookings/lookup", new { reference, phone = "081 000 0000" })).StatusCode);
        var found = await PostAsync("/api/public/bookings/lookup", new { reference, phone = "+264 81 234 5600" }); // same number, written differently
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal("Godwin", (await BodyAsync(found)).GetProperty("barber").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/public/bookings/cancel", new { reference, phone = "081 000 0000" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/api/public/bookings/cancel", new { reference, phone = "0812345600" })).StatusCode);

        var slots = await GetJsonAsync($"/api/public/slots?date={Tomorrow}&barberId=3");
        Assert.Contains(slots.EnumerateArray(), s => s.GetProperty("time").GetString() == "15:00"); // free again
    }

    [Fact]
    public async Task The_booking_shows_up_on_the_dashboard_as_a_website_booking()
    {
        await BookAsync(Tomorrow, "17:00", 2, "Web Wendy", "081 888 0001");
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/day?date={Tomorrow}");
        request.Headers.Add("X-Admin-Key", TestApp.AdminKey);
        var day = await BodyAsync(await App.Client.SendAsync(request));
        var item = day.GetProperty("appointments").EnumerateArray().Single(a => a.GetProperty("customer").GetString() == "Web Wendy");
        Assert.Equal("Web", item.GetProperty("source").GetString());
        Assert.Equal("+264818880001", item.GetProperty("phone").GetString());
    }
}
