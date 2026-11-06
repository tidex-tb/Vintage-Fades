using System.Text.RegularExpressions;
using VintageFades.Domain;

namespace VintageFades.Application.Chatbot;

[Flags]
public enum Intent
{
    None = 0,
    Greeting = 1,
    Book = 2,
    ViewBookings = 4,
    Reschedule = 8,
    Cancel = 16,
    Human = 32,
    Yes = 64,
    No = 128,
    Thanks = 256,
    Menu = 512
}

/// <summary>Keyword-based intent detection. Deliberately conservative: it flags signals, the state machine decides.</summary>
public static partial class IntentRecognizer
{
    [GeneratedRegex(@"\b(human|staff|manager|owner|agent|real person|a person|speak to (?:someone|somebody|a person|someone at the shop)|talk to (?:someone|somebody|a person)|call me|phone me|ring me)\b")]
    private static partial Regex Human();
    [GeneratedRegex(@"^(hi+|hello+|hey+|hiya|howzit|good (?:morning|afternoon|evening|day)|greetings|yo|hallo|morning|afternoon)\b")]
    private static partial Regex Greeting();
    [GeneratedRegex(@"\b(cancel|cancellation|call off)\b")]
    private static partial Regex Cancel();
    [GeneratedRegex(@"\b(reschedule|re-schedule|move|postpone|change (?:my|the) (?:booking|appointment|time|date)|different (?:time|day|date)|shift (?:my|the))\b")]
    private static partial Regex Reschedule();
    [GeneratedRegex(@"\b(my (?:bookings?|appointments?)|view|check my|when is my|what time is my|upcoming)\b")]
    private static partial Regex View();
    [GeneratedRegex(@"\b(book|booking|appointment|appt|haircut|hair cut|cut|fade|trim|shave|beard|lineup|line up|slot|free|available|availability|open|space|reserve)\b")]
    private static partial Regex Book();
    [GeneratedRegex(@"^(yes|y|yeah|yea|ya|yep|yup|sure|ok|okay|k|confirm|confirmed|please do|go ahead|correct|perfect|sounds good|great|absolutely|definitely|that works|book it|do it)\b|👍")]
    private static partial Regex Yes();
    [GeneratedRegex(@"^(no|n|nope|nah|not really|don'?t|do not|never mind|nevermind|wrong|stop)\b")]
    private static partial Regex No();
    [GeneratedRegex(@"\b(thanks|thank you|thx|cheers|appreciate it|ta)\b")]
    private static partial Regex Thanks();
    [GeneratedRegex(@"\b(restart|re-start|start over|start again|begin again|from the beginning|from scratch|main menu)\b|^(menu|start|options|help|back)$")]
    private static partial Regex Menu();

    public static Intent Recognize(string normalized)
    {
        var intent = Intent.None;
        if (Human().IsMatch(normalized)) intent |= Intent.Human;
        if (Greeting().IsMatch(normalized)) intent |= Intent.Greeting;
        if (Cancel().IsMatch(normalized)) intent |= Intent.Cancel;
        if (Reschedule().IsMatch(normalized)) intent |= Intent.Reschedule;
        if (View().IsMatch(normalized)) intent |= Intent.ViewBookings;
        if (Book().IsMatch(normalized)) intent |= Intent.Book;
        var yes = Yes().IsMatch(normalized);
        var no = No().IsMatch(normalized);
        if (yes && !no) intent |= Intent.Yes;
        if (no && !yes) intent |= Intent.No;
        if (Thanks().IsMatch(normalized)) intent |= Intent.Thanks;
        if (Menu().IsMatch(normalized)) intent |= Intent.Menu;
        return intent;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}\s:/\-.'@👍]")] private static partial Regex Punctuation();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.Trim().ToLowerInvariant().Replace('’', '\'');
        lower = Punctuation().Replace(lower, " ");
        return Whitespace().Replace(lower, " ").Trim().TrimEnd('.');
    }
}

public enum BarberPreferenceKind
{
    None,
    Any,
    Specific
}

public sealed record BarberPreference(BarberPreferenceKind Kind, int? BarberId, string? Matched);

public static partial class BarberMatcher
{
    [GeneratedRegex(@"\b(any ?one|any ?body|any barber|any|first available|whoever|who ?ever is free|no preference|doesn'?t matter|does not matter|don'?t mind|do not mind|not fussed|either)\b")]
    private static partial Regex AnyBarber();

    public static BarberPreference Match(string normalized, IReadOnlyList<Barber> barbers)
    {
        foreach (var barber in barbers.Where(b => b.IsActive).OrderByDescending(b => b.DisplayName.Length))
        {
            var display = Regex.Escape(barber.DisplayName.ToLowerInvariant()).Replace(@"\ ", @"\s*");
            var m = Regex.Match(normalized, $@"\b{display}\b");
            if (m.Success) return new BarberPreference(BarberPreferenceKind.Specific, barber.Id, m.Value);
        }

        // First names only when unique and specific enough (placeholder names like "Barber" are shared).
        foreach (var group in barbers.Where(b => b.IsActive && b.FirstName.Length >= 3).GroupBy(b => b.FirstName.ToLowerInvariant()))
        {
            if (group.Count() != 1) continue;
            var m = Regex.Match(normalized, $@"\b{Regex.Escape(group.Key)}\b");
            if (m.Success) return new BarberPreference(BarberPreferenceKind.Specific, group.First().Id, m.Value);
        }

        var any = AnyBarber().Match(normalized);
        return any.Success ? new BarberPreference(BarberPreferenceKind.Any, null, any.Value) : new BarberPreference(BarberPreferenceKind.None, null, null);
    }
}

public static partial class NameParser
{
    [GeneratedRegex(@"^(?:(?:my name is|my name's|name is|name's|it's|its|it is|i am|i'm|im|this is|call me|put it under|under)\s+)?(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*){0,3})$")]
    private static partial Regex Name();

    public static string? Parse(string rawText)
    {
        var text = Regex.Replace(rawText.Trim().TrimEnd('.', '!'), @"\s+", " ");
        if (text.Length is < 2 or > 60) return null;
        var m = Name().Match(text.ToLowerInvariant());
        if (!m.Success) return null;
        var start = m.Groups["name"].Index;
        var name = text.Substring(start, m.Groups["name"].Length);
        return string.Join(' ', name.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
