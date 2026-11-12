# VINTAGE FADES BARBER SHOP – Booking & Messaging Service (Phase 1)

One booking engine and one chatbot behind three Meta channels:

- **WhatsApp Business** (the existing number via YCloud Coexistence, or the direct Meta Cloud API)
- **Instagram Direct Messages** (Instagram Messaging API)
- **Facebook Messenger** (Messenger Platform)

Customers can book, view, reschedule and cancel appointments, or ask for a person, on any channel.
All channels share one PostgreSQL database, so a slot booked on WhatsApp is unavailable on Instagram and
Messenger straight away. The database itself blocks double bookings. The owner gets WhatsApp
notifications for new bookings, cancellations, reschedules, handover requests and system problems.

Phase 1 has no website, customer portal, dashboard, payments or analytics. The design leaves room to add
them later without changes to the booking engine (see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).

## Architecture in one picture

```
WhatsApp ─┐                                   ┌─> WhatsApp replies + owner notifications
Instagram ┼─> /webhooks/{platform} ─> WebhookEvents ─> WebhookProcessor ─> ConversationEngine ─> BookingService ─> PostgreSQL
Messenger ┘   (signature check,        (stored,          (per-message                (state            (rules +
               200 OK at once)          deduplicated)     transaction)                machine)          exclusion constraint)
                                                              │
                                                              └─> OutboxMessages ─> OutboxDispatcher ─> Meta Send APIs (retries)
```

| Project | Responsibility |
|---|---|
| `src/VintageFades.Domain` | Entities, enums, slot and availability rules (no dependencies) |
| `src/VintageFades.Application` | Booking engine, chatbot, inbound pipeline, outbox, reminders, monitoring |
| `src/VintageFades.Infrastructure` | EF Core `AppDbContext`, PostgreSQL mapping, migrations, seeding |
| `src/VintageFades.Integrations.Meta` | WhatsApp (Meta direct or YCloud), Instagram and Messenger adapters, webhook parsers, signature validation |
| `src/VintageFades.Web` | ASP.NET Core host: webhook and admin controllers, background workers, health checks |
| `tests/*` | Unit, integration (real PostgreSQL through Testcontainers) and architecture tests |

## Requirements

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.401 (pinned in `global.json`, rolls forward to newer 10.0 feature bands) | .NET 10 is the current LTS |
| PostgreSQL | 16 or newer (built and tested on 18) | `btree_gist` extension needed (part of standard contrib) |
| Docker | Any recent version | Runs the integration tests and the production stack |
| dotnet-ef | 10.0.x | `dotnet tool install --global dotnet-ef` (only to create new migrations) |

## Local setup

**Whole app in Docker (database + app).** Copy `.env.dev.example` to `.env.dev` and fill it in, then:

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

The app is at http://localhost:5175 (owner dashboard at `/dashboard/`, password = `Admin__ApiKey` in `.env.dev`; Swagger at `/swagger`) and PostgreSQL at `localhost:55434`. The stack shows in
Docker Desktop as `vintagefades-dev`; start or stop that group to start or stop the database and the app together
(data is kept). After code changes, run the command above again. To run the app from the IDE instead, use the same
database: `Host=localhost;Port=55434;...` in user-secrets, as in steps 3–5 below.

**Or step by step:**

1. **Install .NET 10 SDK** from https://dotnet.microsoft.com/download/dotnet/10.0.
2. **Start PostgreSQL.** The easiest way is Docker:
   ```bash
   docker run -d --name vintagefades-db -e POSTGRES_USER=vintagefades -e POSTGRES_PASSWORD=devpassword -e POSTGRES_DB=vintagefades -p 5432:5432 postgres:18-alpine
   ```
   If you use an installed PostgreSQL, create the database and a user that can create the `btree_gist` extension
   (or create it once as a superuser: `CREATE EXTENSION btree_gist;`).
3. **Store your local secrets with user-secrets** (they stay outside the repository):
   ```bash
   cd src/VintageFades.Web
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=vintagefades;Username=vintagefades;Password=devpassword"
   dotnet user-secrets set "Meta:AppSecret" "<your Meta app secret>"
   dotnet user-secrets set "Meta:WebhookVerifyToken" "<any long random string>"
   dotnet user-secrets set "WhatsApp:OwnerNotificationNumber" "+264XXXXXXXXX"
   dotnet user-secrets set "Admin:ApiKey" "<at least 32 random characters>"
   ```
   Add the channel credentials (`WhatsApp:*`, `Instagram:*`, `Facebook:*`) when you have them. `appsettings.Example.json` lists every key.
   A channel with no credentials stays disabled, and the rest of the service still runs.
4. **Database setup.** The app applies migrations and seeds the initial data on startup
   (`Database:ApplyMigrationsOnStartup`, default `true`). The seed data is the shop settings (09:00–19:00,
   60-minute slots, Africa/Windhoek) and four placeholder barbers with working hours for every day.
   Seeding only inserts missing rows, so it is safe to run more than once. To apply migrations by hand instead:
   ```bash
   VINTAGEFADES_DESIGN_CONNECTION="Host=localhost;Database=vintagefades;Username=vintagefades;Password=devpassword" dotnet ef database update --project src/VintageFades.Infrastructure
   ```
5. **Run the application**
   ```bash
   dotnet run --project src/VintageFades.Web
   ```
   - Swagger UI (Development only): `http://localhost:5000/swagger` (see the console for the port)
   - Liveness: `GET /health/live`. Readiness (database and workers): `GET /health/ready`
6. **Expose webhooks to Meta during development** with an HTTPS tunnel, for example:
   ```bash
   cloudflared tunnel --url http://localhost:5000
   ```
   or `ngrok http 5000`. Then use `https://<tunnel-host>/webhooks/whatsapp` (or `/instagram`, `/messenger`) as the
   callback URL ([docs/META_SETUP.md](docs/META_SETUP.md)). Only use a tunnel while you are testing.

## Running the tests

```bash
dotnet test VintageFades.slnx
```

- `VintageFades.UnitTests` checks parsing (dates, times, intents, names), slot rules, time zones, signature
  validation, Meta error classification, payload builders and webhook parsers.
- `VintageFades.IntegrationTests` starts a real **PostgreSQL 18 container** (Docker must be running) and the full
  ASP.NET Core host. Meta's HTTP API is replaced by a recording fake. These tests cover the booking engine
  (including concurrency), whole chatbot conversations, webhooks, retries, reminders and acceptance scenarios A–F.
- `VintageFades.ArchitectureTests` checks the layering rules.

The results are in [docs/ACCEPTANCE_REPORT.md](docs/ACCEPTANCE_REPORT.md).

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): components, message flow, booking flow, data model
- [docs/BOOKING_RULES.md](docs/BOOKING_RULES.md): hours, slots, barber assignment, holds, concurrency
- [docs/META_SETUP.md](docs/META_SETUP.md): WhatsApp (including Coexistence), Instagram and Messenger setup, permissions, app review
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md): hosting, environment variables, HTTPS, migrations, backups, rollback
- [docs/OPERATIONS.md](docs/OPERATIONS.md): owner notifications, troubleshooting, manual bookings, recovery
- [docs/SECURITY.md](docs/SECURITY.md): secrets, tokens, signatures, customer data, access control
- [docs/ACCEPTANCE_REPORT.md](docs/ACCEPTANCE_REPORT.md): test results, what still needs Meta credentials or approval, cost estimates
