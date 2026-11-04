# Deployment

## Hosting requirements

The service must run **all the time**. It receives webhooks at any hour, and its background workers send replies
and reminders. Do **not** use a plan that sleeps when idle (many free tiers do). If the app is asleep, Meta's
webhook deliveries fail and Meta eventually disables the webhook.

Recommended low-cost setup: **one small Linux VPS** running Docker Compose with three containers:

| Container | Role |
|---|---|
| `caddy` | Public HTTPS on 443 (automatic Let's Encrypt certificates), HTTP→HTTPS redirect, security headers |
| `app` | This service (ASP.NET Core, port 8080, internal only) |
| `db` | PostgreSQL 18 (not published to the internet) |

A 2 vCPU / 2–4 GB RAM VPS is plenty (for example Hetzner, DigitalOcean, Vultr or Linode). A managed alternative
is any always-on container host (Azure App Service B1, Render or Railway paid plans, Fly.io) together with a
managed PostgreSQL that allows the `btree_gist` extension (Azure Database for PostgreSQL Flexible Server, Neon,
Supabase, Amazon RDS and DigitalOcean Managed PostgreSQL all do).

## Option: Railway (no server, no domain)

Railway builds the `Dockerfile` from the GitHub repository and runs it 24/7 with a managed PostgreSQL.
A paid plan is required for an always-on service.

1. Railway project → **+ Create → GitHub Repo** → `Vintage-Barber-Shop`.
2. **+ Create → Database → PostgreSQL** (keep the name `Postgres`).
3. Repo service → **Variables → Raw Editor**:
   ```
   PORT=8080
   ASPNETCORE_ENVIRONMENT=Production
   Hosting__UseForwardedHeaders=true
   DATABASE_URL=${{Postgres.DATABASE_URL}}
   WhatsApp__Provider=YCloud
   YCloud__ApiKey=
   YCloud__WebhookSecret=
   YCloud__FromNumber=+264XXXXXXXXX
   WhatsApp__OwnerNotificationNumber=+264XXXXXXXXX
   Admin__ApiKey=<64 random hex characters>
   ```
   The app converts `DATABASE_URL` to an Npgsql connection string and listens on `PORT`.
4. Repo service → **Settings → Networking → Generate Domain** (port 8080). Use
   `https://<name>.up.railway.app/webhooks/ycloud` as the YCloud webhook URL.
5. Check `https://<name>.up.railway.app/health/ready` returns `Healthy`.
6. Every push to `main` redeploys automatically.

The double-booking protection needs the `btree_gist` extension. The first start runs
`CREATE EXTENSION IF NOT EXISTS btree_gist`. If the deploy log shows a permission error for it, run that statement once
in Railway's database query tab. Backups: enable Railway's database backups if your plan offers them, or schedule
`pg_dump` from another machine with `DATABASE_PUBLIC_URL`.

The same variables work on Render, Fly.io or Azure App Service (container). Only the console steps differ.

## Environment variables

Set these in `deploy/.env` (copy from `.env.example`) or in the host's secret settings. `__` separates
configuration levels.

| Variable | Required | Description |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | **yes** (or `DATABASE_URL`) | Npgsql connection string. Compose builds it from `POSTGRES_PASSWORD`. Add `SSL Mode=Require` for managed databases. |
| `DATABASE_URL` | alternative | `postgres://user:pass@host:port/db` URL as given by Railway/Render. Used only when `ConnectionStrings__DefaultConnection` is empty. |
| `PORT` | on PaaS | Port to listen on (set by the platform; overrides `ASPNETCORE_URLS`) |
| `ASPNETCORE_ENVIRONMENT` | yes | `Production` (Swagger off, HSTS on, JSON logs) |
| `ASPNETCORE_URLS` | yes | `http://+:8080` behind the proxy |
| `Hosting__UseForwardedHeaders` | behind a proxy | `true`, so client IP and scheme come from the proxy. Only enable this when the app is reachable **only** through the proxy. |
| `Hosting__UseHttpsRedirection` | optional | `true` only if the app itself terminates TLS |
| `Database__ApplyMigrationsOnStartup` | optional | Default `true`. Set `false` to run migrations as a separate step. |
| `Meta__AppId`, `Meta__AppSecret`, `Meta__WebhookVerifyToken` | **yes** | Without `AppSecret` and `WebhookVerifyToken` every webhook is rejected |
| `Meta__ApiVersion` | optional | Default `v26.0` |
| `WhatsApp__Provider` | yes | `YCloud` (existing number via Coexistence, the chosen setup) or `Meta` (direct Cloud API) |
| `YCloud__ApiKey`, `YCloud__WebhookSecret`, `YCloud__FromNumber` | when Provider=YCloud | API key, webhook signing secret, shop number (`+264…`). Webhook URL: `https://<host>/webhooks/ycloud` |
| `Chatbot__ManualReplyPauseMinutes` | optional | Default `10`. How long the bot stays quiet after the shop replies from the Business app |
| `WhatsApp__PhoneNumberId`, `WhatsApp__BusinessAccountId`, `WhatsApp__AccessToken` | when Provider=Meta | Empty = WhatsApp disabled |
| `WhatsApp__OwnerNotificationNumber` | **yes** | Owner's personal WhatsApp number (`+264…`) |
| `WhatsApp__OwnerNotificationTemplate__Name/__Language` | recommended | Approved utility template for owner notifications |
| `WhatsApp__ReminderTemplate__Name/__Language` | recommended | Approved utility template for reminders outside the 24 h window |
| `Instagram__AccessToken` (Page token of the linked Page), `Instagram__AccountId` (optional filter), `Instagram__AppSecret` (Instagram Login apps only) | per channel | Empty token = Instagram disabled |
| `Facebook__PageId`, `Facebook__PageAccessToken` | per channel | Empty = Messenger disabled |
| `Booking__Reminder24HoursEnabled`, `Booking__Reminder2HoursEnabled` | optional | Default `true` |
| `Booking__PendingHoldMinutes` | optional | Default `10` |
| `Admin__ApiKey` | recommended | At least 32 random characters. Empty = admin API disabled. |
| `Outbox__MaxAttempts`, `Retention__*` | optional | See `appsettings.json` |

## First deployment (VPS + Docker Compose)

1. Create the server (Ubuntu LTS), install Docker Engine and the Compose plugin, and open firewall ports **80** and
   **443** only (plus SSH).
2. Point a DNS **A record** (e.g. `bookings.vintagefades.com.na`) at the server's IP.
3. Copy the repository to `/opt/vintagefades`.
4. `cp .env.example deploy/.env` and fill it in (`chmod 600 deploy/.env`).
5. Start the stack:
   ```bash
   cd /opt/vintagefades/deploy && docker compose up -d --build
   ```
6. Check it:
   ```bash
   curl -s https://bookings.vintagefades.com.na/health/ready
   ```
   The expected result is `{"status":"Healthy",...}`. Then check `docker compose logs app` for
   "integration enabled" lines.
7. Configure the webhook URLs in the Meta App Dashboard ([META_SETUP.md](META_SETUP.md)).
8. Install the backup cron job (below) and an uptime monitor (below).

## HTTPS

Caddy gets and renews certificates automatically for `PUBLIC_HOSTNAME`. Meta requires HTTPS with a valid
certificate for webhook URLs. HSTS is sent by Caddy and also by the app in Production. Do not expose port 8080
publicly.

## Database

- PostgreSQL 16+ with the **`btree_gist`** extension. The first migration runs `CREATE EXTENSION IF NOT EXISTS
  btree_gist`. On managed services that restrict extensions, enable it in the portal or run it once as an admin.
- All times are stored as `timestamptz` in UTC.
- The connection pool uses Npgsql defaults (max 100). One instance needs far fewer connections.
- On a fresh database EF Core logs one `Failed executing DbCommand ... __EFMigrationsHistory` error before it
  creates the history table. This is expected.

## Migrations

**Default:** the app applies pending migrations at startup, then seeds missing configuration. This is safe with one instance.

**Separate step** (recommended once there are several environments): set `Database__ApplyMigrationsOnStartup=false`
and run the migration bundle before starting the new version:

```bash
docker build --target migrations -t vintagefades-migrations .
docker run --rm --network vintagefades_default vintagefades-migrations --connection "Host=db;Database=vintagefades;Username=vintagefades;Password=<password>"
```

To create a new migration after a model change (development):

```bash
dotnet ef migrations add <Name> --project src/VintageFades.Infrastructure --output-dir Persistence/Migrations
```

The test `Migrations_are_applied_and_match_the_model` fails if a model change has no migration.

## Background services

The workers run inside the web process (`Workers__Enabled=true`, the default): webhook processing, outbox
delivery, and the scheduler (holds, reminders, monitoring, retention). Run **exactly one** instance. Workers
claim rows with short locks, so an overlap of a few seconds during a redeploy does not cause duplicate sends.

## Logging and monitoring

- Production logs are structured JSON on stdout (`docker compose logs app`). Tokens, secrets and message bodies
  are not logged. Errors include the outbox or webhook id for tracing.
- `GET /health/live` returns 200 when the process is up. `GET /health/ready` checks the database and worker heartbeats.
- Add an external uptime monitor (UptimeRobot, Better Stack, Healthchecks.io free tiers) on
  `https://<host>/health/ready` with alerts to the owner by email or SMS. This also covers the case where the server itself is down.
- In-app alerts: the owner gets **SYSTEM ALERT** WhatsApp messages for stalled webhook processing (>10 min),
  stalled outbox (>30 min), messages that keep failing, invalid tokens and failed notifications.
- Operational status: `GET /api/admin/status` with the `X-Admin-Key` header.

## Backups

`deploy/backup.sh` writes a compressed `pg_dump` (custom format), checks it is readable, and keeps 14 days of dumps.

```bash
chmod +x /opt/vintagefades/deploy/backup.sh
crontab -e
# add:
30 2 * * * /opt/vintagefades/deploy/backup.sh >> /var/log/vintagefades-backup.log 2>&1
```

**Copy the backups off the server** every night, for example `rclone copy /opt/vintagefades/deploy/backups remote:vintagefades-backups`
to Backblaze B2, Hetzner Storage Box or S3. Also turn on the VPS provider's snapshot backups (low cost).

### Restore

```bash
cd /opt/vintagefades/deploy
docker compose stop app
docker compose exec -T db dropdb -U vintagefades vintagefades
docker compose exec -T db createdb -U vintagefades vintagefades
docker compose exec -T db pg_restore -U vintagefades -d vintagefades --no-owner < backups/vintagefades-<STAMP>.dump
docker compose start app
```

Test a restore into a scratch database every few months.

## Deploy a new version

```bash
cd /opt/vintagefades && git pull            # or copy the new release
./deploy/backup.sh                          # backup before every deploy
cd deploy && docker compose up -d --build app
curl -s https://<host>/health/ready
```

Webhooks that arrive during the few seconds of restart are retried by Meta.

## Rollback

1. Check out the previous release (`git checkout <previous-tag>`) and run `docker compose up -d --build app`.
2. If the failed release applied a **migration** that the old version cannot handle, restore the backup taken just
   before the deploy (see Restore). Migrations in this project are additive where possible. Always read a
   migration before deploying it.

## Cost estimate (indicative, not provider quotes)

| Item | Typical monthly cost |
|---|---|
| VPS 2 vCPU / 4 GB (e.g. Hetzner CX22-class) | about €4–8 |
| Off-site backup storage (a few GB) | about €1–3 |
| VPS snapshot backups | about 20% of VPS price |
| Domain name | about US$10–20 per year |
| **Total self-hosted** | **about €7–12 / month** |
| Managed alternative (Azure App Service B1 + Azure PostgreSQL Flexible B1ms) | about US$25–35 / month |

Meta messaging charges are separate. WhatsApp charges per delivered **template** message (utility, marketing,
authentication), with rates per country. Free-form replies within the 24-hour customer service window are not
charged, and utility templates sent inside an open window are free as well. Instagram and Messenger messaging has no
per-message charge. Check Meta's current rate card for Namibia before going live. With roughly 2 reminders and a
few owner notifications per booking, expect utility-template costs in the order of a few US cents per booking.
Confirm this with the rate card.
