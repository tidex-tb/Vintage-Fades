using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VintageFades.Domain;

namespace VintageFades.Infrastructure.Persistence;

/// <summary>Applies migrations (optional) and seeds initial configuration. Every seed step is idempotent:
/// it only inserts what is missing and never overwrites values the owner has changed.</summary>
public sealed class DatabaseInitializer(AppDbContext db, TimeProvider time, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(bool applyMigrations, CancellationToken ct)
    {
        if (applyMigrations)
        {
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("Database migrations applied");
        }
        await SeedAsync(ct);
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        if (!await db.ShopSettings.AnyAsync(ct))
        {
            db.ShopSettings.Add(new ShopSettings
            {
                ShopName = "VINTAGE FADES BARBER SHOP",
                TimeZoneId = "Africa/Windhoek",
                OpeningTime = new TimeOnly(9, 0),
                ClosingTime = new TimeOnly(19, 0),
                AppointmentDurationMinutes = 60,
                BookingAdvanceDays = 30,
                CancellationNoticeHours = 2,
                ReminderMinutesBefore = [1440, 120],
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            logger.LogInformation("Seeded shop settings");
        }

        if (!await db.Barbers.AnyAsync(ct))
        {
            string[] names = ["Kapza", "Luther", "Godwin", "Tevohn"];
            for (var i = 0; i < names.Length; i++)
            {
                db.Barbers.Add(new Barber
                {
                    FirstName = names[i],
                    DisplayName = names[i],
                    StationNumber = i + 1,
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            logger.LogInformation("Seeded {Count} barbers", names.Length);
        }
        await db.SaveChangesAsync(ct);

        var settings = await db.ShopSettings.OrderBy(s => s.Id).FirstAsync(ct);
        var barbers = await db.Barbers.Include(b => b.WorkingHours).ToListAsync(ct);
        foreach (var barber in barbers)
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                if (barber.WorkingHours.Any(h => h.DayOfWeek == day)) continue;
                barber.WorkingHours.Add(new BarberWorkingHours
                {
                    DayOfWeek = day,
                    OpeningTime = settings.OpeningTime,
                    ClosingTime = settings.ClosingTime,
                    IsWorking = true
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
