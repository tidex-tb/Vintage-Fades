namespace VintageFades.Integrations.Meta;

public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    public string? AppId { get; set; }
    /// <summary>App secret used to verify X-Hub-Signature-256 on WhatsApp and Messenger webhooks.</summary>
    public string? AppSecret { get; set; }
    public string? WebhookVerifyToken { get; set; }
    public string ApiVersion { get; set; } = "v26.0";
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";
}

public enum WhatsAppProvider
{
    /// <summary>Direct Meta WhatsApp Cloud API (graph.facebook.com).</summary>
    Meta,
    /// <summary>Cloud API through the Meta Solution Partner YCloud (needed for Coexistence on an existing Business app number).</summary>
    YCloud
}

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    public WhatsAppProvider Provider { get; set; } = WhatsAppProvider.Meta;
    public string? PhoneNumberId { get; set; }
    public string? BusinessAccountId { get; set; }
    public string? AccessToken { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PhoneNumberId) && !string.IsNullOrWhiteSpace(AccessToken);
}

public sealed class YCloudOptions
{
    public const string SectionName = "YCloud";

    /// <summary>YCloud API key (Console → Developers → API keys). Sent as the X-API-Key header.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Signing secret returned when the webhook endpoint is created. Verifies YCloud-Signature.</summary>
    public string? WebhookSecret { get; set; }
    /// <summary>The shop's WhatsApp business number connected in YCloud, in E.164 format (+264...).</summary>
    public string? FromNumber { get; set; }
    public string BaseUrl { get; set; } = "https://api.ycloud.com/v2";
    /// <summary>Maximum age of a signed webhook (replay protection), as recommended by YCloud.</summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromNumber);
}

public sealed class InstagramOptions
{
    public const string SectionName = "Instagram";

    /// <summary>Optional Instagram professional account id (IG_ID). When set, webhook entries for other accounts are
    /// ignored; sending does not need it because replies go to /me/messages.</summary>
    public string? AccountId { get; set; }
    public string? AccessToken { get; set; }
    /// <summary>Instagram app secret. Apps using "Instagram API with Instagram Login" sign webhooks with it;
    /// when empty, Meta:AppSecret is used.</summary>
    public string? AppSecret { get; set; }
    /// <summary>graph.facebook.com for Facebook Login (Page) tokens; graph.instagram.com for Instagram Login tokens.</summary>
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken);
}

public sealed class FacebookOptions
{
    public const string SectionName = "Facebook";

    public string? PageId { get; set; }
    public string? PageAccessToken { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PageId) && !string.IsNullOrWhiteSpace(PageAccessToken);
}
