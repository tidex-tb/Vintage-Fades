using System.Globalization;
using System.Text.RegularExpressions;

namespace VintageFades.Application.Chatbot;

public enum ParseKind
{
    None,
    Exact,
    /// <summary>More than one plausible reading; the bot must ask. Candidates holds the readings.</summary>
    Ambiguous,
    /// <summary>Customer named a period ("next week") rather than a day; Candidates holds its days.</summary>
    NeedsDay
}

public sealed record DateParseResult(ParseKind Kind, DateOnly? Date, IReadOnlyList<DateOnly> Candidates, string? Matched)
{
    public static readonly DateParseResult None = new(ParseKind.None, null, [], null);
}

public sealed record TimeParseResult(ParseKind Kind, TimeOnly? Time, IReadOnlyList<TimeOnly> Candidates, string? Matched)
{
    public static readonly TimeParseResult None = new(ParseKind.None, null, [], null);
}

/// <summary>Interprets customer date expressions relative to "today" in the shop's time zone. Never guesses:
/// ambiguous phrases ("next Friday", a bare weekday that is also today) return candidates to confirm.</summary>
public static partial class DateParser
{
    private static readonly Dictionary<string, int> Months = new()
    {
        ["jan"] = 1, ["january"] = 1, ["feb"] = 2, ["february"] = 2, ["mar"] = 3, ["march"] = 3, ["apr"] = 4, ["april"] = 4,
        ["may"] = 5, ["jun"] = 6, ["june"] = 6, ["jul"] = 7, ["july"] = 7, ["aug"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9, ["oct"] = 10, ["october"] = 10, ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12
    };

    private static readonly Dictionary<string, DayOfWeek> Weekdays = new()
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday
    };

    private const string MonthPattern = "january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec";
    private const string WeekdayPattern = "monday|tuesday|wednesday|thursday|friday|saturday|sunday|tues|thurs|thur|mon|tue|wed|thu|fri|sat|sun";

    [GeneratedRegex(@"\b(\d{4})-(\d{1,2})-(\d{1,2})\b")] private static partial Regex IsoDate();
    [GeneratedRegex(@"\b(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?\b")] private static partial Regex SlashDate();
    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)?(?:\s+of)?\s+(" + MonthPattern + @")\b(?:\s+(\d{4}))?")] private static partial Regex DayMonth();
    [GeneratedRegex(@"\b(" + MonthPattern + @")\s+(\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s+(\d{4}))?")] private static partial Regex MonthDay();
    [GeneratedRegex(@"\b(?:on\s+)?the\s+(\d{1,2})(?:st|nd|rd|th)\b|\b(\d{1,2})(?:st|nd|rd|th)\b")] private static partial Regex Ordinal();
    [GeneratedRegex(@"\b(?:the\s+)?day\s+after\s+(?:tomorrow|tmrw|tmr|tomorow)\b")] private static partial Regex DayAfterTomorrow();
    [GeneratedRegex(@"\b(?:tomorrow|tomorow|tommorow|tmrw|tmr)\b")] private static partial Regex Tomorrow();
    [GeneratedRegex(@"\b(?:today|tonight|this\s+(?:morning|afternoon|evening))\b")] private static partial Regex Today();
    [GeneratedRegex(@"\b(" + WeekdayPattern + @")\s+next\s+week\b|\bnext\s+week\s+(?:on\s+)?(" + WeekdayPattern + @")\b")] private static partial Regex WeekdayNextWeek();
    [GeneratedRegex(@"\bnext\s+week\b")] private static partial Regex NextWeek();
    [GeneratedRegex(@"\b(next|this|coming|on)?\s*\b(" + WeekdayPattern + @")\b")] private static partial Regex WeekdayExpr();

    public static DateParseResult Parse(string text, DateOnly today)
    {
        var t = text.ToLowerInvariant();
        Match m;

        if ((m = IsoDate().Match(t)).Success &&
            TryDate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out var iso))
            return Exact(iso, m.Value);

        if ((m = DayMonth().Match(t)).Success && TryDayMonth(m.Groups[1].Value, Months[m.Groups[2].Value], m.Groups[3].Value, today, out var dm))
            return Exact(dm, m.Value);

        if ((m = MonthDay().Match(t)).Success && TryDayMonth(m.Groups[2].Value, Months[m.Groups[1].Value], m.Groups[3].Value, today, out var md))
            return Exact(md, m.Value);

        // Namibia writes dates day-first: 7/11 is 7 November.
        if ((m = SlashDate().Match(t)).Success && TryDayMonth(m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value, today, out var sd))
            return Exact(sd, m.Value);

        if ((m = DayAfterTomorrow().Match(t)).Success) return Exact(today.AddDays(2), m.Value);
        if ((m = Tomorrow().Match(t)).Success) return Exact(today.AddDays(1), m.Value);
        if ((m = Today().Match(t)).Success) return Exact(today, m.Value);

        if ((m = WeekdayNextWeek().Match(t)).Success)
        {
            var day = Weekdays[m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value];
            var nextMonday = StartOfWeek(today).AddDays(7);
            return Exact(nextMonday.AddDays(((int)day + 6) % 7), m.Value);
        }

        if ((m = NextWeek().Match(t)).Success)
        {
            var nextMonday = StartOfWeek(today).AddDays(7);
            return new DateParseResult(ParseKind.NeedsDay, null, Enumerable.Range(0, 7).Select(nextMonday.AddDays).ToList(), m.Value);
        }

        foreach (Match w in WeekdayExpr().Matches(t))
        {
            var modifier = w.Groups[1].Value;
            var day = Weekdays[w.Groups[2].Value];
            var daysAhead = ((int)day - (int)today.DayOfWeek + 7) % 7;
            var upcoming = today.AddDays(daysAhead);

            if (modifier == "next")
            {
                // "Next Friday" means the coming Friday to some people and the one after to others: always confirm.
                var first = daysAhead == 0 ? today.AddDays(7) : upcoming;
                return new DateParseResult(ParseKind.Ambiguous, null, [first, first.AddDays(7)], w.Value.Trim());
            }
            if (daysAhead == 0 && modifier is not ("this" or "coming"))
                return new DateParseResult(ParseKind.Ambiguous, null, [today, today.AddDays(7)], w.Value.Trim());
            if (daysAhead == 0 && modifier == "coming")
                return Exact(today.AddDays(7), w.Value.Trim());
            return Exact(upcoming, w.Value.Trim());
        }

        if ((m = Ordinal().Match(t)).Success)
        {
            var day = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            var month = new DateOnly(today.Year, today.Month, 1);
            if (day < today.Day) month = month.AddMonths(1);
            if (TryDate(month.Year, month.Month, day, out var ordinal)) return Exact(ordinal, m.Value);
        }

        return DateParseResult.None;
    }

    private static DateParseResult Exact(DateOnly date, string matched) => new(ParseKind.Exact, date, [], matched);

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static bool TryDayMonth(string dayText, int month, string yearText, DateOnly today, out DateOnly date)
    {
        var day = int.Parse(dayText, CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(yearText))
        {
            var year = int.Parse(yearText, CultureInfo.InvariantCulture);
            if (year < 100) year += 2000;
            return TryDate(year, month, day, out date);
        }
        if (!TryDate(today.Year, month, day, out date)) return false;
        // A date earlier this year is kept as-is (and rejected as past) when it is recent; otherwise it means next year.
        if (date < today && today.DayNumber - date.DayNumber > 31)
            return TryDate(today.Year + 1, month, day, out date);
        return true;
    }

    private static bool TryDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month)) return false;
        date = new DateOnly(year, month, day);
        return true;
    }
}

/// <summary>Interprets customer time expressions. A bare hour ("at 2", "7") has a 12-hour reading and a 24-hour
/// reading; when exactly one of them is a bookable start time it is used, otherwise the bot asks.</summary>
public static partial class TimeParser
{
    [GeneratedRegex(@"\b(\d{1,2})\s*[:h.]\s*(\d{2})\s*(am|pm|a\.m\.?|p\.m\.?)?(?![\d/])")] private static partial Regex HourMinute();
    [GeneratedRegex(@"\b(\d{1,2})\s*(am|pm|a\.m\.?|p\.m\.?)(?!\w)")] private static partial Regex HourMeridiem();
    [GeneratedRegex(@"\b(\d{1,2})h(?!\w)")] private static partial Regex HourH();
    [GeneratedRegex(@"\b(noon|midday|lunchtime)\b")] private static partial Regex Noon();
    [GeneratedRegex(@"\b(?:at|for|around|about|by|from|@)\s*(\d{1,2})(?![\d:/]|\.\d|\s*(?:st|nd|rd|th|am|pm|people|persons?|guys|kids|of)\b)")] private static partial Regex ContextHour();
    [GeneratedRegex(@"^\s*(\d{1,2})\s*$")] private static partial Regex BareHour();

    public static TimeParseResult Parse(string text, IReadOnlyCollection<TimeOnly> bookableStarts, bool allowBareNumber)
    {
        var t = text.ToLowerInvariant();
        Match m;

        if ((m = HourMinute().Match(t)).Success)
        {
            var hour = int.Parse(m.Groups[1].Value);
            var minute = int.Parse(m.Groups[2].Value);
            if (minute > 59 || hour > 23) return TimeParseResult.None;
            if (m.Groups[3].Success) return WithMeridiem(hour, minute, m.Groups[3].Value, m.Value);
            // "09:00", "14:00" and "00:30" are unambiguous 24-hour times.
            if (hour >= 13 || hour == 0 || m.Groups[1].Value.StartsWith('0')) return Exact(new TimeOnly(hour, minute), m.Value);
            return Resolve(hour, minute, bookableStarts, m.Value);
        }

        if ((m = HourMeridiem().Match(t)).Success)
            return WithMeridiem(int.Parse(m.Groups[1].Value), 0, m.Groups[2].Value, m.Value);

        if ((m = HourH().Match(t)).Success)
        {
            var hour = int.Parse(m.Groups[1].Value);
            if (hour > 23) return TimeParseResult.None;
            return hour >= 13 || m.Groups[1].Value.StartsWith('0') ? Exact(new TimeOnly(hour, 0), m.Value) : Resolve(hour, 0, bookableStarts, m.Value);
        }

        if ((m = Noon().Match(t)).Success) return Exact(new TimeOnly(12, 0), m.Value);

        if ((m = ContextHour().Match(t)).Success || (allowBareNumber && (m = BareHour().Match(t)).Success))
        {
            var hour = int.Parse(m.Groups[1].Value);
            if (hour > 23) return TimeParseResult.None;
            return hour >= 13 || hour == 0 ? Exact(new TimeOnly(hour, 0), m.Value) : Resolve(hour, 0, bookableStarts, m.Value);
        }

        return TimeParseResult.None;
    }

    private static TimeParseResult WithMeridiem(int hour, int minute, string meridiem, string matched)
    {
        if (hour is < 1 or > 12) return TimeParseResult.None;
        var pm = meridiem.StartsWith('p');
        var h = hour % 12 + (pm ? 12 : 0);
        return Exact(new TimeOnly(h, minute), matched);
    }

    private static TimeParseResult Resolve(int hour, int minute, IReadOnlyCollection<TimeOnly> bookableStarts, string matched)
    {
        var candidates = hour == 12
            ? new List<TimeOnly> { new(12, minute) }
            : [new TimeOnly(hour, minute), new TimeOnly(hour + 12, minute)];
        if (candidates.Count == 1) return Exact(candidates[0], matched);

        var plausible = candidates.Where(c => IsWithinBookableDay(c, bookableStarts)).ToList();
        return plausible.Count == 1
            ? Exact(plausible[0], matched)
            : new TimeParseResult(ParseKind.Ambiguous, null, candidates, matched);
    }

    private static bool IsWithinBookableDay(TimeOnly time, IReadOnlyCollection<TimeOnly> starts) =>
        starts.Count > 0 && time >= starts.Min() && time <= starts.Max();

    private static TimeParseResult Exact(TimeOnly time, string matched) => new(ParseKind.Exact, time, [], matched);
}
