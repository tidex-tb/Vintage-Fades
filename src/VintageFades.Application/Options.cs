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
    /// <summary>A customer who goes quiet in the middle of a booking is asked whether they want to continue after this long.</summary>
    public int IdleCheckMinutes { get; set; } = 10;
    /// <summary>If they still do not answer the question after this long, the booking session is reset.</summary>
    public int IdleResetMinutes { get; set; } = 10;
    /// <summary>When a customer asks for a person, or the shop replies from the WhatsApp Business app (Coexistence),
    /// the bot stays quiet in that chat this long; each further reply from the shop extends the pause.</summary>
    public int ManualReplyPauseMinutes { get; set; } = 10;
}

/// <summary>What the bot tells customers who ask about prices, hours or where the shop is.</summary>
public sealed class ShopInfoOptions
{
    public const string SectionName = "ShopInfo";

    public string? Address { get; set; }
    /// <summary>One line per service, e.g. "Haircut (adults): N$150".</summary>
    public List<string> Prices { get; set; } = [];
    /// <summary>Opening hours as free text. When empty, the bot describes the hours from the shop settings.</summary>
    public string? Hours { get; set; }
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
}
