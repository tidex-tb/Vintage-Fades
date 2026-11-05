using VintageFades.Application.Chatbot;
using VintageFades.Domain;

namespace VintageFades.UnitTests;

public class DateParserTests
{
    // Monday 2 November 2026
    private static readonly DateOnly Today = new(2026, 11, 2);

    [Theory]
    [InlineData("today", "2026-11-02")]
    [InlineData("tomorrow please", "2026-11-03")]
    [InlineData("the day after tomorrow", "2026-11-04")]
    [InlineData("this friday", "2026-11-06")]
    [InlineData("friday", "2026-11-06")]
    [InlineData("on saturday", "2026-11-07")]
    [InlineData("7 november", "2026-11-07")]
    [InlineData("7th of november", "2026-11-07")]
    [InlineData("november 7", "2026-11-07")]
    [InlineData("7/11", "2026-11-07")]
    [InlineData("2026-11-07", "2026-11-07")]
    [InlineData("the 7th", "2026-11-07")]
    [InlineData("friday next week", "2026-11-13")]
    [InlineData("next week tuesday", "2026-11-10")]
    [InlineData("15 january", "2027-01-15")]
    public void Parses_exact_dates(string text, string expected)
    {
        var result = DateParser.Parse(text, Today);
        Assert.Equal(ParseKind.Exact, result.Kind);
        Assert.Equal(DateOnly.Parse(expected), result.Date);
    }

    [Fact]
    public void Next_weekday_is_ambiguous_and_offers_both_readings()
    {
        var result = DateParser.Parse("next friday", Today);
        Assert.Equal(ParseKind.Ambiguous, result.Kind);
        Assert.Equal([new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 13)], result.Candidates);
    }

    [Fact]
    public void Bare_weekday_matching_today_is_ambiguous()
    {
        var result = DateParser.Parse("monday", Today);
        Assert.Equal(ParseKind.Ambiguous, result.Kind);
        Assert.Equal([Today, Today.AddDays(7)], result.Candidates);
    }

    [Fact]
    public void Next_week_asks_for_a_day()
    {
        var result = DateParser.Parse("next week", Today);
        Assert.Equal(ParseKind.NeedsDay, result.Kind);
        Assert.Equal(7, result.Candidates.Count);
        Assert.Equal(new DateOnly(2026, 11, 9), result.Candidates[0]);
    }

    [Fact]
    public void Recently_passed_date_stays_in_the_past_so_it_can_be_rejected()
    {
        var result = DateParser.Parse("1 november", Today);
        Assert.Equal(new DateOnly(2026, 11, 1), result.Date);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("i want a haircut")]
    [InlineData("31/02")]
    public void Returns_none_when_no_date(string text) => Assert.Equal(ParseKind.None, DateParser.Parse(text, Today).Kind);
}

public class TimeParserTests
{
    private static readonly TimeOnly[] Slots = Enumerable.Range(9, 10).Select(h => new TimeOnly(h, 0)).ToArray();

    [Theory]
    [InlineData("14:00", 14, 0)]
    [InlineData("at 14h00", 14, 0)]
    [InlineData("09:00", 9, 0)]
    [InlineData("3pm", 15, 0)]
    [InlineData("i need a slot at 3 pm", 15, 0)]
    [InlineData("10am", 10, 0)]
    [InlineData("12pm", 12, 0)]
    [InlineData("noon", 12, 0)]
    [InlineData("book me for 2.", 14, 0)]
    [InlineData("at 10", 10, 0)]
    [InlineData("2:30pm", 14, 30)]
    [InlineData("16h", 16, 0)]
    public void Parses_times(string text, int hour, int minute)
    {
        var result = TimeParser.Parse(text, Slots, allowBareNumber: false);
        Assert.Equal(ParseKind.Exact, result.Kind);
        Assert.Equal(new TimeOnly(hour, minute), result.Time);
    }

    [Fact]
    public void Bare_number_is_only_a_time_when_a_time_is_expected()
    {
        Assert.Equal(ParseKind.None, TimeParser.Parse("2", Slots, allowBareNumber: false).Kind);
        Assert.Equal(new TimeOnly(14, 0), TimeParser.Parse("2", Slots, allowBareNumber: true).Time);
    }

    [Fact]
    public void Seven_is_ambiguous_because_neither_reading_is_bookable()
    {
        var result = TimeParser.Parse("book me at 7", Slots, allowBareNumber: false);
        Assert.Equal(ParseKind.Ambiguous, result.Kind);
        Assert.Equal([new TimeOnly(7, 0), new TimeOnly(19, 0)], result.Candidates);
    }

    [Theory]
    [InlineData("book for 2 people")]
    [InlineData("hello there")]
    public void Ignores_non_times(string text) => Assert.Equal(ParseKind.None, TimeParser.Parse(text, Slots, false).Kind);
}

public class IntentRecognizerTests
{
    [Theory]
    [InlineData("I want a haircut", Intent.Book)]
    [InlineData("Can I book?", Intent.Book)]
    [InlineData("Are you free tomorrow?", Intent.Book)]
    [InlineData("Cancel my appointment", Intent.Cancel)]
    [InlineData("Move my booking to Friday", Intent.Reschedule)]
    [InlineData("I want to speak to someone", Intent.Human)]
    [InlineData("Can I talk to the manager", Intent.Human)]
    [InlineData("call me please", Intent.Human)]
    [InlineData("hi", Intent.Greeting)]
    [InlineData("yes please", Intent.Yes)]
    [InlineData("nope", Intent.No)]
    [InlineData("show my bookings", Intent.ViewBookings)]
    [InlineData("menu", Intent.Menu)]
    [InlineData("lets restart", Intent.Menu)]
    [InlineData("can we start over", Intent.Menu)]
    [InlineData("how much is a taper with go black", Intent.Info)]
    [InlineData("what are your prices?", Intent.Info)]
    [InlineData("what time do you close", Intent.Info)]
    [InlineData("where are you located", Intent.Info)]
    public void Detects_intent(string text, Intent expected) =>
        Assert.True(IntentRecognizer.Recognize(IntentRecognizer.Normalize(text)).HasFlag(expected));

    [Theory]
    [InlineData("Are you free tomorrow?")]
    [InlineData("Can I book at 10?")]
    public void Availability_questions_are_not_info_questions(string text) =>
        Assert.False(IntentRecognizer.Recognize(IntentRecognizer.Normalize(text)).HasFlag(Intent.Info));

    [Fact]
    public void Not_sure_is_neither_yes_nor_no()
    {
        var intent = IntentRecognizer.Recognize(IntentRecognizer.Normalize("not sure"));
        Assert.False(intent.HasFlag(Intent.Yes));
    }
}

public class BarberMatcherTests
{
    private static readonly List<Barber> Barbers = Enumerable.Range(1, 4)
        .Select(i => new Barber { Id = i, FirstName = "Barber", LastName = i.ToString(), DisplayName = $"Barber {i}", StationNumber = i, IsActive = true })
        .ToList();

    [Theory]
    [InlineData("is barber 1 available", 1)]
    [InlineData("book me with barber 2 tomorrow at 15:00", 2)]
    [InlineData("barber4", 4)]
    public void Matches_display_names(string text, int expected)
    {
        var result = BarberMatcher.Match(text, Barbers);
        Assert.Equal(BarberPreferenceKind.Specific, result.Kind);
        Assert.Equal(expected, result.BarberId);
    }

    [Theory]
    [InlineData("anyone is fine")]
    [InlineData("first available")]
    [InlineData("doesn't matter")]
    public void Matches_any(string text) => Assert.Equal(BarberPreferenceKind.Any, BarberMatcher.Match(text, Barbers).Kind);

    [Fact]
    public void Matches_unique_real_first_name()
    {
        var named = Barbers.Select(b => b.Id == 3 ? new Barber { Id = 3, FirstName = "Tjeripo", DisplayName = "Tjeripo", StationNumber = 3, IsActive = true } : b).ToList();
        Assert.Equal(3, BarberMatcher.Match("can tjeripo do it", named).BarberId);
    }
}

public class NameParserTests
{
    [Theory]
    [InlineData("John", "John")]
    [InlineData("my name is john doe", "John Doe")]
    [InlineData("I'm Ndapewa", "Ndapewa")]
    public void Parses_names(string text, string expected) => Assert.Equal(expected, NameParser.Parse(text));

    [Theory]
    [InlineData("1")]
    [InlineData("x")]
    [InlineData("call me at 0811234567")]
    public void Rejects_non_names(string text) => Assert.Null(NameParser.Parse(text));
}
