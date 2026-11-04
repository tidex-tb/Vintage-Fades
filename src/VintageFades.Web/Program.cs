using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using VintageFades.Application;
using VintageFades.Infrastructure;
using VintageFades.Infrastructure.Persistence;
using VintageFades.Integrations.Meta;
using VintageFades.Web.Background;
using VintageFades.Web.Security;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var connectionString = DatabaseUrl.Resolve(configuration.GetConnectionString("DefaultConnection"), configuration["DATABASE_URL"]);
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set ConnectionStrings__DefaultConnection or DATABASE_URL. See README.md (Local setup).");

// Hosting platforms (Railway, Render, Fly.io) tell the app which port to listen on through PORT.
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port))
    builder.WebHost.UseUrls($"http://+:{port}");

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = WebhooksControllerLimits.MaxBodyBytes);

builder.Services
    .AddApplication(configuration)
    .AddInfrastructure(connectionString)
    .AddMetaIntegrations(configuration);

builder.Services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, AdminKeyAuthenticationHandler>(AdminKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Meta delivers from many addresses; one generous shared bucket protects the database from floods.
    options.AddPolicy("webhooks", _ => RateLimitPartition.GetFixedWindowLimiter("meta",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 100 }));
    options.AddPolicy("admin", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

if (configuration.GetValue("Hosting:UseForwardedHeaders", false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // The app must only be reachable through the reverse proxy when this is enabled (see docs/DEPLOYMENT.md).
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

builder.Services.AddSingleton<WorkerHeartbeat>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"])
    .AddCheck<WorkerHealthCheck>("workers", tags: ["ready"]);

if (configuration.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService<WebhookWorker>();
    builder.Services.AddHostedService<OutboxWorker>();
    builder.Services.AddHostedService<SchedulerWorker>();
}

var app = builder.Build();

if (configuration.GetValue("Hosting:UseForwardedHeaders", false)) app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    if (configuration.GetValue("Hosting:UseHttpsRedirection", false)) app.UseHttpsRedirection();
}
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "VINTAGE FADES API"));
}

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString())
        }));
    }
});
app.MapGet("/", () => Results.Text("VINTAGE FADES booking service"));

await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(configuration.GetValue("Database:ApplyMigrationsOnStartup", true), CancellationToken.None);

    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var meta = scope.ServiceProvider.GetRequiredService<IOptions<MetaOptions>>().Value;
    if (string.IsNullOrEmpty(meta.AppSecret) || string.IsNullOrEmpty(meta.WebhookVerifyToken))
        log.LogWarning("Meta__AppSecret or Meta__WebhookVerifyToken is missing: all webhooks will be rejected");
    foreach (var channel in scope.ServiceProvider.GetServices<VintageFades.Application.Abstractions.IMessagingChannel>())
        log.LogInformation("{Platform} integration {State}", channel.Platform, channel.IsConfigured ? "enabled" : "not configured");
    if (scope.ServiceProvider.GetRequiredService<IOptions<WhatsAppOptions>>().Value.Provider == WhatsAppProvider.YCloud &&
        string.IsNullOrEmpty(scope.ServiceProvider.GetRequiredService<IOptions<YCloudOptions>>().Value.WebhookSecret))
        log.LogWarning("YCloud__WebhookSecret is missing: all YCloud webhooks will be rejected");    if (scope.ServiceProvider.GetRequiredService<IOptions<NotificationOptions>>().Value.OwnerRecipient is null)
        log.LogWarning("WhatsApp__OwnerNotificationNumber is not set: owner notifications will be recorded as failed");
}

app.Run();

internal static class WebhooksControllerLimits
{
    public const long MaxBodyBytes = VintageFades.Web.Controllers.WebhooksController.MaxPayloadBytes;
}

public partial class Program;
