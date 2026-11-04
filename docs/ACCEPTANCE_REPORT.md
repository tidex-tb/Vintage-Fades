# Phase 1 Acceptance Report

**Date:** 2 November 2026
**Build:** .NET SDK 10.0.401 (LTS), EF Core 10.0.14, Npgsql 10.0.3, PostgreSQL 18 (Testcontainers `postgres:18-alpine`)

## Test results

```
dotnet test VintageFades.slnx
Passed!  - Failed: 0, Passed: 134 - VintageFades.UnitTests
Passed!  - Failed: 0, Passed:   7 - VintageFades.ArchitectureTests
Passed!  - Failed: 0, Passed:  86 - VintageFades.IntegrationTests   (real PostgreSQL 18; Meta and YCloud hosts)
Total: 227 passed, 0 failed
```

The final integration suite passed 3 runs in a row with no failures, including the concurrency tests.
Production smoke test: the `runtime` Docker image was built and started against a fresh PostgreSQL 18 container.
Migrations applied, seed data inserted (4 barbers, `Africa/Windhoek`), `/health/ready` returned `Healthy` (database
and workers), the webhook verification handshake echoed the challenge, Swagger returned 404 in Production, and a
webhook for an unconfigured channel returned 404.

## Required booking tests (spec §17)

| # | Requirement | Test(s) |
|---|---|---|
| 1 | Book valid slot | `BookingServiceTests.Books_a_valid_available_slot_and_stores_utc` |
| 2 | Specific barber | `Books_a_specific_barber` |
| 3 | Any available barber | `Any_barber_spreads_load_instead_of_always_using_the_first` |
| 4 | Unavailable barber | `Unavailable_barber_is_rejected_and_other_barbers_are_offered` |
| 5 | Alternative barbers | same, plus `ChatbotConversationTests.Unavailable_barber_gets_other_barbers_at_the_same_time` |
| 6 | Alternative times | `Fifth_simultaneous_booking_is_refused_and_alternatives_are_close_to_the_request`, `Alternatives_move_to_next_day_when_the_day_is_full`, `Fully_booked_time_offers_alternative_times` |
| 7–10 | Past date / before opening / after closing / not on the hour | `Rejects_invalid_slots` (5 cases), `ShopCalendarTests.Validates_slots`, `Rejects_a_slot_earlier_today` |
| 11 | Barber time off | `Rejects_booking_during_barber_time_off` |
| 12 | Outside working hours | `Rejects_booking_outside_barber_working_hours` |
| 13, 15 | Cancel and release slot | `Cancelling_releases_the_slot_and_notifies_the_owner` |
| 14 | Reschedule | `Rescheduling_keeps_reference_and_frees_the_old_slot`, `Failed_reschedule_leaves_the_original_booking_intact` |
| 16 | Prevent double booking | `Database_constraint_rejects_overlapping_active_bookings_even_without_the_service` |
| 17 | Simultaneous requests | `Simultaneous_requests_for_the_same_barber_produce_exactly_one_booking` (10 parallel → 1), `Simultaneous_any_barber_requests_never_exceed_four_bookings_per_slot` (12 parallel → 4) |
| 18 | Expired pending bookings | `Hold_blocks_others_until_it_expires_then_is_released`, `Expired_hold_is_reacquired_on_confirm_when_still_free` |
| 19 | Time zone conversion | `ShopCalendarTests.Converts_windhoek_local_time_to_utc` |
| 20 | Midnight / boundary | `Today_follows_the_shop_time_zone_around_midnight`, `Hours_are_evaluated_in_windhoek_time_across_midnight` |

## Chatbot tests

Greeting (`Greeting_shows_the_welcome_menu`), booking request and full flow
(`Full_booking_conversation_confirms_only_after_database_commit`), missing date/time/barber
(`Only_missing_information_is_requested`, `Menu_number_starts_booking`), natural-language dates and times
(`DateParserTests`, `TimeParserTests`, `Availability_question_lists_open_times`, `Bare_hour_with_one_sensible_reading_is_used`),
ambiguous dates (`Ambiguous_next_weekday_is_confirmed_before_use`, `Next_week_asks_which_day`), ambiguous times
(`Ambiguous_hour_is_clarified_with_opening_hours`), cancellation (`Cancellation_requires_explicit_confirmation`,
`Saying_no_keeps_the_booking`, `Customer_with_several_bookings_chooses_which_to_cancel`), rescheduling
(`Rescheduling_moves_the_existing_booking`), human assistance (`Human_request_pauses_the_bot_until_owner_resumes`,
`Owner_can_reply_to_a_customer_through_the_bot`), invalid messages (`Two_misunderstandings_offer_human_help`,
`Past_dates_and_closed_hours_are_explained`), resumption (`Conversation_resumes_where_it_left_off`,
`Stale_conversation_restarts_after_a_day`), interactive buttons (`Interactive_button_reply_is_understood`).

## Integration tests (mocked Meta)

Verification handshake (3 platforms), invalid signature (3 platforms), duplicate delivery, the same message in a
different delivery, correct endpoint, token and platform identification per channel, echo filtering, transient failure
retried with backoff, permanent failure recorded, invalid token alert, delivery statuses, reminders (sent once,
window respected, cancelled bookings skipped), admin API auth and manual booking, health checks, migrations match
the model, exclusion constraint present, idempotent seeding.

## End-to-end scenarios (spec §17)

| Scenario | Test | Result |
|---|---|---|
| A: WhatsApp booking blocks Instagram | `A_whatsapp_booking_is_immediately_unavailable_on_instagram` | Pass |
| B: Messenger booking notifies owner on WhatsApp | `B_messenger_booking_notifies_owner_on_whatsapp` | Pass |
| C: WhatsApp cancellation frees slot everywhere | `C_cancelled_whatsapp_slot_is_immediately_bookable_elsewhere` | Pass |
| D: Concurrent booking, one winner | `D_two_customers_racing_for_the_same_barber_only_one_wins` + the two concurrency tests above | Pass |
| E: Human request → owner notified, bot paused | `E_human_request_notifies_owner_and_pauses_bot` | Pass |
| F: Confirmation fails → retried, no second booking | `F_failed_confirmation_is_retried_without_a_second_booking` | Pass |

## Defects found and fixed during verification

1. **Alternative-time ordering was wrong across the day.** `TimeOnly` subtraction wraps around midnight
   (13:00 − 14:00 = 23 h), so earlier alternatives were ranked last. Fixed by comparing `TimeSpan` values.
2. **Concurrent conflicting inserts could raise a PostgreSQL deadlock (40P01)** instead of an exclusion
   violation. This is normal exclusion-constraint behaviour. It is now treated as "lost the race", and Npgsql's
   wrapping execution strategy was replaced with a non-retrying one so the savepoint handling applies.

## Acceptance criteria status

| Criterion | Status |
|---|---|
| Four barbers, 60-minute appointments, 09:00–19:00, last start 18:00 | ✅ Tested |
| No overlapping active bookings per barber | ✅ Enforced by PostgreSQL, tested under concurrency |
| Alternative barbers and times suggested | ✅ Tested |
| Book / cancel / reschedule | ✅ Tested end to end |
| One shared database for all channels | ✅ Tested (scenarios A, C) |
| WhatsApp / Instagram / Messenger messages enter the chatbot | ✅ Tested with signed, Meta-shaped payloads. ⚠️ **Not yet tested against live Meta** (no credentials) |
| Confirmations sent through the originating channel | ✅ Tested (endpoint and token checked per platform) |
| Owner notifications through WhatsApp | ✅ Tested with the mocked API. ⚠️ Needs a configured number and template on live Meta |
| Duplicate webhooks never duplicate bookings | ✅ Tested |
| Failed outbound messages retried safely; failures recorded | ✅ Tested |
| Provider outages handled gracefully | ✅ Transient/permanent/auth paths tested |
| Webhook signatures validated | ✅ Tested |
| Credentials protected; HTTPS in production | ✅ Implemented (env vars and user-secrets; Caddy TLS + HSTS). HTTPS not exercised in tests |
| Documentation for setup, Meta prerequisites, deployment, recovery | ✅ `README.md`, `docs/*` |

## What works locally (verified)

Everything in the repository. With Docker or PostgreSQL available, `dotnet test` exercises the whole system,
including the HTTP webhook endpoints, the background-processing logic, and outbound calls in Meta's documented
request formats against a recording fake.

## What requires Meta credentials (not yet done)

- App ID and app secret, a webhook verify token, and the HTTPS callback URLs saved in the App Dashboard.
- WhatsApp: phone number ID, WABA ID, a system-user token, and the owner's personal number.
- Messenger: Page ID and Page token. Instagram: IG account ID, token and Instagram app secret.
- A **live test of each channel** (see [META_SETUP.md §8](META_SETUP.md#8-testing-each-platform)). Until then the
  integrations are implemented and tested against mocks only, **not verified against Meta**.

## What requires Meta approval

- Business verification of the portfolio.
- Messenger: Advanced Access for `pages_messaging` (App Review) to message the public.
- Instagram: probably Advanced Access for `instagram_business_manage_messages` (App Review). To confirm.
- WhatsApp: display-name approval for a production number; approval of the `appointment_reminder` and
  `shop_owner_update` utility templates.
- App switched to Live mode.

## WhatsApp through YCloud (chosen: Coexistence, option A)

Implemented with `WhatsApp__Provider=YCloud`: sending through YCloud's API, signed webhooks on `/webhooks/ycloud`
(`YCloud-Signature` checked, 5-minute replay window), delivery statuses, and an automatic bot pause when the owner
replies from the WhatsApp Business app (`whatsapp.smb.message.echoes`, resumes after 12 h or `RESUME`).
Tests: `YCloudWhatsAppTests` (7 integration tests: reply from shop number, full booking + owner alert, bad/stale
signatures, Meta route disabled, auto-pause and resume, delivery status, retry on 429) and 25 unit tests
(signature, error mapping, payloads, parser). Built from YCloud's public documentation; **not yet tested against a
live YCloud account**, and Namibia (+264) Coexistence support is **not yet confirmed by YCloud**.

## WhatsApp Coexistence

- **Supported by Meta?** Yes, as "Onboarding WhatsApp Business app users". It needs Business app ≥ 2.24.17,
  Embedded Signup (v4+), and onboarding by a **Solution Partner or Tech Provider**.
- **Eligibility verified?** **No.** No access to the shop's accounts, and an app built only for the shop does not
  meet the partner requirement. No onboarding was attempted and **nothing on the existing number was changed**.
- **Decision needed from the owner:** option A (coexistence through a Solution Partner), B (become a Tech
  Provider), C (new Cloud API number, recommended to start fastest with zero risk), or D (move the existing number;
  needs explicit written approval). See [META_SETUP.md §2.2](META_SETUP.md#22-options-the-owner-must-choose-nothing-irreversible-happens-without-approval).

## Remaining before go-live

1. Confirm with YCloud: +264 Coexistence support and which plan includes API access; sign up and connect the number (META_SETUP §2.3).
2. Create the Meta app, connect the assets, get permissions and approvals, and create the templates ([META_SETUP.md](META_SETUP.md)).
3. Provision the VPS, DNS and `.env`, then `docker compose up` ([DEPLOYMENT.md](DEPLOYMENT.md)).
4. Configure webhooks and run the live test on every channel.
5. Rename the barbers (`PUT /api/admin/barbers/{id}`), confirm opening hours, notice period and advance window.
6. Install the backup cron with off-site copy, and an uptime monitor.
7. Set a calendar reminder for the Instagram token refresh (~every 45 days).
8. Publish a privacy policy and data-deletion instructions (Meta requires them).

## Known limitations (by design for Phase 1)

- Rule-based language understanding (English). Unusual phrasing leads to clarification, and to a human after 2 attempts.
- Customer identities are not linked across channels (no safe proof of ownership). Bookings are managed on the channel used to make them.
- When the owner replies from the WhatsApp Business app (coexistence) or Meta Business Suite, the bot is not
  paused automatically. Use the handover flow and `RESUME`.
- Instagram and Messenger reminders are only sent within 24 hours of the customer's last message. Meta deprecated
  Messenger message tags, and Instagram has no templates.
- Run a single application instance.

## Estimated running costs (indicative, not quotations)

| Item | Estimate |
|---|---|
| Self-hosted VPS + backups + domain | about €7–12 per month |
| Managed cloud alternative | about US$25–35 per month |
| Instagram / Messenger messaging | No per-message charge from Meta |
| WhatsApp service replies within 24 h | Free |
| WhatsApp utility templates (reminders and owner alerts outside the window) | Charged per delivered message at Meta's rate for Namibia. Expect a few US cents per booking at most. **Check Meta's current rate card.** |
| Optional Solution Partner for Coexistence (option A) | Partner's own monthly fee (varies; get a quote) |
