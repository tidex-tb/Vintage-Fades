using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using VintageFades.Domain;

namespace VintageFades.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Barber> Barbers { get; }
    DbSet<BarberWorkingHours> BarberWorkingHours { get; }
    DbSet<BarberTimeOff> BarberTimeOff { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ConversationMessage> ConversationMessages { get; }
    DbSet<ShopSettings> ShopSettings { get; }
    DbSet<WebhookEvent> WebhookEvents { get; }
    DbSet<NotificationLog> NotificationLogs { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Next value of the database sequence used for human-readable booking references.</summary>
    Task<long> NextBookingNumberAsync(CancellationToken cancellationToken);

    /// <summary>True when a booking write lost a race: the exclusion constraint forbidding overlapping active bookings
    /// per barber fired, or PostgreSQL aborted one of two concurrent conflicting inserts as a deadlock.</summary>
    bool IsBookingConflict(DbUpdateException exception);

    bool IsUniqueViolation(DbUpdateException exception);
}

public static class AppDbContextExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction, joining the ambient one when present.
    /// Inside a transaction EF Core wraps each SaveChanges in a savepoint and rolls back to it on failure,
    /// so a lost booking race does not poison the surrounding unit of work.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(this IAppDbContext db, Func<Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            return await work();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        await transaction.CommitAsync(ct);
        return result;
    }

    public static void Discard(this IAppDbContext db, object entity) => db.ChangeTracker.Entries()
        .Where(e => ReferenceEquals(e.Entity, entity)).ToList().ForEach(e => e.State = EntityState.Detached);
}
