using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using VintageFades.Integrations.Meta;

namespace VintageFades.Web.Security;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    /// <summary>Shared secret for the admin API. At least 32 characters; when unset the admin API is disabled.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Authenticates the owner/operator API with a long random key in the X-Admin-Key header (constant-time comparison).</summary>
public sealed class AdminKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AdminOptions> admin) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AdminKey";
    public const string HeaderName = "X-Admin-Key";
    public const int MinimumKeyLength = 32;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = admin.Value.ApiKey;
        if (string.IsNullOrEmpty(expected) || expected.Length < MinimumKeyLength)
            return Task.FromResult(AuthenticateResult.Fail("Admin API is disabled (Admin__ApiKey not configured)."));
        if (!Request.Headers.TryGetValue(HeaderName, out var provided))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!MetaSignatureValidator.TokensMatch(provided.ToString(), expected))
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin key."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
