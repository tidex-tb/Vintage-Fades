using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VintageFades.Infrastructure.Persistence;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class DatabaseTests(TestApp app) : IntegrationTest(app)
{
    [Fact]
    public async Task Migrations_are_applied_and_match_the_model()
    {
        await App.Db(async db =>
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges(), "Model changed without a migration: run dotnet ef migrations add.");
            return 0;
        });
    }

    [Fact]
    public async Task Exclusion_constraint_and_extension_exist()
    {
        var constraints = await App.Db(db => db.Database
            .SqlQueryRaw<string>("SELECT conname AS \"Value\" FROM pg_constraint WHERE conname = 'EX_Bookings_Barber_NoOverlap'").ToListAsync());
        Assert.Single(constraints);
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_does_not_overwrite_owner_changes()
    {
        await App.Db(async db =>
        {
            var barber = await db.Barbers.SingleAsync(b => b.StationNumber == 1);
            barber.DisplayName = "Tjeripo";
            return await db.SaveChangesAsync();
        });
        await App.WithScopeAsync(sp => sp.GetRequiredService<DatabaseInitializer>().SeedAsync(CancellationToken.None));
        await App.WithScopeAsync(sp => sp.GetRequiredService<DatabaseInitializer>().SeedAsync(CancellationToken.None));

        Assert.Equal(4, await App.Db(db => db.Barbers.CountAsync()));
        Assert.Equal(28, await App.Db(db => db.BarberWorkingHours.CountAsync()));
        Assert.Equal(1, await App.Db(db => db.ShopSettings.CountAsync()));
        Assert.Equal("Tjeripo", await App.Db(db => db.Barbers.Where(b => b.StationNumber == 1).Select(b => b.DisplayName).SingleAsync()));
        var settings = await App.Db(db => db.ShopSettings.SingleAsync());
        Assert.Equal("Africa/Windhoek", settings.TimeZoneId);
        Assert.Equal(60, settings.AppointmentDurationMinutes);
    }
}
