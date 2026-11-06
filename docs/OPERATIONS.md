# Operations Guide (for the owner)

## How you receive notifications

All notifications arrive on **your personal WhatsApp** (`WhatsApp__OwnerNotificationNumber`), sent from the shop's
WhatsApp Business number:

| Message | When |
|---|---|
| **NEW BOOKING – VINTAGE FADES** | A customer confirmed, or a manual booking was made |
| **BOOKING CANCELLED** | A confirmed booking was cancelled |
| **BOOKING RESCHEDULED** | A booking moved (old and new details are shown) |
| **BOT PAUSED** | You replied to a customer yourself in the WhatsApp Business app; the bot stays quiet in that chat |
| **CUSTOMER NEEDS HUMAN ASSISTANCE** | The customer asked for a person, the bot could not understand them, or they want to cancel/move inside the 2-hour notice period |
| **SYSTEM ALERT – VINTAGE FADES** | Something is wrong: incoming messages are stuck, messages keep failing, a token is invalid, or a message could not be processed |

> Owner notifications use the approved template `WhatsApp__OwnerNotificationTemplate__Name`. Without it, WhatsApp
> only delivers them if **you** messaged the business number in the last 24 hours. Every notification is also
> recorded in the database, so none are silently lost. See the status check below.

## Talking to customers yourself (human handover)

When you get **CUSTOMER NEEDS HUMAN ASSISTANCE**, the bot has stopped replying in that chat. The message includes
a conversation number, for example `#42`. Send these commands from your personal WhatsApp **to the business number**:

| Command | Effect |
|---|---|
| `REPLY 42 Hi John, this is Peter from Vintage Fades…` | Sends your text to that customer through the bot (WhatsApp, Instagram or Messenger). Only within 24 hours of the customer's last message. |
| `RESUME 42` | Hands the chat back to the bot (it starts fresh from the menu the next time the customer writes) |
| `HELP` | Shows the commands |

**WhatsApp (YCloud Coexistence):** just reply in your **WhatsApp Business app** as usual. You see every chat there,
including what the bot said. As soon as you reply to a customer yourself, the bot goes quiet in that chat, stops
holding any slot it offered, and sends you **BOT PAUSED – #42**. It starts answering that customer again after
10 minutes (`Chatbot__ManualReplyPauseMinutes`), or straight away when you send `RESUME 42`.

**Instagram and Messenger:** reply in **Meta Business Suite**, then send `RESUME <id>`. The bot cannot see replies made there.

Admin API alternative: `POST /api/admin/conversations/42/resume`.

## Checking that everything works

```bash
curl -s https://<host>/health/ready
curl -s -H "X-Admin-Key: <key>" https://<host>/api/admin/status
```

`status` shows pending and failed webhook events, pending, failed and skipped messages (last 24 h), chats waiting
for a human, the last webhook received, the last message sent, and which channels are configured.

What to look for:

- `pendingOutboxMessages` growing → messages are not going out. Look for SYSTEM ALERTs and check the logs.
- `failedOutboxMessagesLast24h > 0` → see `GET /api/admin/outbox/failed` for the reason.
- `lastWebhookReceivedAtUtc` very old during opening hours → Meta is not delivering. Check webhook settings and the app's Live mode.

## When an integration fails

| Symptom | Likely cause | Fix |
|---|---|---|
| SYSTEM ALERT "AuthenticationFailed" / Meta error 190 | Token expired or revoked (Instagram tokens last ~60 days) | Generate or refresh the token ([META_SETUP.md](META_SETUP.md)), update the environment variable, restart: `docker compose up -d app` |
| Meta error 131047, or reminders `Skipped` with "24-hour messaging window" | Customer last wrote more than 24 h ago and no approved template is configured | Configure and approve the reminder/owner templates |
| Meta errors 4, 80007, 130429 (rate limit) | Too many messages in a short time | Nothing to do. The service retries with backoff. |
| HTTP 5xx / timeouts | Meta outage or network problem | Nothing to do. Retries run for about an hour (8 attempts, 30 s doubling), then the message is marked failed and you get an alert. |
| WhatsApp messages fail with YCloud 403 (e.g. balance) | YCloud wallet empty or account restricted | Top up the YCloud wallet, then retry failed messages (below) |
| No messages arriving at all | Webhook unsubscribed, app not Live, server down | Check the App Dashboard webhooks (green tick), `/health/ready`, and the uptime monitor |
| One channel down | Only that channel is affected | The other channels keep working, because each channel is independent |

**Retrying failed messages** after you fix the cause:

```bash
curl -s -H "X-Admin-Key: <key>" https://<host>/api/admin/outbox/failed
curl -s -X POST -H "X-Admin-Key: <key>" https://<host>/api/admin/outbox/<id>/retry
```

Booking data is never changed by a delivery failure. A booking that was confirmed stays confirmed.

## Troubleshooting a booking

- **See a day's bookings:** `GET /api/admin/bookings?date=2026-11-07` lists reference, status, time, barber,
  customer, phone and source.
- **Customer says they were not confirmed:** check the booking status. If it is `Confirmed`, the confirmation may
  have failed to deliver. Check `/api/admin/outbox/failed`.
- **"Hold expired" cancellations:** the customer did not confirm within 10 minutes. This is normal.
- **Slot shows as taken but nobody is booked:** an active hold (`Pending`) blocks it for up to 10 minutes.
- **A barber is off sick:** add time off: `POST /api/admin/barbers/{id}/time-off` with
  `{"startDate":"2026-11-07","startTime":"09:00","endDate":"2026-11-07","endTime":"19:00","reason":"Sick"}`.
  The response lists confirmed bookings that overlap. Contact those customers and cancel or move them.
- **Rename a barber or change who is active:** `PUT /api/admin/barbers/{id}` with
  `{"displayName":"Tjeripo","isActive":true}`.

## Manual bookings during downtime

If a messaging channel or the whole service is down:

1. **Service is up, a channel is down:** take bookings by phone or in person and enter them with the admin API.
   They use the same engine, so double bookings are still impossible:
   ```bash
   curl -s -X POST -H "X-Admin-Key: <key>" -H "Content-Type: application/json" \
     -d '{"customerName":"John","phoneNumber":"+264811234567","date":"2026-11-07","time":"14:00:00","barberId":2}' \
     https://<host>/api/admin/bookings
   ```
   A `409 Conflict` means the slot is taken (the response says why). Leave out `barberId` to take the first available barber.
2. **Whole service is down:** keep a paper or spreadsheet list for that period. When the service is back, enter
   each booking with the command above **before** reopening online bookings. Any conflict is reported and must be
   resolved with the customer.
3. Cancel a booking: `POST /api/admin/bookings/VF-2026-000123/cancel` with `{"reason":"Customer phoned"}`.

## Restoring the service

1. `docker compose ps`: are all three containers up? If not, run `docker compose up -d`.
2. `docker compose logs --tail=200 app`: look for errors at startup (database connection, configuration).
3. Database will not start or data is damaged: restore the latest backup ([DEPLOYMENT.md](DEPLOYMENT.md#restore)).
4. Server lost: create a new server, deploy, restore the latest off-site backup, and point DNS at the new IP.
   Webhook URLs stay the same.
5. After any outage, check `/api/admin/status`. Webhooks that Meta delivered while the service was up but
   processing was stuck are processed automatically. Messages sent while the server was completely down are
   retried by Meta for a limited time only. Ask affected customers to resend if needed.

## Customers who abandon a conversation

No action is needed. Holds expire after 10 minutes, and an unfinished conversation older than 24 hours starts
again from the menu.

## The owner dashboard (appointments and earnings)

Open `https://<your-railway-address>/dashboard/` on your phone or computer. It is part of the bot service, so it needs
no extra website, domain or hosting. On a phone, use the browser menu and choose **Add to Home screen** to get an app icon.

**Sign-in.** The page asks for the admin key, which is the value of `Admin__ApiKey` in Railway (at least 32 characters; make
it long and random, and keep it private). It is remembered on that device until you sign out.

**Today** (the default view)
- Every appointment of the day with its barber, customer and status. Use the arrows to move between days.
- **Done**: pick the services that were done (from the price list, or "Other service"), who actually did them, and adjust
  the price if there was a discount. This is what feeds the earnings. Use **+ Add service** on a finished appointment to
  add something forgotten, and the ✕ next to a line to remove a mistake.
- **No-show** and **Cancel** (the customer is told about a cancellation on their chat).
- An appointment can only be closed once it has started (or is within 30 minutes of starting), so a future slot is never freed by accident.

**The + button**
- **Phone booking**: name, phone, barber (or first available), date and a free time slot. It is saved as a normal confirmed
  booking, so the bot treats that slot as taken immediately and will not offer it to anyone else.
- **Walk-in / sale**: a customer who had no booking. Pick the barber and services. You can also choose an earlier date to
  catch up on entries from a paper book.

**Earnings**: choose Today, This week (Monday to today), This month, Last month or a custom range. You see revenue, customers
served, services done, the average per customer, no-shows and cancellations; revenue per barber (ranked, with each barber's
share), per service and per day; and a **CSV download** that opens in Excel or Google Sheets.

Revenue is counted from the services you record with **Done** or **Walk-in**, at the price you saved, and only for the barber
who did the work. Appointments that are never closed earn nothing, so close each one when the customer leaves.

## Prices, services and shop details the bot can answer

Customers can ask about prices, a specific service ("do you do facials", "how much is a kids cut"), opening hours,
where the shop is, contact details, walk-ins and how long an appointment takes, with or without saying hello first.
The bot answers directly and then shows the booking menu (or returns to the booking step they were on).

Everything it says comes from the `ShopInfo` section of `src/VintageFades.Web/appsettings.json`:

- `Address`, `Phone`, `Email`, `Instagram`, and `Hours` (leave empty to use the opening hours from the shop settings).
- `Catalog`: categories with items. Each item has a `Name`, a `Price`, optional `Details`, and `Keywords`, the words a
  customer might use for it. Specific keywords ("kids", "hollywood") count more than generic ones ("haircut", "cut").
- `Notes`: lines shown under the full price list.
- `Faqs`: keyword-matched answers (walk-ins, hygiene, hot towel).

To change a price or add a service, edit that file and push to `main`; Railway redeploys automatically. After a change,
run the tests: `ShopInfoResponderTests` checks the catalog and the matching.
