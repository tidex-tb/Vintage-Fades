# Booking Rules

The rules below come from configuration in the database (`ShopSettings`, `Barbers`, `BarberWorkingHours`,
`BarberTimeOff`) and from `appsettings` / environment variables (`Booking:*`). None of them is hard-coded in
channel code.

## Opening hours and slot generation

| Setting | Initial value | Where |
|---|---|---|
| Shop name | VINTAGE FADES BARBER SHOP | `ShopSettings.ShopName` |
| Time zone | `Africa/Windhoek` (UTC+2, no daylight saving) | `ShopSettings.TimeZoneId` |
| Opening / closing | 09:00 / 19:00 | `ShopSettings.OpeningTime` / `ClosingTime` |
| Appointment length | 60 minutes | `ShopSettings.AppointmentDurationMinutes` |
| Advance window | 30 days | `ShopSettings.BookingAdvanceDays` |
| Cancellation/reschedule notice | 2 hours | `ShopSettings.CancellationNoticeHours` |
| Reminders | 1440 and 120 minutes before | `ShopSettings.ReminderMinutesBefore` (+ `Booking__Reminder24HoursEnabled`, `Booking__Reminder2HoursEnabled` switches) |
| Hold length | 10 minutes | `Booking:PendingHoldMinutes` |
| Alternatives shown | 5 | `Booking:MaxAlternatives` |

`ShopCalendar` generates start times from opening time in steps of the appointment length, while
`start + length <= closing`. With the initial settings that gives **09:00, 10:00, … 18:00** (10 slots). The last
appointment finishes at 19:00.

A requested slot is rejected when:

| Check | Customer is told |
|---|---|
| Date before today (Windhoek time) or start time already passed | "That date has already passed." / "14:00 has already passed." |
| Date after today + advance window | "We take bookings up to 30 days ahead (until …)." |
| Before opening, at/after closing, or would end after closing | Opening hours and last start time |
| Not on a slot boundary (e.g. 14:30) | "Our appointments start on the hour …" |

All timestamps are stored in UTC. "Today", "tomorrow" and weekday names are worked out in the shop time zone, so
a message sent at 00:30 Windhoek time (22:30 UTC the previous day) correctly treats "today" as the new day.

## Barbers, stations, capacity

- Four placeholder barbers ("Barber 1" … "Barber 4") on stations 1–4 are seeded. Rename them, deactivate them or
  add barbers in the database or through `PUT /api/admin/barbers/{id}`. No code change is needed.
- An active barber must have a unique station number (unique partial index).
- Each barber has a row per weekday in `BarberWorkingHours` (`IsWorking`, `OpeningTime`, `ClosingTime`). A slot must
  sit entirely inside both the shop hours and the barber's hours.
- `BarberTimeOff` (UTC ranges) blocks any slot that overlaps it. Use it for leave, breaks or training.
  `POST /api/admin/barbers/{id}/time-off` adds an entry and lists any confirmed bookings that overlap it. Those are
  **not** cancelled automatically; the owner decides.
- Capacity is one customer per barber at a time. With four active barbers, at most four appointments can run at
  once (40 a day). A fifth simultaneous booking is impossible because each barber can hold only one booking per slot.

## Which statuses occupy a slot

| Status | Occupies the barber? | Meaning |
|---|---|---|
| `Pending` | **Yes, until `HoldExpiresAtUtc`** | Temporary hold while the customer confirms (10 min) |
| `Confirmed` | **Yes** | Booked |
| `Cancelled` | No | Cancelled by customer or shop, declined hold, or expired hold |
| `Completed` | No | Reserved for Phase 2 (after the appointment) |
| `NoShow` | No | Reserved for Phase 2 |

### Pending holds and expiry

When the bot finds a free slot it creates a `Pending` booking right away, so nobody else can take the slot while
the customer confirms. Holds expire after `PendingHoldMinutes`:

- The scheduler marks expired holds `Cancelled` ("Hold expired") every minute.
- Before **any** insert or move for a barber/time, `BookingService` first releases expired holds for that exact
  slot in the same transaction. An expired hold therefore never blocks anyone, even if the scheduler is behind.
- If a customer confirms after their hold expired and the slot is still free, the same booking (same reference) is
  re-acquired. If someone else has taken it, the customer is told "Sorry, that slot was just taken" and is shown
  alternatives.

## Barber assignment

- **Specific barber requested.** Only that barber is tried. If they are busy, the bot offers up to 3 other barbers
  at the same time plus that barber's nearest other times (5 options in total). If nobody can be offered that
  day, it suggests other times.
- **"Anyone" / "first available".** Available barbers are sorted by **fewest active bookings that day**, then by
  station number. The order is deterministic and spreads the load evenly instead of always filling Barber 1. If
  the first candidate loses a race, the next one is tried in the same transaction.
- **Rescheduling.** The customer's current barber is preferred unless they name another one.

## Alternative suggestions

`GetAlternativeSlotsAsync(date, time, preferredBarber)`:

1. All free (time, barber) pairs on the requested day, sorted by distance from the requested time. On a tie the
   earlier time wins, and the preferred barber comes first.
2. At most two barbers per time, so the list shows a range of times.
3. If fewer than 5 are found, the following days (up to 7 days, within the advance window) are added, nearest to
   the requested time of day first.
4. When the customer picks an option, availability is **checked again** before the hold is created.

## Confirmation

The customer is told "confirmed" only after `ConfirmBookingAsync` has committed `Status = Confirmed`. The
confirmation and the owner's "NEW BOOKING" notification are written to the outbox **in the same transaction**,
so they cannot be lost. If sending fails, the message is retried and the booking is not touched.

References have the form `VF-<year>-<6-digit sequence>`, for example `VF-2026-000123`. They come from the
PostgreSQL sequence `booking_reference_seq` and are unique (unique index).

## Cancellation

1. Customer asks to cancel ("cancel my appointment").
2. Their upcoming confirmed bookings on this channel are listed. If there are several, they choose one.
3. The bot asks for explicit confirmation. Only a clear *yes* cancels. "Maybe" or anything unclear triggers a
   clarification, and the booking stays confirmed.
4. The booking becomes `Cancelled` (`CancelledAtUtc`, `CancellationReason`), which frees the slot at once for every channel.
5. The owner is notified and the customer gets a confirmation that includes the reference.

**Notice period.** Inside `CancellationNoticeHours` (2 h) the bot does not cancel or reschedule automatically. It
tells the customer the team will be in touch, notifies the owner ("CUSTOMER NEEDS HUMAN ASSISTANCE"), and hands
the chat to a human. The shop can always cancel through the admin API.

## Rescheduling

1. The bot finds the booking (asks which one if there are several) and checks the notice period.
2. It collects only the missing parts of the new date, time and barber.
3. It checks availability with the booking's own slot excluded, and offers alternatives if needed.
4. After the customer confirms, **a single UPDATE** changes barber, start and end. The exclusion constraint checks
   the new slot inside that statement. The old slot is released only if the new one is secured, and the booking is
   never cancelled first. The reference stays the same.
5. The customer and owner are notified, and the owner sees the old and new details.

## Concurrency protection

The guarantee lives in PostgreSQL, not in application checks:

```sql
ALTER TABLE "Bookings" ADD CONSTRAINT "EX_Bookings_Barber_NoOverlap"
  EXCLUDE USING gist ("BarberId" WITH =, tstzrange("StartAtUtc", "EndAtUtc", '[)') WITH &&)
  WHERE ("Status" IN ('Pending', 'Confirmed'));
```

- Two transactions that insert or move overlapping active bookings for the same barber can never both commit.
- The losing transaction gets an exclusion violation (`23P01`). When the two inserts happen at exactly the same
  moment, PostgreSQL instead aborts one of them as a deadlock (`40P01`). `BookingService` treats both as "lost the
  race". Each attempt runs inside a savepoint (EF Core wraps `SaveChanges` in one inside a transaction), so the
  surrounding conversation transaction continues. The service then tries the next barber or returns `SlotTaken`.
- The constraint also protects against duration changes (overlapping ranges, not only identical start times) and
  against manual SQL inserts.
- Tests: 10 parallel requests for one barber produce exactly 1 booking. 12 parallel "anyone" requests produce
  exactly 4 bookings, one per barber.

## Idempotency

- A webhook redelivery is ignored (`WebhookEvents` unique per payload hash).
- A repeated platform message id is ignored (`ConversationMessages` unique per platform message id), so a retried
  "yes" can never confirm twice.
- One reminder per booking, offset and start time (`OutboxMessages.DeduplicationKey`).
