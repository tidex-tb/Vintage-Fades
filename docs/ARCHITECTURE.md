# Architecture

## Principles

1. **One source of truth.** PostgreSQL holds every booking. All channels call the same `BookingService`.
2. **Adapters are thin.** The Meta project only turns webhook JSON into `InboundMessage` records and turns
   `OutboundMessage` records into Graph API calls. It has no booking logic. An architecture test enforces this.
3. **The database guarantees consistency.** A PostgreSQL exclusion constraint makes overlapping active bookings for
   one barber impossible, whatever the application does.
4. **Nothing is lost or duplicated.** Webhooks are stored before processing and deduplicated. Replies and
   notifications go through a transactional outbox with retries.
5. **Phase 2 ready.** A website or dashboard would be another caller of `BookingService` and `AdminService`.
   Nothing in the booking engine depends on messaging.

## Projects and dependencies

```
Domain  <──  Application  <──  Infrastructure  <──┐
                  ^                                ├──  Web (composition root)
                  └───────  Integrations.Meta  <──┘
```

| Project | Contents |
|---|---|
| **Domain** | Entities (`Barber`, `Booking`, `Customer`, `Conversation`, ...). `ShopCalendar` handles slot generation, UTC/local conversion and slot validation. `BarberAvailability` holds the pure availability rule. |
| **Application** | `BookingService` (the booking engine). `ConversationEngine` (finite-state chatbot) with `DateParser`, `TimeParser`, `IntentRecognizer`, `BarberMatcher`, `NameParser`. `InboundMessageHandler`, `WebhookIngestionService`, `WebhookProcessor`. `NotificationService` (outbox writer), `OutboxDispatcher`. `ReminderScheduler`, `OperationalMonitor`, `DataRetentionService`, `AdminService`. Interfaces: `IAppDbContext`, `IMessagingChannel`, `IWebhookPayloadParser`, `IWorkSignal`. |
| **Infrastructure** | `AppDbContext` (EF Core + Npgsql), migrations (including the exclusion constraint), `DatabaseInitializer` (idempotent seed), `WorkSignal`. |
| **Integrations.Meta** | `WhatsAppChannel`, `InstagramChannel`, `MessengerChannel` (send). `WhatsAppWebhookParser`, `InstagramWebhookParser`, `MessengerWebhookParser`. `MetaSignatureValidator`, `MetaErrorClassifier`. YCloud partner path for WhatsApp Coexistence: `YCloudWhatsAppChannel`, `YCloudWebhookParser`, `YCloudSignatureValidator`, `YCloudErrorClassifier`. `WhatsApp:Provider` picks exactly one WhatsApp adapter and parser. |
| **Web** | `WebhooksController`, `AdminController` (API-key protected), background workers, health checks, rate limiting, HTTPS/HSTS, OpenAPI. |

Application uses EF Core abstractions (`DbSet`) through `IAppDbContext` and has no reference to the PostgreSQL
provider. This is a deliberate trade-off: it avoids writing a repository for each entity while keeping the
provider-specific parts (exclusion constraint detection, sequences) in Infrastructure.

## Inbound message flow

1. Meta POSTs to `/webhooks/{whatsapp|instagram|messenger}`.
2. `WebhooksController` reads the raw body (1 MB limit, rate limited) and checks `X-Hub-Signature-256` against the
   app secret. An invalid or missing signature gets **401** and nothing is stored.
3. `WebhookIngestionService` saves a `WebhookEvent` (raw payload plus SHA-256 hash, unique per platform). A
   redelivery of the same payload is ignored. The controller returns `200 EVENT_RECEIVED` straight away, so Meta
   never times out.
4. `WebhookWorker` wakes immediately (in-process signal, 5-second poll as a fallback). `WebhookProcessor` claims
   the event with a short row lock, then parses it with the right `IWebhookPayloadParser`.
5. For each message, `InboundMessageHandler` opens **one database transaction** and:
   - skips the message if its platform message id was already processed (second dedupe layer)
   - sends messages from the owner's number to `OwnerCommandHandler`, never to the customer chatbot
   - finds or creates the `Customer` and `Conversation` (by platform-scoped id)
   - stores the inbound `ConversationMessage`
   - runs `ConversationEngine`, which may call `BookingService` (holds, confirmations, cancellations)
   - stores outbound `ConversationMessage` rows and writes `OutboxMessage` rows
   - commits. Either everything above happens or none of it does.
6. Delivery status webhooks (WhatsApp `statuses`) update `NotificationLogs.DeliveryStatus`.
7. A failure is retried with backoff (15 s, 30 s, 60 s, ...). After 5 attempts the event is marked `Failed` and
   the owner gets a system alert. Malformed JSON fails at once.

## Outbound flow (outbox)

`OutboxWorker` → `OutboxDispatcher`:

1. Claims due `Pending` rows (short lock, so a second instance during a deploy cannot send twice).
2. Reminders are re-checked first. The booking must still be confirmed at the same start time, otherwise the
   reminder is `Skipped`.
3. Picks the `IMessagingChannel` for the platform. If the channel is not configured, the row is `Failed` and visible in diagnostics.
4. Applies the messaging-window rules:
   - Chat replies are sent as free-form text with buttons, lists or quick replies.
   - Owner notifications use the approved WhatsApp template when one is configured, otherwise free-form text.
   - Reminders are sent as text if the customer wrote in the last 23.5 hours. Otherwise they use the approved
     WhatsApp template, or are `Skipped` (Instagram and Messenger have no template option in Phase 1).
5. A transient error (HTTP 5xx, timeout, rate limit) is retried with exponential backoff (30 s doubling, capped at
   60 min, 8 attempts). A permanent error (invalid parameter, undeliverable) is marked `Failed` without retry.
   Authentication errors and failed owner notifications raise an owner alert, at most one per platform per hour.
   A failed system alert never raises another alert.
6. Every attempt is written to `NotificationLogs`.

Because the booking and its outbox rows commit together, **a confirmed booking always has its confirmation
queued**. A failed send is retried and never creates another booking (scenario F).

## Booking flow

```
customer gives date/time/barber
  → ShopCalendar.ValidateSlot          (past? opening hours? on the hour? advance window?)
  → BookingService.GetSlotStatusAsync  (barber active, working hours, time off, occupying bookings)
  → available?
       yes → CreateBookingAsync(HoldOnly)  → Pending hold (10 min) → customer confirms → ConfirmBookingAsync → Confirmed
       no  → other barbers at the same time, then nearby times (max 5) → customer picks → re-checked again
```

The rules are described in [BOOKING_RULES.md](BOOKING_RULES.md).

## Conversation state machine

States are stored in `Conversations.CurrentState`. The draft (`Conversations.CurrentBookingIntent`, jsonb) holds the
intent, date, time, barber preference, held booking id, offered alternatives and clarification count. A customer
can therefore continue a conversation later without repeating anything. An unfinished conversation older than
24 hours starts again from the menu.

```
Idle/Greeting ─book─> CollectingBookingDate ─> CollectingBookingTime ─> SelectingBarber ─> (check)
                                                                                           ├─ free ─> [CollectingCustomerName] ─> AwaitingBookingConfirmation ─yes─> BookingConfirmed
                                                                                           └─ busy ─> OfferingAlternatives ─pick─> (check again)
Idle ─cancel─> [AwaitingCancellationSelection] ─> AwaitingCancellationConfirmation ─yes─> Idle
Idle ─reschedule─> [Rescheduling (select)] ─> date/time/barber ─> AwaitingBookingConfirmation ─yes─> BookingConfirmed
any ─"human"/2 misunderstandings+yes─> HumanSupportRequested (bot silent until RESUME)
```

States are skipped when the customer has already supplied that information ("Book me with Barber 2 tomorrow at
15:00" goes straight to the availability check).

## Data model

```
Barbers 1─* BarberWorkingHours           (unique BarberId+DayOfWeek)
Barbers 1─* BarberTimeOff
Barbers 1─* Bookings *─1 Customers
Customers 1─* Conversations 1─* ConversationMessages
ShopSettings (single row)
WebhookEvents   (unique Platform+PayloadHash)
OutboxMessages  (unique DeduplicationKey where not null)
NotificationLogs (→ OutboxMessages by id, → provider message id for delivery status)
```

Key constraints and indexes:

| Object | Purpose |
|---|---|
| `EX_Bookings_Barber_NoOverlap`: `EXCLUDE USING gist (BarberId WITH =, tstzrange(StartAtUtc, EndAtUtc) WITH &&) WHERE Status IN ('Pending','Confirmed')` | Makes double booking impossible |
| `IX_Bookings_BookingReference` (unique) | Reference lookup |
| `booking_reference_seq` | Human-readable references `VF-YYYY-NNNNNN` |
| `IX_Barbers_StationNumber` (unique where active) | One active barber per station |
| `IX_Customers_WhatsAppId / InstagramScopedUserId / FacebookScopedUserId` (unique) | One customer per platform identity |
| `IX_Conversations_Platform_PlatformConversationId` (unique) | One conversation per customer per channel |
| `IX_ConversationMessages_Platform_PlatformMessageId` (unique) | Message-level idempotency |
| Check constraints | `End > Start`, a Pending booking must have a hold expiry |

All timestamps are `timestamptz` in UTC. Local times are only produced for display, through `ShopCalendar`
(`ShopSettings.TimeZoneId`, default `Africa/Windhoek`).

### Customer identity across channels

WhatsApp customers are identified by their WhatsApp id, which is their phone number. Instagram and Messenger give
only page-scoped ids with no phone number. Identities are **never merged automatically**, because nothing proves
that an Instagram user owns a given phone number. A customer manages their bookings on the channel they used to
make them. Linking identities safely (for example with a one-time code sent over WhatsApp) is a Phase 2 item.

## Background workers

| Worker | Interval | Work |
|---|---|---|
| `WebhookWorker` | Signal or 5 s | Process stored webhook events |
| `OutboxWorker` | Signal or 5 s | Deliver outbox messages |
| `SchedulerWorker` | 60 s | Release expired holds, queue reminders, detect stalled queues; daily retention purge |

The workers record heartbeats. `/health/ready` reports `Degraded` when one has not run for 5 minutes.

## Deliberate Phase 1 limits

- Run **one** application instance. Row claiming makes a brief overlap during a deploy safe, but message ordering
  per conversation assumes one worker.
- No rich media understanding. Images, voice notes and stickers get a polite "text only" reply.
- Natural-language understanding is rule-based (no paid AI dependency).
