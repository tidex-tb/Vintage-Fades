using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VintageFades.Application.Bookings;
using VintageFades.Domain;

namespace VintageFades.Application.Chatbot;

/// <summary>An answer to an information question. <paramref name="Partial"/> means it showed only part of the price list,
/// so the customer is offered the rest ("more").</summary>
public sealed record InfoAnswer(string Text, bool Partial = false);

/// <summary>
/// Answers questions that are not part of a booking: prices, what the shop offers, hours, location, contact details,
/// walk-ins and similar. It works on the customer's own words, so a first message like "how much is a kids cut" or
/// "where are you located" gets a direct answer without any greeting. It returns null when the text is not such a question.
/// </summary>
public sealed partial class ShopInfoResponder(IOptions<ShopInfoOptions> options)
{
    private readonly ShopInfoOptions _info = options.Value;

    /// <summary>Words that fit many services; they count for less than a specific word like "kids" or "facial".</summary>
    private static readonly HashSet<string> Generic = ["haircut", "hair cut", "cut", "trim", "shave", "styling", "men", "classic"];

    private const int MaxItems = 6;

    [GeneratedRegex(@"\b(how long|duration|how much time|how many minutes|how many hours|takes how long)\b")] private static partial Regex Duration();
    [GeneratedRegex(@"\b(where are you|where is the shop|where is your shop|where r u|where do you operate|location|located|address|directions|find you|get there|which street|how do i get)\b")] private static partial Regex Location();
    [GeneratedRegex(@"\b(opening hours|opening times?|working hours|business hours|hours of operation|closing times?|what time do you (?:open|close)|when do you (?:open|close)|are you (?:open|closed)|you open|open (?:today|now|tomorrow|on|until|till)|until what time|till what time|open late|close early)\b")] private static partial Regex Hours();
    [GeneratedRegex(@"\b(phone number|contact number|your number|contact details|contact info|contact you|email|e-mail|instagram|insta page|call you|call the shop|whatsapp number)\b")] private static partial Regex Contact();
    [GeneratedRegex(@"\b(price|prices|pricing|price list|pricelist|how much|cost|costs|costing|rates?|charge|charges|fees?|tariff|what do you charge)\b")] private static partial Regex Price();
    [GeneratedRegex(@"\b(what services|which services|what do you (?:offer|do|provide)|what else do you|services|treatments)\b")] private static partial Regex ServicesAsk();
    [GeneratedRegex(@"\b(do you (?:guys )?(?:do|offer|have|provide|sell|also do|also offer)|are there any)\b")] private static partial Regex DoYouDo();
    [GeneratedRegex(@"\b(info|information|details)\b")] private static partial Regex Info();
    [GeneratedRegex(@"^(how|what|where|when|which|who|do you|does|are you|is there|is it|can i|can you|could you|will you|would you|any)\b")] private static partial Regex QuestionStart();

    /// <summary>True when the text reads like a question (used where a plain word could just be someone's name).</summary>
    public static bool LooksLikeQuestion(string rawText, string normalized) => rawText.Contains('?') || QuestionStart().IsMatch(normalized);

    /// <summary>
    /// Returns the answer to an information question, or null if the text is not one. When the same message also carries a
    /// day, time or barber (<paramref name="hasBookingDetails"/>), only an opening-hours question is answered: anything else
    /// is a booking request ("do you have space tomorrow at 2?") and belongs to the booking flow.
    /// </summary>
    public InfoAnswer? TryAnswer(string normalized, ShopSettings settings, bool hasBookingDetails = false)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var text = Prepare(normalized);

        if (Hours().IsMatch(text)) return new(HoursAnswer(settings));
        if (hasBookingDetails) return null;

        if (Duration().IsMatch(text)) return new(DurationAnswer(settings));
        if (Contact().IsMatch(text)) return new(ContactAnswer());
        if (Location().IsMatch(text) && !Price().IsMatch(text)) return new(LocationAnswer());

        var faq = _info.Faqs.FirstOrDefault(f => f.Keywords.Any(k => ContainsWord(text, k)));
        if (faq is not null) return new(faq.Answer);

        var (matches, genericOnly) = MatchItems(text);
        if (Price().IsMatch(text)) return matches.Count > 0 ? ItemsAnswer(matches, genericOnly, asked: "price") : new(FullPrices());
        if (ServicesAsk().IsMatch(text)) return matches.Count > 0 ? ItemsAnswer(matches, genericOnly, asked: "service") : new(ServicesOverview());
        // "Do you do facials?" is answered only when the service is on the list; "do you have space tomorrow?" is a booking question.
        if (DoYouDo().IsMatch(text) && matches.Count > 0) return ItemsAnswer(matches, genericOnly, asked: "service");
        if (Info().IsMatch(text)) return matches.Count > 0 ? ItemsAnswer(matches, genericOnly, asked: "price") : new(FullPrices());
        return null;
    }

    /// <summary>"More", "show more", "full list": the customer wants the whole price list after a partial answer.</summary>
    public static bool IsMoreRequest(string normalized) => MoreRequest().IsMatch(normalized);

    [GeneratedRegex(@"^(more|show more|see more|more please|more prices|the rest|see all|show all|all|all prices|everything|full list|full price list|full catalog|full catalogue|catalog|catalogue)$")]
    private static partial Regex MoreRequest();

    // ---------------------------------------------------------------- answers

    private string DurationAnswer(ShopSettings s)
    {
        var minutes = s.AppointmentDurationMinutes;
        var how = minutes == 60 ? "one hour" : $"{minutes} minutes";
        return $"Appointments are booked in {how} slots. Some services, like the massages, can take a little longer or shorter — tell the barber when you arrive.";
    }

    private string LocationAnswer()
    {
        var text = new StringBuilder();
        text.AppendLine(string.IsNullOrWhiteSpace(_info.Address) ? "I don't have our address on hand — please message the shop directly." : $"Find us: {_info.Address}");
        if (!string.IsNullOrWhiteSpace(_info.Instagram)) text.AppendLine($"Instagram: {_info.Instagram}");
        return text.ToString().TrimEnd();
    }

    private string HoursAnswer(ShopSettings s) =>
        string.IsNullOrWhiteSpace(_info.Hours)
            ? $"Opening hours: {Format.Time(s.OpeningTime)} – {Format.Time(s.ClosingTime)}"
            : $"Opening hours: {_info.Hours}";

    private string ContactAnswer()
    {
        var text = new StringBuilder("You can reach us here:\n");
        if (!string.IsNullOrWhiteSpace(_info.Phone)) text.AppendLine($"• Phone / WhatsApp: {_info.Phone}");
        if (!string.IsNullOrWhiteSpace(_info.Email)) text.AppendLine($"• Email: {_info.Email}");
        if (!string.IsNullOrWhiteSpace(_info.Instagram)) text.AppendLine($"• Instagram: {_info.Instagram}");
        return text.ToString().TrimEnd();
    }

    /// <summary>The whole price list, grouped by category, with the shop's notes.</summary>
    public string FullPrices()
    {
        if (_info.Catalog.Count == 0) return "Please message the shop directly for our price list.";
        var text = new StringBuilder("Our price list:\n");
        foreach (var category in _info.Catalog) AppendCategory(text, category);
        foreach (var note in _info.Notes) text.AppendLine($"• {note}");
        return text.ToString().TrimEnd();
    }

    private string ServicesOverview()
    {
        if (_info.Catalog.Count == 0) return "Please message the shop directly to ask about our services.";
        var text = new StringBuilder("Here's what we offer:\n");
        foreach (var category in _info.Catalog) text.AppendLine($"• {category.Name} ({category.Items.Count})");
        text.Append("\nAsk me for the price of any of them, or say *price list* to see everything.");
        return text.ToString();
    }

    /// <summary>A partial answer (the engine adds "reply *more* for the full price list"). When the customer only used a general
    /// word like "haircut", or too many services match, the whole category is shown instead of a handful of lines.</summary>
    private InfoAnswer ItemsAnswer(IReadOnlyList<(CatalogCategory Category, CatalogItem Item)> matches, bool genericOnly, string asked)
    {
        if (genericOnly || matches.Count > MaxItems)
        {
            // The category most of the matches belong to, listed in full ("haircut price" shows every haircut service).
            var best = matches.GroupBy(m => m.Category).OrderByDescending(g => g.Count()).First().Key;
            var text = new StringBuilder();
            AppendCategory(text, best);
            return new InfoAnswer(text.ToString().Trim(), Partial: true);
        }

        var lines = new StringBuilder(asked == "service" ? "Yes, we do:\n" : "");
        foreach (var (category, item) in matches)
        {
            lines.AppendLine($"• {item.Name} – {item.Price}{(string.IsNullOrWhiteSpace(item.Details) ? "" : $" ({item.Details})")}");
            if (!string.IsNullOrWhiteSpace(category.Note) && matches.Count(m => m.Category == category) == matches.Count)
                lines.AppendLine(category.Note);
        }
        return new InfoAnswer(lines.ToString().TrimEnd(), Partial: true);
    }

    private static void AppendCategory(StringBuilder text, CatalogCategory category)
    {
        text.AppendLine().AppendLine($"*{category.Name}*");
        foreach (var item in category.Items)
            text.AppendLine($"• {item.Name} – {item.Price}{(string.IsNullOrWhiteSpace(item.Details) ? "" : $" ({item.Details})")}");
        if (!string.IsNullOrWhiteSpace(category.Note)) text.AppendLine(category.Note);
    }

    // ---------------------------------------------------------------- matching

    /// <summary>The services whose keywords best match the customer's words. Specific words outweigh generic ones.</summary>
    private (List<(CatalogCategory Category, CatalogItem Item)> Items, bool GenericOnly) MatchItems(string text)
    {
        var scored = new List<(CatalogCategory Category, CatalogItem Item, double Score, double Specific)>();
        foreach (var category in _info.Catalog)
        {
            foreach (var item in category.Items)
            {
                double score = 0, specific = 0;
                foreach (var keyword in item.Keywords)
                {
                    if (!ContainsWord(text, keyword)) continue;
                    if (Generic.Contains(keyword.ToLowerInvariant())) { score += 0.5; continue; }
                    var weight = keyword.Count(c => c == ' ') + 1;
                    score += weight;
                    specific += weight;
                }
                if (score > 0) scored.Add((category, item, score, specific));
            }
        }
        if (scored.Count == 0) return ([], false);
        var best = scored.Max(s => s.Score);
        var winners = scored.Where(s => s.Score >= best).ToList();
        // Nothing but general words matched ("haircut", "cut"): the customer didn't name a particular service.
        return (winners.Select(s => (s.Category, s.Item)).ToList(), winners.All(s => s.Specific == 0));
    }

    /// <summary>Whole-word match of a keyword or phrase; a trailing "s" or "es" on the customer's word is allowed.</summary>
    private static bool ContainsWord(string text, string keyword) =>
        !string.IsNullOrWhiteSpace(keyword) &&
        Regex.IsMatch(text, $@"\b{Regex.Escape(keyword.ToLowerInvariant().Trim())}(?:s|es)?\b");

    /// <summary>Smooths spelling variants so "hair cut", "blonde" and "walk in" meet the catalog's spelling.</summary>
    private static string Prepare(string normalized) =>
        normalized.Replace("hair cut", "haircut").Replace("blonde", "blond").Replace("walk in", "walk-in").Replace("walkin", "walk-in")
            .Replace("colour", "color").Replace("inhancement", "enhancement");
}
