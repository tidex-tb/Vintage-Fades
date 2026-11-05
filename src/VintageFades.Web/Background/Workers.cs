using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Inbound;
using VintageFades.Application.Messaging;
using VintageFades.Application.Operations;

namespace VintageFades.Web.Background;

/// <summary>Records when each worker last completed a loop, for the readiness health check.</summary>
public sealed class WorkerHeartbeat(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _beats = new();
    public void Beat(string worker) => _beats[worker] = time.GetUtcNow();
    public IReadOnlyDictionary<string, DateTimeOffset> Snapshot() => _beats;
}

public sealed class WorkerHealthCheck(WorkerHeartbeat heartbeat, TimeProvider time) : IHealthCheck
{
    private static readonly string[] Workers = [nameof(WebhookWorker), nameof(OutboxWorker), nameof(SchedulerWorker)];

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var beats = heartbeat.Snapshot();
        var now = time.GetUtcNow();
        var stale = Workers.Where(w => !beats.TryGetValue(w, out var at) || now - at > TimeSpan.FromMinutes(5)).ToList();
        return Task.FromResult(stale.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Workers not reporting: {string.Join(", ", stale)}"));
    }
}

/// <summary>Runs a unit of work in a fresh DI scope, waking early when signalled. Failures are logged and retried
/// with a growing delay (e.g. while the database is unreachable); they never stop the worker.</summary>
public abstract class QueueWorker(IServiceScopeFactory scopes, IWorkSignal signal, WorkerHeartbeat heartbeat, ILogger logger) : BackgroundService
{
    protected IWorkSignal Signal => signal;
    protected abstract WorkQueue? Queue { get; }
    protected abstract TimeSpan Interval { get; }
    protected abstract Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                processed = await RunOnceAsync(scope.ServiceProvider, stoppingToken);
                failures = 0;
                heartbeat.Beat(GetType().Name);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, failures - 1)));
                logger.LogError(ex, "{Worker} failed (attempt {Failures}); retrying in {Delay}", GetType().Name, failures, delay);
                await Delay(delay, stoppingToken);
                continue;
            }

            if (processed > 0 && Queue is not null) continue; // drain the queue before waiting
            try
            {
                if (Queue is WorkQueue queue) await signal.WaitAsync(queue, Interval, stoppingToken);
                else await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}

public sealed class WebhookWorker(IServiceScopeFactory scopes, IWorkSignal signal, WorkerHeartbeat heartbeat, ILogger<WebhookWorker> logger)
    : QueueWorker(scopes, signal, heartbeat, logger)
{
    protected override WorkQueue? Queue => WorkQueue.Webhooks;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);
    protected override Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<WebhookProcessor>().ProcessDueAsync(ct);
}

public sealed class OutboxWorker(IServiceScopeFactory scopes, IWorkSignal signal, WorkerHeartbeat heartbeat, ILogger<OutboxWorker> logger)
    : QueueWorker(scopes, signal, heartbeat, logger)
{
    protected override WorkQueue? Queue => WorkQueue.Outbox;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);
    protected override Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<OutboxDispatcher>().DispatchDueAsync(ct);
}

/// <summary>Every minute: release expired holds, queue reminders, check on idle customers, check for stalled processing.
/// Daily: retention purge.</summary>
public sealed class SchedulerWorker(IServiceScopeFactory scopes, IWorkSignal signal, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<SchedulerWorker> logger)
    : QueueWorker(scopes, signal, heartbeat, logger)
{
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    protected override WorkQueue? Queue => null;
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override async Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct)
    {
        await services.GetRequiredService<BookingService>().ReleaseExpiredHoldsAsync(ct);
        var reminders = await services.GetRequiredService<ReminderScheduler>().EnqueueDueRemindersAsync(ct);
        reminders += await services.GetRequiredService<IdleConversationService>().ProcessAsync(ct);
        await services.GetRequiredService<OperationalMonitor>().CheckAsync(ct);
        if (time.GetUtcNow() - _lastPurge > TimeSpan.FromDays(1))
        {
            await services.GetRequiredService<DataRetentionService>().PurgeAsync(ct);
            _lastPurge = time.GetUtcNow();
        }
        Signal.Notify(WorkQueue.Outbox);
        return reminders;
    }
}
