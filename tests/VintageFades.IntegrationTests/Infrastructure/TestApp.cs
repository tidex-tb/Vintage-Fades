using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Inbound;
using VintageFades.Application.Messaging;
using VintageFades.Domain;
using VintageFades.Infrastructure.Persistence;

namespace VintageFades.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<TestApp>
{
    public const string Name = "postgres";
}

/// <summary>
/// One real PostgreSQL container (Testcontainers) and one in-memory host for the whole run. Background workers are
/// disabled so tests drive webhook processing and outbox dispatch explicitly. Meta's HTTP API is replaced by
/// <see cref="FakeMeta"/>, which records every request.
/// </summary>
public class TestApp : IAsyncLifetime
{
    public const string AppSecret = "test-app-secret";
    public const string VerifyToken = "test-verify-token";
    public const string OwnerNumber = "264811110000";
    public const string AdminKey = "test-admin-key-0123456789abcdef0123456789";
    /// <summary>Monday 2 November 2026, 08:00 in Windhoek.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;

    public TestClock Time { get; } = new(Start);
    public FakeMeta Meta { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
            ["Workers:Enabled"] = "false",
            ["Meta:AppSecret"] = AppSecret,
            ["Meta:WebhookVerifyToken"] = VerifyToken,
            ["WhatsApp:PhoneNumberId"] = "PNID",
            ["WhatsApp:AccessToken"] = "wa-token",
            ["WhatsApp:OwnerNotificationNumber"] = "+" + OwnerNumber,
            ["Instagram:AccountId"] = "IGID",
            ["Instagram:AccessToken"] = "ig-token",
            ["Facebook:PageId"] = "PAGEID",
            ["Facebook:PageAccessToken"] = "fb-token",
            ["Admin:ApiKey"] = AdminKey
        };

        Customize(settings);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(Time);
                services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Meta));
            });
        });
        Client = _factory.CreateClient();
        await ResetAsync();
    }

    /// <summary>Lets a derived fixture change configuration (e.g. WhatsApp through YCloud).</summary>
    protected virtual void Customize(Dictionary<string, string?> settings) { }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>Clears all data, re-seeds configuration and resets the clock and fake Meta API.</summary>
    public async Task ResetAsync()
    {
        Time.SetUtcNow(Start);
        Meta.Reset();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE "NotificationLogs", "OutboxMessages", "WebhookEvents", "ConversationMessages", "Conversations",
                     "Bookings", "Customers", "BarberTimeOff", "BarberWorkingHours", "Barbers", "ShopSettings" RESTART IDENTITY CASCADE;
            ALTER SEQUENCE booking_reference_seq RESTART WITH 1;
            """);
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().SeedAsync(CancellationToken.None);
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task WithScopeAsync(Func<IServiceProvider, Task> work) => WithScopeAsync<int>(async sp => { await work(sp); return 0; });

    public Task<T> Db<T>(Func<AppDbContext, Task<T>> query) => WithScopeAsync(sp => query(sp.GetRequiredService<AppDbContext>()));

    public Task<BookingService> BookingServiceAsync(AsyncServiceScope scope) => Task.FromResult(scope.ServiceProvider.GetRequiredService<BookingService>());

    public async Task<long> CreateCustomerAsync(string name, string? whatsAppId = null)
    {
        return await Db(async db =>
        {
            var customer = new Customer { FullName = name, WhatsAppId = whatsAppId, CreatedAtUtc = Time.GetUtcNow().UtcDateTime, UpdatedAtUtc = Time.GetUtcNow().UtcDateTime };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            return customer.Id;
        });
    }

    // ------------------------------------------------------------------ messaging simulation

    private int _messageCounter;

    /// <summary>Delivers a customer message through the real pipeline: signed webhook POST → stored event →
    /// processor → chatbot → outbox → fake Meta API. Returns the texts sent back to that customer.</summary>
    public async Task<IReadOnlyList<string>> SendAsync(Platform platform, string senderId, string text, string? replyPayload = null)
    {
        var before = Meta.Requests.Count;
        var response = await PostWebhookAsync(platform, BuildPayload(platform, senderId, text, replyPayload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ProcessAsync();
        return Meta.Requests.Skip(before).Where(r => r.Recipient == senderId).Select(r => r.Text).ToList();
    }

    public async Task ProcessAsync()
    {
        await WithScopeAsync(sp => sp.GetRequiredService<WebhookProcessor>().ProcessDueAsync(CancellationToken.None));
        await DispatchAsync();
    }

    public Task<int> DispatchAsync() => WithScopeAsync(sp => sp.GetRequiredService<OutboxDispatcher>().DispatchDueAsync(CancellationToken.None));

    public async Task<HttpResponseMessage> PostWebhookAsync(Platform platform, string payload, string? signature = null)
    {
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{PathFor(platform)}") { Content = content };
        request.Headers.Add("X-Hub-Signature-256", signature ?? Sign(payload));
        return await Client.SendAsync(request);
    }

    public static string Sign(string payload) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), Encoding.UTF8.GetBytes(payload)));

    public static string PathFor(Platform platform) => platform switch
    {
        Platform.WhatsApp => "whatsapp",
        Platform.Instagram => "instagram",
        _ => "messenger"
    };

    public string BuildPayload(Platform platform, string senderId, string text, string? replyPayload = null)
    {
        var id = Interlocked.Increment(ref _messageCounter);
        var ts = Time.GetUtcNow();
        if (platform == Platform.WhatsApp)
        {
            var message = replyPayload is null
                ? new JsonObject { ["from"] = senderId, ["id"] = $"wamid.test{id}", ["timestamp"] = ts.ToUnixTimeSeconds().ToString(), ["type"] = "text", ["text"] = new JsonObject { ["body"] = text } }
                : new JsonObject
                {
                    ["from"] = senderId, ["id"] = $"wamid.test{id}", ["timestamp"] = ts.ToUnixTimeSeconds().ToString(), ["type"] = "interactive",
                    ["interactive"] = new JsonObject { ["type"] = "button_reply", ["button_reply"] = new JsonObject { ["id"] = replyPayload, ["title"] = text } }
                };
            return new JsonObject
            {
                ["object"] = "whatsapp_business_account",
                ["entry"] = new JsonArray(new JsonObject
                {
                    ["id"] = "WABA",
                    ["changes"] = new JsonArray(new JsonObject
                    {
                        ["field"] = "messages",
                        ["value"] = new JsonObject
                        {
                            ["messaging_product"] = "whatsapp",
                            ["metadata"] = new JsonObject { ["phone_number_id"] = "PNID" },
                            ["contacts"] = new JsonArray(new JsonObject { ["wa_id"] = senderId, ["profile"] = new JsonObject { ["name"] = "Test" } }),
                            ["messages"] = new JsonArray(message)
                        }
                    })
                })
            }.ToJsonString();
        }

        var msg = new JsonObject { ["mid"] = $"mid.test{id}", ["text"] = text };
        if (replyPayload is not null) msg["quick_reply"] = new JsonObject { ["payload"] = replyPayload };
        var account = platform == Platform.Instagram ? "IGID" : "PAGEID";
        return new JsonObject
        {
            ["object"] = platform == Platform.Instagram ? "instagram" : "page",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = account,
                ["time"] = ts.ToUnixTimeMilliseconds(),
                ["messaging"] = new JsonArray(new JsonObject
                {
                    ["sender"] = new JsonObject { ["id"] = senderId },
                    ["recipient"] = new JsonObject { ["id"] = account },
                    ["timestamp"] = ts.ToUnixTimeMilliseconds(),
                    ["message"] = msg
                })
            })
        }.ToJsonString();
    }
}

public sealed record MetaRequest(string Url, string? Authorization, string Body, string? Recipient, string Text, string Type, string? ApiKey = null);

/// <summary>Stands in for graph.facebook.com / graph.instagram.com. Records requests; can be told to fail.</summary>
public sealed class FakeMeta : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _scripted = new();
    private readonly List<MetaRequest> _requests = [];
    private int _counter;

    public IReadOnlyList<MetaRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public void Reset()
    {
        lock (_requests) _requests.Clear();
        _scripted.Clear();
    }

    /// <summary>The next <paramref name="times"/> requests fail with the given status and body.</summary>
    public void FailNext(int times, HttpStatusCode status, string body)
    {
        for (var i = 0; i < times; i++)
            _scripted.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    public IReadOnlyList<MetaRequest> To(string recipient) => Requests.Where(r => r.Recipient == recipient).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonNode.Parse(body);
        var recipient = (string?)json?["to"] ?? (string?)json?["recipient"]?["id"];
        var type = (string?)json?["type"] ?? "send_api";
        var text = (string?)json?["text"]?["body"]
                   ?? (string?)json?["interactive"]?["body"]?["text"]
                   ?? (string?)json?["message"]?["text"]
                   ?? (json?["template"] is { } t ? $"[template:{(string?)t["name"]}] " + string.Join(" ", t["components"]?[0]?["parameters"]?.AsArray().Select(p => (string?)p?["text"]) ?? []) : "");
        var apiKey = request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.First() : null;
        lock (_requests) _requests.Add(new MetaRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body, recipient, text, type, apiKey));

        if (_scripted.TryDequeue(out var scripted)) return scripted();

        var id = Interlocked.Increment(ref _counter);
        if (request.RequestUri!.AbsolutePath.EndsWith("/whatsapp/messages/sendDirectly"))
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["id"] = $"yc_{id}", ["to"] = recipient, ["status"] = "accepted" }.ToJsonString(), Encoding.UTF8, "application/json")
            };
        var response = request.RequestUri!.AbsolutePath.Contains("/PNID/")
            ? new JsonObject { ["messaging_product"] = "whatsapp", ["messages"] = new JsonArray(new JsonObject { ["id"] = $"wamid.out{id}" }) }
            : new JsonObject { ["recipient_id"] = recipient, ["message_id"] = $"m_out{id}" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

public abstract class IntegrationTest(TestApp app) : IAsyncLifetime
{
    protected TestApp App { get; } = app;
    protected DateTime Now => App.Time.GetUtcNow().UtcDateTime;

    public Task InitializeAsync() => App.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    protected static string Joined(IEnumerable<string> texts) => string.Join("\n---\n", texts);
}


/// <summary>Settable clock (FakeTimeProvider cannot move backwards, which a shared fixture needs between tests).</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void SetUtcNow(DateTimeOffset value) => _now = value;
    public void Advance(TimeSpan by) => _now += by;
}