using System.Text.Json;
using Microsoft.Extensions.Options;
using VintageFades.Application;
using VintageFades.Application.Chatbot;
using VintageFades.Domain;

namespace VintageFades.UnitTests;

/// <summary>Runs against the real price list in appsettings.json, so the tests check what customers will actually see.</summary>
public class ShopInfoResponderTests
{
    private static readonly ShopInfoResponder Responder = new(Options.Create(LoadShopInfo()));

    private static readonly ShopSettings Settings = new()
    {
        ShopName = "VINTAGE FADES BARBER SHOP", TimeZoneId = "Africa/Windhoek",
        OpeningTime = new TimeOnly(9, 0), ClosingTime = new TimeOnly(19, 0), AppointmentDurationMinutes = 60
    };

    private static ShopInfoOptions LoadShopInfo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "VintageFades.Web", "appsettings.json"))) dir = dir.Parent;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "VintageFades.Web", "appsettings.json")));
        return doc.RootElement.GetProperty("ShopInfo").Deserialize<ShopInfoOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string? Ask(string text, bool hasBookingDetails = false) =>
        Responder.TryAnswer(IntentRecognizer.Normalize(text), Settings, hasBookingDetails);

    [Fact]
    public void The_catalog_matches_the_printed_price_list()
    {
        var items = LoadShopInfo().Catalog.SelectMany(c => c.Items).ToList();
        Assert.Equal(26, items.Count); // UV light therapy is printed twice on the menu (N$100 both times) and listed once here
        Assert.All(items, i => Assert.Matches(@"^N\$\d+$", i.Price));
        Assert.Equal("N$800", items.Single(i => i.Name == "Swedish massage").Price);
        Assert.Equal("N$30", items.Single(i => i.Name == "Beard trim").Price);
    }

    [Fact]
    public void Go_black_question_lists_the_three_black_services()
    {
        var answer = Ask("How much is a taper with go black");
        Assert.Contains("Adult's cut + go black – N$250", answer);
        Assert.Contains("Student's cut + go black – N$220", answer);
        Assert.Contains("Full black dye – N$100", answer);
        Assert.DoesNotContain("Swedish", answer);
    }

    [Fact]
    public void Specific_words_beat_generic_ones()
    {
        var kids = Ask("how much is a kids cut?")!;
        Assert.Contains("Kids cut (below 11) – N$100", kids);
        Assert.DoesNotContain("Classic men's haircut", kids);

        var wax = Ask("how much for a hollywood wax")!;
        Assert.Contains("Hollywood waxing – N$250", wax);
        Assert.DoesNotContain("Underarm", wax);

        Assert.Contains("Swedish massage – N$800", Ask("swedish massage price?"));
    }

    [Fact]
    public void A_general_haircut_price_question_lists_the_haircuts()
    {
        var answer = Ask("how much is a haircut")!;
        Assert.Contains("Classic men's haircut – N$150", answer);
        Assert.Contains("Haircut + beard trim – N$180", answer);
    }

    [Fact]
    public void Price_list_and_vague_requests_show_everything_with_the_notes()
    {
        foreach (var question in new[] { "price list", "what are your prices?", "how much do you charge", "Hello! Can I get more info on this?" })
        {
            var answer = Ask(question)!;
            Assert.Contains("Haircut services", answer);
            Assert.Contains("Massage services (30 minutes)", answer);
            Assert.Contains("Infrared light therapy – N$150", answer);
            Assert.Contains("hot towel", answer);
            Assert.Contains("walk-ins are also welcome", answer);
        }
    }

    [Fact]
    public void Location_hours_contact_and_duration_questions_get_direct_answers()
    {
        Assert.Contains("26 Van Rhijn Street", Ask("where are you located"));
        Assert.Contains("26 Van Rhijn Street", Ask("what's your address?"));
        Assert.Contains("Opening hours: 09:00 – 19:00", Ask("what time do you open"));
        Assert.Contains("Opening hours: 09:00 – 19:00", Ask("are you open tomorrow?", hasBookingDetails: true));

        var contact = Ask("what's your phone number")!;
        Assert.Contains("081 474 8665", contact);
        Assert.Contains("vintagebarber.na@gmail.com", contact);
        Assert.Contains("@vintage_fades_barbershop", contact);

        Assert.Contains("one hour", Ask("how long does a haircut take"));
    }

    [Fact]
    public void Do_you_do_questions_answer_only_for_services_on_the_list()
    {
        var facials = Ask("do you do facials?")!;
        Assert.StartsWith("Yes, we do:", facials);
        Assert.Contains("30 minute express facial for men – N$500", facials);
        Assert.Contains("vitamin C serum", facials);

        Assert.Contains("Waxing", Ask("what services do you offer"));
        Assert.Contains("walk-ins are welcome", Ask("do you take walk ins?"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hot towel", Ask("do you use a hot towel"));
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("John")]
    [InlineData("Can I book a haircut tomorrow")]
    [InlineData("I want a haircut at 2pm")]
    [InlineData("do you have space tomorrow at 2?")]
    [InlineData("do you have a slot on Friday")]
    [InlineData("Friday")]
    [InlineData("yes")]
    public void Booking_requests_and_other_messages_are_not_information_questions(string text) =>
        Assert.Null(Ask(text, hasBookingDetails: text.Contains("tomorrow") || text.Contains("2") || text.Contains("Friday")));
}
