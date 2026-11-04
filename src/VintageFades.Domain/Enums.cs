namespace VintageFades.Domain;

public enum Platform
{
    WhatsApp,
    Instagram,
    Messenger
}

public enum BookingStatus
{
    /// <summary>Temporary hold while the customer confirms. Occupies capacity until <see cref="Booking.HoldExpiresAtUtc"/>.</summary>
    Pending,
    Confirmed,
    Cancelled,
    Completed,
    NoShow
}

/// <summary>Channel the booking originated from. Admin covers manual bookings entered by the shop.</summary>
public enum BookingSource
{
    WhatsApp,
    Instagram,
    Messenger,
    Admin
}

public enum MessageDirection
{
    Inbound,
    Outbound
}

public enum MessageType
{
    Text,
    Interactive,
    Unsupported
}

public enum ProcessingStatus
{
    Pending,
    Processed,
    Ignored,
    Failed
}

public enum OutboxStatus
{
    Pending,
    Sent,
    /// <summary>Permanent failure or retries exhausted. Visible in diagnostics; never silently discarded.</summary>
    Failed,
    /// <summary>Intentionally not sent (e.g. outside the platform messaging window, booking no longer valid).</summary>
    Skipped
}

public enum NotificationStatus
{
    Sent,
    Failed,
    Skipped
}

public enum ConversationState
{
    Idle,
    Greeting,
    CollectingCustomerName,
    CollectingBookingDate,
    CollectingBookingTime,
    SelectingBarber,
    OfferingAlternatives,
    AwaitingBookingConfirmation,
    BookingConfirmed,
    ViewingBookings,
    AwaitingCancellationSelection,
    AwaitingCancellationConfirmation,
    Rescheduling,
    OfferingHumanSupport,
    HumanSupportRequested
}

public static class PlatformExtensions
{
    public static BookingSource ToBookingSource(this Platform platform) => platform switch
    {
        Platform.WhatsApp => BookingSource.WhatsApp,
        Platform.Instagram => BookingSource.Instagram,
        Platform.Messenger => BookingSource.Messenger,
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    public static string DisplayName(this Platform platform) => platform switch
    {
        Platform.WhatsApp => "WhatsApp",
        Platform.Instagram => "Instagram",
        Platform.Messenger => "Facebook Messenger",
        _ => platform.ToString()
    };
}
