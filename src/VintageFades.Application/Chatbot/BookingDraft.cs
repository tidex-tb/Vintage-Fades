using System.Text.Json;
using VintageFades.Domain;

namespace VintageFades.Application.Chatbot;

public enum DraftIntent
{
    None,
    Book,
    Reschedule,
    Cancel
}

public sealed record DraftOffer(DateOnly Date, TimeOnly Time, int BarberId);

/// <summary>Conversation context persisted between messages (Conversation.CurrentBookingIntent), so a customer
/// can continue where they left off without repeating themselves.</summary>
public sealed class BookingDraft
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DraftIntent Intent { get; set; }
    public DateOnly? Date { get; set; }
    public TimeOnly? Time { get; set; }
    public BarberPreferenceKind BarberMode { get; set; }
    public int? BarberId { get; set; }
    /// <summary>Barber chosen for a reschedule when the customer accepted "first available".</summary>
    public int? ResolvedBarberId { get; set; }
    public long? HeldBookingId { get; set; }
    public long? TargetBookingId { get; set; }
    public bool DateIsPeriod { get; set; }
    public List<DateOnly> DateCandidates { get; set; } = [];
    public List<TimeOnly> TimeCandidates { get; set; } = [];
    /// <summary>Open slots last listed for the chosen date; the customer picks one by its number.</summary>
    public List<TimeOnly> TimeChoices { get; set; } = [];
    public List<DraftOffer> Offers { get; set; } = [];
    public List<int> BarberChoices { get; set; } = [];
    public List<long> BookingChoices { get; set; } = [];
    public int ClarificationAttempts { get; set; }
    public ConversationState? ResumeState { get; set; }
    /// <summary>Set when the bot paused for a person (customer asked, or the shop replied from the WhatsApp Business app);
    /// the bot resumes after this time.</summary>
    public DateTime? AutoResumeAtUtc { get; set; }
    /// <summary>Set when the bot asked an idle customer whether they still want to continue. If they answer, the
    /// question is cleared; if they stay silent, the conversation is reset.</summary>
    public DateTime? IdleCheckSentAtUtc { get; set; }
    /// <summary>Set when the bot showed only part of the price list and offered "more"; the next message may ask for the rest.</summary>
    public bool MorePricesOffered { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static BookingDraft Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BookingDraft();
        try
        {
            return JsonSerializer.Deserialize<BookingDraft>(json, Json) ?? new BookingDraft();
        }
        catch (JsonException)
        {
            return new BookingDraft();
        }
    }
}
