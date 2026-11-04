using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VintageFades.Application.Abstractions;

namespace VintageFades.Integrations.Meta;

public static class MetaSignatureValidator
{
    /// <summary>Validates Meta's X-Hub-Signature-256 header ("sha256=" + hex HMAC-SHA256 of the raw body keyed with the app secret).</summary>
    public static bool IsValid(ReadOnlySpan<byte> body, string? signatureHeader, string? appSecret)
    {
        const string prefix = "sha256=";
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(signatureHeader) ||
            !signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHeader.AsSpan(prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public static bool TokensMatch(string? provided, string? expected) =>
        !string.IsNullOrEmpty(provided) && !string.IsNullOrEmpty(expected) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}

/// <summary>Maps Graph API errors to retry decisions. Codes from Meta's Graph API, Messenger and WhatsApp Cloud API error references.</summary>
public static class MetaErrorClassifier
{
    private static readonly HashSet<int> RateLimitCodes = [4, 17, 32, 613, 80007, 130429, 131048, 131056];
    private static readonly HashSet<int> TransientCodes = [1, 2, 131000, 131016, 133004];
    private static readonly HashSet<int> OutsideWindowSubcodes = [2018278, 2534022];

    public static (SendFailureKind Kind, string Message) Classify(HttpStatusCode status, string body)
    {
        int? code = null, subcode = null;
        string? message = null;
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            code = (int?)error?["code"];
            subcode = (int?)error?["error_subcode"];
            message = (string?)error?["message"];
        }
        catch (JsonException)
        {
            // Non-JSON body (e.g. proxy error page): fall back to HTTP status.
        }

        return Classify(status, code, subcode, message);
    }

    public static (SendFailureKind Kind, string Message) Classify(HttpStatusCode status, int? code, int? subcode, string? message)
    {
        var description = $"HTTP {(int)status}" + (code is null ? "" : $", Meta error {code}" + (subcode is null ? "" : $"/{subcode}")) +
                          (message is null ? "" : $": {message}");

        SendFailureKind kind;
        if (code == 131047 || (subcode is int s && OutsideWindowSubcodes.Contains(s))) kind = SendFailureKind.OutsideMessagingWindow;
        else if (code is int c && RateLimitCodes.Contains(c)) kind = SendFailureKind.Transient;
        else if (code is int t && TransientCodes.Contains(t)) kind = SendFailureKind.Transient;
        else if (code is 190 or 102 or 10 || code is >= 200 and <= 299 || status == HttpStatusCode.Unauthorized) kind = SendFailureKind.AuthenticationFailed;
        else if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout) kind = SendFailureKind.Transient;
        else kind = SendFailureKind.Permanent;
        return (kind, description);
    }
}

/// <summary>Shared POST-and-classify logic for Graph API send endpoints. Tokens travel only in the Authorization header.</summary>
public abstract class MetaGraphClient(HttpClient http, ILogger logger)
{
    protected async Task<SendResult> PostAsync(string url, string accessToken, JsonObject payload, Func<JsonNode?, string?> readMessageId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                string? id = null;
                try { id = readMessageId(JsonNode.Parse(body)); } catch (JsonException) { }
                return SendResult.Sent(id);
            }
            var (kind, message) = MetaErrorClassifier.Classify(response.StatusCode, body);
            logger.LogWarning("Meta send failed ({Kind}): {Error}", kind, message);
            return SendResult.Failed(kind, message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Meta send network failure");
            return SendResult.Failed(SendFailureKind.Transient, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Meta send timed out");
            return SendResult.Failed(SendFailureKind.Transient, "Request to Meta timed out.");
        }
    }

    protected static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>Splits long text on line breaks so each part stays within a platform limit.</summary>
    protected static List<string> Split(string text, Func<string, bool> fits)
    {
        if (fits(text)) return [text];
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var candidate = current.Length == 0 ? line : current + "\n" + line;
            if (fits(candidate))
            {
                current.Clear().Append(candidate);
                continue;
            }
            if (current.Length > 0) parts.Add(current.ToString());
            var remaining = line;
            while (!fits(remaining))
            {
                var cut = remaining.Length - 1;
                while (cut > 1 && !fits(remaining[..cut])) cut--;
                parts.Add(remaining[..cut]);
                remaining = remaining[cut..];
            }
            current.Clear().Append(remaining);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
}
