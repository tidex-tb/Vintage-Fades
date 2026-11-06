namespace VintageFades.Application;

public sealed class BookingOptions
{
    public const string SectionName = "Booking";

    /// <summary>How long an offered slot is held (status Pending) while the customer confirms.</summary>
    public int PendingHoldMinutes { get; set; } = 10;
    public int MaxAlternatives { get; set; } = 5;
    /// <summary>Operational switches for the 1440-minute and 120-minute reminder offsets in ShopSettings.</summary>
    public bool Reminder24HoursEnabled { get; set; } = true;
    public bool Reminder2HoursEnabled { get; set; } = true;
}

public sealed class ChatbotOptions
{
    public const string SectionName = "Chatbot";

    /// <summary>An unfinished conversation older than this restarts from the menu.</summary>
    public int ConversationResetHours { get; set; } = 24;
    public int ClarificationAttemptsBeforeHumanOffer { get; set; } = 2;
    /// <summary>Messages the WhatsApp Business app sends by itself (greeting, away message) start with one of these.
    /// They are not a person replying, so they never pause the bot.</summary>
    public List<string> AutomatedMessagePrefixes { get; set; } = ["Thank you for contacting Vintage Fades", "Hi! Are you looking for a good Barber?"];
    /// <summary>After answering a first-contact question (for example a price), the bot waits this long for the customer's next
    /// message before sending the "What would you like to do?" menu.</summary>
    public int MenuDelayMinutes { get; set; } = 10;
    /// <summary>A customer who goes quiet in the middle of a booking is asked whether they want to continue after this long.</summary>
    public int IdleCheckMinutes { get; set; } = 10;
    /// <summary>If they still do not answer the question after this long, the booking session is reset.</summary>
    public int IdleResetMinutes { get; set; } = 10;
    /// <summary>When a customer asks for a person, or the shop replies from the WhatsApp Business app (Coexistence),
    /// the bot stays quiet in that chat this long; each further reply from the shop extends the pause.</summary>
    public int ManualReplyPauseMinutes { get; set; } = 10;
}

/// <summary>What the bot tells customers who ask about prices, services, hours, location or contact details.
/// Bound from the "ShopInfo" section, so the owner can change prices without a code change.</summary>
public sealed class ShopInfoOptions
{
    public const string SectionName = "ShopInfo";

    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Instagram { get; set; }
    /// <summary>Opening hours as free text. When empty, the bot describes the hours from the shop settings.</summary>
    public string? Hours { get; set; }
    /// <summary>The price list, grouped as on the printed menu.</summary>
    public List<CatalogCategory> Catalog { get; set; } = [];
    /// <summary>Short facts shown with the full price list (for example "Walk-ins are also welcome").</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>Answers to common questions, matched by keywords.</summary>
    public List<ShopFaq> Faqs { get; set; } = [];
}

public sealed class CatalogCategory
{
    public string Name { get; set; } = "";
    /// <summary>Optional line shown under the category when the whole category is listed.</summary>
    public string? Note { get; set; }
    public List<CatalogItem> Items { get; set; } = [];
}

public sealed class CatalogItem
{
    public string Name { get; set; } = "";
    public string Price { get; set; } = "";
    /// <summary>Optional detail shown after the price, e.g. what a treatment includes.</summary>
    public string? Details { get; set; }
    /// <summary>Words a customer might use for this service. Matched as whole words (plurals allowed).</summary>
    public List<string> Keywords { get; set; } = [];
}

public sealed class ShopFaq
{
    public List<string> Keywords { get; set; } = [];
    public string Answer { get; set; } = "";
}

/// <summary>Owner destination and approved WhatsApp templates. Bound from the "WhatsApp" section.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Owner's WhatsApp number in international format (digits, optional leading +). Must differ from the business number.</summary>
    public string? OwnerNotificationNumber { get; set; }
    public TemplateOptions OwnerNotificationTemplate { get; set; } = new();
    public TemplateOptions ReminderTemplate { get; set; } = new();

    public string? OwnerRecipient => PhoneNumbers.Normalize(OwnerNotificationNumber);
}

public sealed class TemplateOptions
{
    /// <summary>Approved template name. Empty = no template; free-form text is attempted instead.</summary>
    public string? Name { get; set; }
    public string Language { get; set; } = "en";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Name);
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";
    public int MaxAttempts { get; set; } = 8;
    public int BaseRetryDelaySeconds { get; set; } = 30;
    public int MaxRetryDelayMinutes { get; set; } = 60;
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";
    public int ConversationMessageDays { get; set; } = 90;
    public int WebhookEventDays { get; set; } = 30;
    public int OutboxMessageDays { get; set; } = 90;
    public int NotificationLogDays { get; set; } = 180;
}

public static class PhoneNumbers
{
    /// <summary>Digits only (WhatsApp wa_id format). Null when empty.</summary>
    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>"+264…" form that matches what WhatsApp stores. A local Namibian number (081 474 8665) gets the 264 code.
    /// Null when it is not a plausible phone number.</summary>
    public static string? ToInternational(string? phone, string defaultCountryCode = "264")
    {
        var digits = Normalize(phone);
        if (digits is null) return null;
        if (digits.StartsWith("00")) digits = digits[2..];
        else if (digits.StartsWith('0')) digits = defaultCountryCode + digits[1..];
        return digits.Length is >= 9 and <= 15 ? "+" + digits : null;
    }
}
