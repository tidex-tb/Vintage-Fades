# Security

## Secrets

| Secret | Where it lives |
|---|---|
| Database password | `deploy/.env` (`POSTGRES_PASSWORD`) on the server, file mode `600`; the container env only |
| Meta app secret, Instagram app secret | Environment variables (`Meta__AppSecret`, `Instagram__AppSecret`) |
| Webhook verify token | `Meta__WebhookVerifyToken` |
| WhatsApp / Page / Instagram access tokens | `WhatsApp__AccessToken`, `Facebook__PageAccessToken`, `Instagram__AccessToken` |
| Admin API key | `Admin__ApiKey` (at least 32 characters; the admin API is disabled when empty or shorter) |

Rules:

- **Nothing secret is committed.** `appsettings.json` contains only empty values; `appsettings.Example.json` and
  `.env.example` contain placeholders only. `.gitignore` excludes `.env`, `appsettings.Local.json`,
  `appsettings.Production.json` and backups.
- **Local development:** use `dotnet user-secrets` (stored in your user profile, outside the repository).
- **Production:** environment variables from `deploy/.env` or the host's secret store (e.g. Azure Key Vault
  references, Render/Railway secret variables).
- **Rotation:** rotate the app secret, tokens and admin key if they leak or when a person with access leaves.
  Generating a new system-user token invalidates nothing else; update the variable and restart.

## API tokens

- Tokens are sent only in the `Authorization: Bearer` header, never in URLs, so they cannot appear in proxy or
  HttpClient request logs.
- Tokens, secrets and the verify token are never logged. Meta error messages are logged (they do not contain tokens).
- Prefer **system-user tokens** with only the permissions listed in [META_SETUP.md](META_SETUP.md).
- An invalid or expired token (Meta error 190) raises a SYSTEM ALERT for the owner.

## Webhook signatures

- Every POST to `/webhooks/*` must carry `X-Hub-Signature-256: sha256=<hex>`, the HMAC-SHA256 of the **raw request
  body** keyed with the app secret (Instagram Login apps: the Instagram app secret).
- The comparison is constant-time (`CryptographicOperations.FixedTimeEquals`). A missing, malformed or wrong
  signature returns **401**, and the payload is neither stored nor processed. A missing app secret rejects
  everything (fail closed).
- The GET verification handshake compares the verify token in constant time.
- Webhook bodies are limited to 1 MB. The webhook endpoints are rate limited (600 requests/min shared, queue 100).
- Events for other WhatsApp numbers, Pages or Instagram accounts on the same app are ignored.
- Duplicate deliveries are detected (payload hash and message id), so a replayed valid payload cannot book twice.

## Customer information

Stored:

| Data | Why | Retention |
|---|---|---|
| Name (as given), WhatsApp number / platform-scoped ids | Identify the customer and their bookings | Kept with bookings (business record) |
| Bookings | The service itself | Kept |
| Conversation messages (text only) | Continue conversations, troubleshooting | **90 days** (`Retention__ConversationMessageDays`) |
| Raw webhook payloads | Reliable processing | **Deleted as soon as processed**; failed ones kept for diagnosis and purged after 30 days |
| Notification log | Delivery evidence | 180 days |

Not stored: media files, profile pictures, locations, payment data. Images and voice notes are not downloaded.

- Customers are never merged across platforms based on names (see [ARCHITECTURE.md](ARCHITECTURE.md#customer-identity-across-channels)).
- A customer can only see, cancel or move **their own** bookings (checked by customer id in `BookingService`).
- Customer-facing replies never include internal errors. Unexpected exceptions are logged and the ASP.NET Core
  exception handler returns a generic problem response without details in Production.
- Data access uses EF Core with parameterised queries. The only raw SQL is fixed statements (sequence value,
  migration DDL). No user input is concatenated into SQL.
- **Data-deletion requests:** delete the customer's conversations and messages and anonymise the customer row
  (`FullName`, `PhoneNumber`, platform ids set to NULL). Keep bookings for the shop's records if required by law. Meta
  requires a published data-deletion procedure for the app.
- Namibia's data-protection framework and Meta's platform terms both expect only necessary data, kept no longer
  than needed. The retention settings above implement that. Review them with the owner.

## Backups

- Nightly `pg_dump` with a readability check, 14-day local retention, **copied off-server** ([DEPLOYMENT.md](DEPLOYMENT.md#backups)).
- Backups contain customer data. Store them in a private bucket with encryption at rest, and restrict who can download them.

## Access control

| Surface | Protection |
|---|---|
| `/webhooks/*` | Meta signature (POST), verify token (GET), rate limit, size limit |
| `/api/admin/*` | `X-Admin-Key` header (constant-time compare), 60 requests/min per IP, disabled when no key is configured |
| `/health/live`, `/health/ready` | Public, but they only reveal Healthy/Degraded/Unhealthy per check |
| Swagger / OpenAPI | Development only, and also blocked at the proxy |
| PostgreSQL | Not exposed to the internet (Compose internal network); dedicated database user |
| Server | SSH keys only, firewall allows 22/80/443, automatic security updates |

HTTPS: Caddy terminates TLS with automatic certificates. HSTS is enabled. Forwarded headers are trusted only when
`Hosting__UseForwardedHeaders=true`, which must only be set when the app is reachable exclusively through the proxy.

## Reporting a problem

If you suspect a leaked token or secret: revoke it in Meta Business Settings (system users → revoke tokens) or
reset the app secret (App settings → Basic), update the environment, restart, and review `NotificationLogs`
and the logs for unexpected activity.
