using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Inbound;
using VintageFades.Domain;
using VintageFades.Integrations.Meta;

namespace VintageFades.Web.Controllers;

/// <summary>
/// Webhook endpoints, one callback URL per source: /webhooks/whatsapp, /webhooks/messenger and /webhooks/instagram
/// (signed by Meta), or /webhooks/ycloud when WhatsApp runs through YCloud (signed by YCloud).
/// </summary>
[ApiController]
[Route("webhooks/{source}")]
[EnableRateLimiting("webhooks")]
public sealed class WebhooksController(
    IOptions<MetaOptions> meta,
    IOptions<InstagramOptions> instagram,
    IOptions<WhatsAppOptions> whatsApp,
    IOptions<YCloudOptions> ycloud,
    IEnumerable<IMessagingChannel> channels,
    WebhookIngestionService ingestion,
    TimeProvider time,
    ILogger<WebhooksController> logger) : ControllerBase
{
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>Verification handshake performed by Meta when the callback URL is saved.</summary>
    [HttpGet]
    public IActionResult Verify(
        string source,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (!TryResolve(source, out _, out var viaYCloud) || viaYCloud) return NotFound();
        if (mode == "subscribe" && challenge is not null && MetaSignatureValidator.TokensMatch(token, meta.Value.WebhookVerifyToken))
            return Content(challenge, "text/plain");
        logger.LogWarning("Webhook verification rejected for {Source}", source);
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>Receives events. Only correctly signed requests are stored; processing happens in the background.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxPayloadBytes)]
    public async Task<IActionResult> Receive(string source, CancellationToken ct)
    {
        if (!TryResolve(source, out var platform, out var viaYCloud)) return NotFound();
        if (channels.FirstOrDefault(c => c.Platform == platform) is not { IsConfigured: true })
        {
            logger.LogWarning("Webhook received on {Source}, which is not configured", source);
            return NotFound();
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        var valid = viaYCloud
            ? YCloudSignatureValidator.IsValid(body, Request.Headers["YCloud-Signature"], ycloud.Value.WebhookSecret,
                time.GetUtcNow(), TimeSpan.FromSeconds(ycloud.Value.SignatureToleranceSeconds))
            : MetaSignatureValidator.IsValid(body, Request.Headers["X-Hub-Signature-256"],
                platform == Platform.Instagram && !string.IsNullOrEmpty(instagram.Value.AppSecret) ? instagram.Value.AppSecret : meta.Value.AppSecret);
        if (!valid)
        {
            logger.LogWarning("Rejected {Source} webhook with missing or invalid signature from {RemoteIp}", source, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        var stored = await ingestion.StoreAsync(platform, System.Text.Encoding.UTF8.GetString(body), ct);
        if (!stored) logger.LogInformation("Duplicate {Source} webhook delivery ignored", source);
        return Ok("EVENT_RECEIVED");
    }

    /// <summary>WhatsApp is accepted on exactly one route, matching WhatsApp:Provider.</summary>
    private bool TryResolve(string source, out Platform platform, out bool viaYCloud)
    {
        var usesYCloud = whatsApp.Value.Provider == WhatsAppProvider.YCloud;
        viaYCloud = false;
        platform = source.ToLowerInvariant() switch
        {
            "whatsapp" when !usesYCloud => Platform.WhatsApp,
            "ycloud" when usesYCloud => Platform.WhatsApp,
            "instagram" => Platform.Instagram,
            "messenger" or "facebook" => Platform.Messenger,
            _ => (Platform)(-1)
        };
        viaYCloud = usesYCloud && platform == Platform.WhatsApp;
        return Enum.IsDefined(platform);
    }
}
