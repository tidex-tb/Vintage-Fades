namespace VintageFades.Domain;

public class Barber
{
    public int Id { get; set; }
    public required string FirstName { get; set; }
    public string? LastName { get; set; }
    public required string DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public int StationNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<BarberWorkingHours> WorkingHours { get; set; } = [];
    public List<BarberTimeOff> TimeOff { get; set; } = [];
}

public class BarberWorkingHours
{
    public int Id { get; set; }
    public int BarberId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly OpeningTime { get; set; }
    public TimeOnly ClosingTime { get; set; }
    public bool IsWorking { get; set; } = true;
}

public class BarberTimeOff
{
    public long Id { get; set; }
    public int BarberId { get; set; }
    public DateTime StartAtUtc { get; set; }
    public DateTime EndAtUtc { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class Customer
{
    public long Id { get; set; }
    public string? FullName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? WhatsAppId { get; set; }
    public string? InstagramScopedUserId { get; set; }
    public string? FacebookScopedUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One service the shop actually performed (and was paid for). The owner records these from the dashboard, which is what
/// makes revenue per barber, per service and per day possible. A record belongs to a booking, or stands alone for a walk-in.</summary>
public class ServiceRecord
{
    public long Id { get; set; }
    public long? BookingId { get; set; }
    public Booking? Booking { get; set; }
    /// <summary>Groups the services entered together for one customer, so a walk-in who had two services counts as one visit.</summary>
    public Guid VisitId { get; set; }
    /// <summary>The barber who did the work. This can differ from the barber the customer booked.</summary>
    public int BarberId { get; set; }
    public Barber? Barber { get; set; }
    public required string ServiceName { get; set; }
    public decimal Price { get; set; }
    public string? CustomerName { get; set; }
    /// <summary>When the service happened: the appointment time for a booking, the entry time for a walk-in.</summary>
    public DateTime PerformedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Set when the owner deletes a wrong entry. Voided records stay in the table but are left out of every report.</summary>
    public DateTime? VoidedAtUtc { get; set; }
}

public class Booking
{
    /// <summary>Statuses that occupy a barber's slot. Pending only while its hold has not expired
    /// (expired holds are released before any competing insert; see BookingService).</summary>
    public static readonly BookingStatus[] OccupyingStatuses = [BookingStatus.Pending, BookingStatus.Confirmed];

    public long Id { get; set; }
    public required string BookingReference { get; set; }
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int BarberId { get; set; }
    public Barber? Barber { get; set; }
    public DateTime StartAtUtc { get; set; }
    public DateTime EndAtUtc { get; set; }
    public BookingStatus Status { get; set; }
    public BookingSource SourcePlatform { get; set; }
    public string? CustomerNotes { get; set; }
    public DateTime? HoldExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancellationReason { get; set; }

    public bool OccupiesSlotAt(DateTime nowUtc) =>
        Status == BookingStatus.Confirmed ||
        (Status == BookingStatus.Pending && HoldExpiresAtUtc > nowUtc);
}

public class Conversation
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Platform Platform { get; set; }
    /// <summary>Platform user id of the customer (WhatsApp wa_id, Instagram IGSID, Messenger PSID).</summary>
    public required string PlatformConversationId { get; set; }
    public ConversationState CurrentState { get; set; } = ConversationState.Idle;
    /// <summary>JSON-serialised booking draft (see Application BookingDraft).</summary>
    public string? CurrentBookingIntent { get; set; }
    public bool RequiresAttention { get; set; }
    public DateTime? LastMessageAtUtc { get; set; }
    /// <summary>Last customer-originated message. Drives the platform customer-service window.</summary>
    public DateTime? LastInboundAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class ConversationMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public Platform Platform { get; set; }
    public string? PlatformMessageId { get; set; }
    public MessageDirection Direction { get; set; }
    public MessageType MessageType { get; set; }
    public string? MessageContent { get; set; }
    public ProcessingStatus ProcessingStatus { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ShopSettings
{
    public int Id { get; set; }
    public required string ShopName { get; set; }
    public required string TimeZoneId { get; set; }
    public TimeOnly OpeningTime { get; set; }
    public TimeOnly ClosingTime { get; set; }
    public int AppointmentDurationMinutes { get; set; }
    public int BookingAdvanceDays { get; set; }
    public int CancellationNoticeHours { get; set; }
    /// <summary>Reminder offsets before the appointment, e.g. {1440, 120}.</summary>
    public int[] ReminderMinutesBefore { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class WebhookEvent
{
    public long Id { get; set; }
    public Platform Platform { get; set; }
    public string? ExternalEventId { get; set; }
    public required string PayloadHash { get; set; }
    /// <summary>Raw payload kept until processed, then cleared (data minimisation).</summary>
    public string? Payload { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public ProcessingStatus ProcessingStatus { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

public class NotificationLog
{
    public long Id { get; set; }
    public long? OutboxMessageId { get; set; }
    public required string NotificationType { get; set; }
    public required string Recipient { get; set; }
    public Platform Platform { get; set; }
    public string? MessageContent { get; set; }
    public NotificationStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    /// <summary>Latest delivery status reported by the provider (sent/delivered/read/failed).</summary>
    public string? DeliveryStatus { get; set; }
    public DateTime SentAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

public class OutboxMessage
{
    public long Id { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    /// <summary>Optional idempotency key (e.g. one reminder per booking/offset/start time).</summary>
    public string? DeduplicationKey { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public OutboxStatus Status { get; set; }
    public string? LastError { get; set; }
}
