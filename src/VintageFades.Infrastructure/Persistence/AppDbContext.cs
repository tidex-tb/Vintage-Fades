using Microsoft.EntityFrameworkCore;
using Npgsql;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public const string BookingOverlapConstraint = "EX_Bookings_Barber_NoOverlap";
    public const string BookingReferenceSequence = "booking_reference_seq";

    public DbSet<Barber> Barbers => Set<Barber>();
    public DbSet<BarberWorkingHours> BarberWorkingHours => Set<BarberWorkingHours>();
    public DbSet<BarberTimeOff> BarberTimeOff => Set<BarberTimeOff>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<ServiceRecord> ServiceRecords => Set<ServiceRecord>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<ShopSettings> ShopSettings => Set<ShopSettings>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public async Task<long> NextBookingNumberAsync(CancellationToken cancellationToken) =>
        await Database.SqlQueryRaw<long>($"SELECT nextval('\"{BookingReferenceSequence}\"') AS \"Value\"").SingleAsync(cancellationToken);

    // Exclusion constraints are checked after the index entry is written, so two transactions inserting conflicting
    // rows at the same instant can each wait for the other; PostgreSQL then aborts one with a deadlock error.
    public bool IsBookingConflict(DbUpdateException exception) => exception.InnerException is PostgresException pg &&
        ((pg.SqlState == PostgresErrorCodes.ExclusionViolation && pg.ConstraintName == BookingOverlapConstraint) ||
         pg.SqlState == PostgresErrorCodes.DeadlockDetected);

    public bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("btree_gist");
        model.HasSequence<long>(BookingReferenceSequence).StartsAt(1);

        model.Entity<Barber>(e =>
        {
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.HasIndex(x => x.StationNumber).IsUnique().HasFilter("\"IsActive\"");
            e.HasMany(x => x.WorkingHours).WithOne().HasForeignKey(x => x.BarberId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.TimeOff).WithOne().HasForeignKey(x => x.BarberId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<BarberWorkingHours>(e =>
        {
            e.HasIndex(x => new { x.BarberId, x.DayOfWeek }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_BarberWorkingHours_Range", "\"ClosingTime\" > \"OpeningTime\""));
        });

        model.Entity<BarberTimeOff>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(200);
            e.HasIndex(x => new { x.BarberId, x.StartAtUtc, x.EndAtUtc });
            e.ToTable(t => t.HasCheckConstraint("CK_BarberTimeOff_Range", "\"EndAtUtc\" > \"StartAtUtc\""));
        });

        model.Entity<Customer>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(100);
            e.Property(x => x.PhoneNumber).HasMaxLength(32);
            e.Property(x => x.WhatsAppId).HasMaxLength(64);
            e.Property(x => x.InstagramScopedUserId).HasMaxLength(64);
            e.Property(x => x.FacebookScopedUserId).HasMaxLength(64);
            e.HasIndex(x => x.WhatsAppId).IsUnique();
            e.HasIndex(x => x.InstagramScopedUserId).IsUnique();
            e.HasIndex(x => x.FacebookScopedUserId).IsUnique();
            e.HasIndex(x => x.PhoneNumber);
        });

        model.Entity<Booking>(e =>
        {
            e.Property(x => x.BookingReference).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SourcePlatform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CustomerNotes).HasMaxLength(500);
            e.Property(x => x.CancellationReason).HasMaxLength(200);
            e.HasIndex(x => x.BookingReference).IsUnique();
            e.HasIndex(x => new { x.BarberId, x.StartAtUtc });
            e.HasIndex(x => new { x.CustomerId, x.StartAtUtc });
            e.HasIndex(x => new { x.Status, x.StartAtUtc });
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Barber).WithMany().HasForeignKey(x => x.BarberId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Bookings_Range", "\"EndAtUtc\" > \"StartAtUtc\"");
                t.HasCheckConstraint("CK_Bookings_PendingHasExpiry", "\"Status\" <> 'Pending' OR \"HoldExpiresAtUtc\" IS NOT NULL");
            });
            // The exclusion constraint itself is created in the migration (EF has no fluent API for EXCLUDE).
        });

        model.Entity<ServiceRecord>(e =>
        {
            e.Property(x => x.ServiceName).HasMaxLength(200);
            e.Property(x => x.CustomerName).HasMaxLength(100);
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.HasIndex(x => x.PerformedAtUtc);
            e.HasIndex(x => new { x.BarberId, x.PerformedAtUtc });
            e.HasIndex(x => x.BookingId);
            e.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Barber).WithMany().HasForeignKey(x => x.BarberId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_ServiceRecords_Price", "\"Price\" >= 0"));
        });

        model.Entity<Conversation>(e =>
        {
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PlatformConversationId).HasMaxLength(64);
            e.Property(x => x.CurrentState).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.CurrentBookingIntent).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Platform, x.PlatformConversationId }).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.RequiresAttention).HasFilter("\"RequiresAttention\"");
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ConversationMessage>(e =>
        {
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.MessageType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PlatformMessageId).HasMaxLength(256);
            e.Property(x => x.MessageContent).HasMaxLength(4096);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.HasIndex(x => new { x.Platform, x.PlatformMessageId }).IsUnique().HasFilter("\"PlatformMessageId\" IS NOT NULL");
            e.HasIndex(x => new { x.ConversationId, x.ReceivedAtUtc });
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ShopSettings>(e =>
        {
            e.Property(x => x.ShopName).HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
        });

        model.Entity<WebhookEvent>(e =>
        {
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ExternalEventId).HasMaxLength(256);
            e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.HasIndex(x => new { x.Platform, x.PayloadHash }).IsUnique();
            e.HasIndex(x => new { x.ProcessingStatus, x.NextAttemptAtUtc });
        });

        model.Entity<NotificationLog>(e =>
        {
            e.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.NotificationType).HasMaxLength(40);
            e.Property(x => x.Recipient).HasMaxLength(64);
            e.Property(x => x.MessageContent).HasMaxLength(4096);
            e.Property(x => x.ProviderMessageId).HasMaxLength(256);
            e.Property(x => x.DeliveryStatus).HasMaxLength(20);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.HasIndex(x => x.ProviderMessageId);
            e.HasIndex(x => x.SentAtUtc);
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.DeduplicationKey).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.DeduplicationKey).IsUnique().HasFilter("\"DeduplicationKey\" IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        });
    }
}
