using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using VintageFades.Application.Abstractions;
using VintageFades.Infrastructure.Persistence;

namespace VintageFades.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        // Non-retrying strategy: the booking flow uses explicit transactions with savepoints, which retrying
        // strategies do not support. Transient failures surface to the caller and the webhook/outbox retry loops
        // replay the whole unit of work instead.
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql
            .CommandTimeout(30)
            .ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies))));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<DatabaseInitializer>();
        services.AddSingleton<IWorkSignal, WorkSignal>();
        return services;
    }
}

public sealed class WorkSignal : IWorkSignal
{
    private readonly Dictionary<WorkQueue, SemaphoreSlim> _signals =
        Enum.GetValues<WorkQueue>().ToDictionary(q => q, _ => new SemaphoreSlim(0, 1));

    public void Notify(WorkQueue queue)
    {
        var signal = _signals[queue];
        lock (signal)
        {
            if (signal.CurrentCount == 0) signal.Release();
        }
    }

    public async Task WaitAsync(WorkQueue queue, TimeSpan timeout, CancellationToken ct) =>
        await _signals[queue].WaitAsync(timeout, ct);
}

/// <summary>Used only by `dotnet ef` tooling. Reads the connection string from VINTAGEFADES_DESIGN_CONNECTION
/// or falls back to a local placeholder; no secrets are stored here.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VINTAGEFADES_DESIGN_CONNECTION")
                         ?? "Host=localhost;Database=vintagefades_design;Username=postgres";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
    }
}
