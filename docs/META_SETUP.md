# Meta Setup: WhatsApp, Instagram and Messenger

> Verified against Meta's developer documentation on **2 November 2026**. The current Graph API version is
> **v26.0** (released 29 July 2026; v25.0 is supported until 29 July 2028). Meta changes onboarding flows,
> permission names and policies often. Re-check each linked page before you act, and change `Meta__ApiVersion`
> only after testing.
>
> Nothing in this guide was carried out on the shop's real accounts. **No Meta account, number or setting has been
> changed.** Every step below that touches the live WhatsApp number needs the owner's explicit approval.

The service works with any subset of channels. A channel with empty credentials is disabled: its webhook
returns 404 and nothing is sent on it. Each channel can therefore go live when its approval comes through.

---

## 0. Prerequisites (owner)

| Item | Why |
|---|---|
| A **Meta Business Portfolio** (business.facebook.com) that owns the Facebook Page, the Instagram professional account and the WhatsApp Business Account | All assets and the app must sit in the same portfolio for system-user tokens |
| Owner (or developer) has **full control** of that portfolio | Needed to create system users, assign assets and verify the business |
| **Business verification** of the portfolio (Business Settings → Security Center) | Needed for higher WhatsApp limits, display-name approval, Advanced Access and Tech Provider status |
| A **Meta developer account** (developers.facebook.com) linked to a person with a role in the portfolio | Needed to create the app |
| The production HTTPS URL of this service, e.g. `https://bookings.example.com` | Webhook callback URLs |
| A long random **verify token** (`openssl rand -hex 32`) | `Meta__WebhookVerifyToken` |
| The owner's **personal** WhatsApp number for notifications | `WhatsApp__OwnerNotificationNumber`. It must **not** be the business number, because a number cannot message itself. |

## 1. Create the Meta app

1. developers.facebook.com → **My Apps → Create app**. Choose the **Business** app type (the dashboard may ask
   for a use case instead; pick the ones for WhatsApp, Messenger and Instagram messaging).
2. Connect the app to the shop's **Business Portfolio**.
3. **App settings → Basic**: note the **App ID** (`Meta__AppId`) and **App secret** (`Meta__AppSecret`). The app
   secret signs WhatsApp and Messenger webhooks. Store it only as a secret (see [SECURITY.md](SECURITY.md)).
4. Add a Privacy Policy URL and a data-deletion URL or instructions. App Review and Live mode require them.

## 2. WhatsApp Business (Cloud API)

### 2.1 What the shop wants, and what Meta allows

The shop wants to keep its **existing number and the WhatsApp Business app** and also use the Cloud API.
Meta calls this **"Onboarding WhatsApp Business app users" (Coexistence)**.

Source: <https://developers.facebook.com/documentation/business-messaging/whatsapp/embedded-signup/onboarding-business-app-users>

Meta's documented requirements (checked 2 Nov 2026):

| Requirement | Status for this shop |
|---|---|
| Business app **version 2.24.17 or newer** on the phone | **Owner to check** (WhatsApp Business → Settings → Help → App info) |
| The integrating party must be a **Solution Partner or Tech Provider** | **Not met.** A private app for one business is neither. See the options below. |
| Onboarding through **Embedded Signup** with session logging and `featureType: whatsapp_business_app_onboarding` (on by default in Embedded Signup v5). Embedded Signup v2/v3 were **deprecated on 15 Oct 2026**; use v4 or newer. | Not built in Phase 1 (it is a partner-side onboarding page) |
| Webhooks subscribed to `account_update`, `history`, `smb_app_state_sync`, `smb_message_echoes` | Phase 1 handles `messages`. The other fields are not processed (see "Effects" below) |
| Country/region eligibility | **Not verified.** Meta's page lists no excluded regions today, but third-party guides report that eligibility varies by region and changes over time. Confirm during onboarding. |

**Effects of Coexistence on the number** (from Meta's page):

- Throughput is fixed at **20 messages/second** (no problem for one shop).
- **Broadcast lists become read-only.** Disappearing messages, view-once and live location are turned off for 1:1
  chats. Group chats are not synced.
- **All linked companion devices are unlinked** (WhatsApp Web/desktop must be relinked).
- Contacts and up to 180 days of chat history can be synced, **once only, within 24 hours** of onboarding.
- The Deregister API cannot be used on a coexistence number. Disconnecting is done in the Business app
  (**Settings → Account → Business Platform → Disconnect Account**).
- Messages sent through the Cloud API are charged at Cloud API rates. Messages sent from the Business app stay free.
- Messages the owner types in the app arrive as `smb_message_echoes`. Phase 1 ignores them, so when the owner
  replies from the app the bot is **not** paused automatically. The owner should use the handover flow (customer
  asks for a person, or the bot offers one), then send `RESUME <id>` when finished.

**Eligibility verdict:** *Coexistence is supported by Meta in general, but this shop's eligibility has NOT been
verified, and the current setup (an app built for the shop's own use) does not meet the Tech Provider requirement.*
Following the brief, the build stopped here instead of attempting onboarding.

### 2.2 Options. The owner must choose; nothing irreversible happens without approval.

| Option | Keeps existing number + app? | Effort / cost | Risk |
|---|---|---|---|
| **A. Coexistence through a Meta Solution Partner (BSP) that supports it** | Yes | BSP fee; Embedded Signup run by the BSP. The integration must then use that BSP's API access, so it may need a small adapter change. | Low for the number, but adds a paid intermediary (the brief prefers direct Meta) |
| **B. Become a Tech Provider and build Embedded Signup** | Yes | Business verification, App Review, a hosted Embedded Signup page (not in Phase 1). Several weeks of Meta review. | Medium (review delays) |
| **C. New, separate number on the Cloud API** (recommended to start quickly) | Yes. The existing number and app are untouched. | Lowest. The new number only needs to receive one SMS or voice call. Share it on Instagram, Facebook and Google; set a Business-app greeting/away message that points to it. | Lowest. Nothing changes on the existing number. |
| **D. Move the existing number to Cloud API only** | **No.** The Business app stops working for that number. | Low | **Needs explicit written owner approval.** Hard to reverse; chat history on the phone is not carried over |

Until the owner chooses, the service runs with Instagram and Messenger, and WhatsApp can be connected later with
option A, B or C without code changes. Option A with YCloud is implemented (section 2.3).

### 2.3 Chosen setup: Coexistence through YCloud (option A)

The owner chose option A with **YCloud** (a Meta Solution Partner; docs: <https://docs.ycloud.com>). The code supports
it through `WhatsApp__Provider=YCloud`. The bot then sends with YCloud's API (`POST https://api.ycloud.com/v2/whatsapp/messages/sendDirectly`,
header `X-API-Key`) and receives YCloud's signed webhooks on `/webhooks/ycloud`. Instagram and Messenger still connect
directly to Meta as described in sections 3 and 4.

**Ask YCloud before signing up (not yet confirmed):**
1. Is Coexistence available for a **Namibian (+264)** WhatsApp Business app number?
2. Does the free plan include a Coexistence number with API access and webhooks, or which plan is needed?
3. How do I top up the wallet that pays Meta's template fees (YCloud adds no markup)?

**Steps:**
1. On the phone: update WhatsApp Business to **2.24.17 or newer** (Settings → Help → App info) and back up chats.
   Note which computers use WhatsApp Web; they are logged out once.
2. Create a YCloud account. In the console go to **Create channels → WhatsApp Business APP Coexistence** and follow the
   Meta Embedded Signup: log in with Facebook, choose or create the shop's Business Portfolio, choose
   **connect existing WhatsApp Business app**, enter the shop number, and scan the QR code in the app. Agree to share
   chat history. **Never delete or deregister the number.** Keep the app open until the sync finishes.
3. Start **business verification** of the portfolio in Meta Business Settings if Meta asks for it (it lifts limits).
4. YCloud console → **Developers → API keys**: create a key and store it as `YCloud__ApiKey`.
5. Create the webhook endpoint (console, or API) with URL `https://<host>/webhooks/ycloud` and the events
   **`whatsapp.inbound_message.received`**, **`whatsapp.message.updated`** and **`whatsapp.smb.message.echoes`**:
   ```bash
   curl -X POST https://api.ycloud.com/v2/webhookEndpoints \
     -H "X-API-Key: <api key>" -H "Content-Type: application/json" \
     -d '{"url":"https://<host>/webhooks/ycloud","enabledEvents":["whatsapp.inbound_message.received","whatsapp.message.updated","whatsapp.smb.message.echoes"]}'
   ```
   Store the `secret` from the response as `YCloud__WebhookSecret`. The service checks every webhook's
   `YCloud-Signature` (HMAC-SHA256 over `timestamp.body`, at most 5 minutes old) and rejects anything else with 401.
6. Set `YCloud__FromNumber` to the shop number (`+264...`) and `WhatsApp__OwnerNotificationNumber` to the owner's
   **personal** number. Leave `WhatsApp__PhoneNumberId` and `WhatsApp__AccessToken` empty.
7. Create the two templates (section 2.5) in the YCloud console and put their names in
   `WhatsApp__ReminderTemplate__Name` and `WhatsApp__OwnerNotificationTemplate__Name`.
8. Restart the service and message the shop number "hi" from another phone. The bot answers, and the chat (bot replies
   included) is visible in the WhatsApp Business app.

**When the owner replies in the app:** YCloud reports it (`whatsapp.smb.message.echoes`). The bot stops replying in
that chat, releases any slot it was holding, and sends the owner "BOT PAUSED". It resumes after
`Chatbot__ManualReplyPauseMinutes` (default 10) or as soon as the owner sends `RESUME <id>`.

**Notes:** coexistence numbers are limited to 20 messages per second. YCloud notes Coexistence can be less stable
than a dedicated Cloud API number (sync delays, occasional disconnects). If the shop later moves to a direct Meta setup,
switch `WhatsApp__Provider=Meta` and follow section 2.4. No booking data changes.

### 2.4 Direct Cloud API setup (options B/C/D, or a test number)

1. App Dashboard → **Add product → WhatsApp**. Meta creates a test WhatsApp Business Account and a test number, which is
   useful for development: add up to 5 recipient numbers.
2. **WhatsApp → API Setup**: note the **Phone number ID** (`WhatsApp__PhoneNumberId`) and the **WhatsApp Business
   Account ID** (`WhatsApp__BusinessAccountId`).
3. **Permanent token:** Business Settings → **Users → System users** → *Add* (Admin) → **Assign assets**: the app
   (full control) and the WhatsApp Business Account (full control) → **Generate new token** for the app with the
   permissions **`whatsapp_business_messaging`** and **`whatsapp_business_management`**, expiry **Never**. Store it
   as `WhatsApp__AccessToken`. Temporary tokens from the API Setup page expire after about 24 hours. Do not use them in production.
4. **Webhook:** WhatsApp → **Configuration → Webhook → Edit**
   - Callback URL: `https://<host>/webhooks/whatsapp`
   - Verify token: the value of `Meta__WebhookVerifyToken`
   - Save. Meta calls `GET /webhooks/whatsapp?hub.mode=subscribe&...`, and the service echoes `hub.challenge`.
   - **Manage → subscribe to `messages`** (this delivers incoming messages and delivery statuses).
5. Add a production number (option C) under **WhatsApp → API Setup → Add phone number**, verify it by SMS or voice,
   set the display name ("VINTAGE FADES BARBER SHOP") and wait for display-name approval.
6. Add a payment method to the WhatsApp Business Account (Business Settings → WhatsApp accounts → Payment
   settings). Template messages are charged.

### 2.5 Message templates (needed for messages outside the 24-hour window)

Free-form replies are only allowed within **24 hours of the customer's last message** (the customer service
window). The service enforces this: business-initiated messages outside the window use an approved template or are
recorded as `Skipped`. Create these in **WhatsApp Manager → Message templates** (or the YCloud console when using YCloud), category **Utility**, language
English (`en`):

| Config key | Suggested name | Body (variables in order) |
|---|---|---|
| `WhatsApp__ReminderTemplate__Name` | `appointment_reminder` | `Hi {{1}}, this is a reminder of your appointment at VINTAGE FADES BARBER SHOP on {{2}} at {{3}} with {{4}}. We look forward to seeing you!` |
| `WhatsApp__OwnerNotificationTemplate__Name` | `shop_owner_update` | `VINTAGE FADES update: {{1}}` |

The service sends reminder parameters in the order *name, date, time, barber*. Owner notifications send one
parameter: the notification text on one line (line breaks become ` | `, as Meta requires). Meta decides the final
category. If a template is rejected or recategorised, adjust the wording and resubmit. Leave a name empty to
disable that template. The service then sends free-form text and records a failure if the window is closed.

**Owner notifications without a template** only reach the owner if the owner messaged the business number within
the last 24 hours. Configure the owner template before going live.

## 3. Facebook Messenger

Source: <https://developers.facebook.com/docs/messenger-platform>

1. App Dashboard → **Add product → Messenger** (or the Messenger use case).
2. **Messenger → Settings → Access tokens**: connect the shop's Facebook Page. Generate a Page access token, or
   better, generate a non-expiring token from the same **system user** (assign the Page to the system user, then
   generate a token with **`pages_messaging`**, **`pages_manage_metadata`**, **`pages_show_list`**,
   **`pages_read_engagement`**). Store it as `Facebook__PageAccessToken` and the Page ID as `Facebook__PageId`.
3. **Webhooks:** Callback URL `https://<host>/webhooks/messenger`, verify token `Meta__WebhookVerifyToken`.
   Subscribe the Page to the fields **`messages`** and **`messaging_postbacks`**. (The dashboard does this per Page.
   Through the API: `POST /v26.0/{page-id}/subscribed_apps?subscribed_fields=messages,messaging_postbacks` with the Page token.)
4. **App Review:** until `pages_messaging` has **Advanced Access**, the app can only talk to people who have a role
   on the app. To message the public, submit `pages_messaging` (plus `pages_manage_metadata` if requested) for App
   Review with a screencast of a booking conversation. Business verification is required.
5. Switch the app to **Live** mode once approved.

Messaging rules the service follows: replies are sent with `messaging_type: RESPONSE` inside the 24-hour standard
window. **Message tags such as `CONFIRMED_EVENT_UPDATE` were deprecated by Meta (requests that use them fail
from 27 April 2026).** For that reason Messenger reminders are only sent when the customer wrote in the last 24 hours.
Otherwise they are recorded as `Skipped`. Messenger utility templates exist but are not used in Phase 1.

## 4. Instagram Direct Messages (Instagram API with Instagram Login)

Source: <https://developers.facebook.com/documentation/instagram-platform/instagram-api-with-instagram-login/messaging-api>

1. The shop's Instagram account must be a **professional account** (Business or Creator). Instagram app →
   Settings → Account type.
2. App Dashboard → **Add product → Instagram → API setup with Instagram login**.
3. **Generate a token**: add the Instagram account and log in. Required permissions:
   **`instagram_business_basic`** and **`instagram_business_manage_messages`**. Store:
   - the Instagram professional account id (IG_ID) as `Instagram__AccountId`
   - the access token as `Instagram__AccessToken`
   - the **Instagram app secret** shown on the same page as `Instagram__AppSecret` (webhooks for Instagram Login
     apps are signed with it; if left empty, `Meta__AppSecret` is used)
4. **Webhooks:** Callback URL `https://<host>/webhooks/instagram`, verify token `Meta__WebhookVerifyToken`.
   Subscribe to **`messages`** and **`messaging_postbacks`**. The account itself must also be subscribed:
   `POST https://graph.instagram.com/v26.0/me/subscribed_apps?subscribed_fields=messages` with the account's token.
5. In the Instagram app, make sure connected tools may access messages (Settings → Messages and story replies →
   Message controls → Connected tools → **Allow access to messages**), if the option is shown.
6. **Access level:** Meta's page states that *Standard Access* is enough when the app serves accounts you own or
   manage (or added in the App Dashboard), but "some features may not work until Advanced Access is granted".
   Plan for **App Review** of `instagram_business_manage_messages` so that DMs from the public arrive in Live mode.
   **Not verified for this account.**
7. **Token lifetime:** Instagram user tokens are long-lived (about 60 days) and must be **refreshed before they
   expire**:
   ```bash
   curl -s "https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token=<current-token>"
   ```
   Put the new token in `Instagram__AccessToken` and restart the app. Set a calendar reminder every 45 days. If
   the token expires, sends fail with error 190 and the owner gets a **SYSTEM ALERT** on WhatsApp. Automatic
   refresh is not in Phase 1.

Rules the service follows: replies only within the 24-hour window after the customer's message. Text is at most
1000 bytes per message (longer replies are split). Group messaging is not supported. Instagram has no template
option, so Instagram reminders outside the window are `Skipped`. The `HUMAN_AGENT` tag (7-day window for human
replies) is **not** used by automated messages.

## 5. Webhook summary

| Product | Callback URL | Fields | Signature secret |
|---|---|---|---|
| WhatsApp (direct Meta) | `https://<host>/webhooks/whatsapp` | `messages` | `Meta__AppSecret` |
| WhatsApp (YCloud) | `https://<host>/webhooks/ycloud` (set in YCloud, not Meta) | `whatsapp.inbound_message.received`, `whatsapp.message.updated`, `whatsapp.smb.message.echoes` | `YCloud__WebhookSecret` |
| Messenger | `https://<host>/webhooks/messenger` | `messages`, `messaging_postbacks` | `Meta__AppSecret` |
| Instagram | `https://<host>/webhooks/instagram` | `messages`, `messaging_postbacks` | `Instagram__AppSecret` (fallback `Meta__AppSecret`) |

All three use the same verify token (`Meta__WebhookVerifyToken`). Every POST must carry a valid
`X-Hub-Signature-256` (HMAC-SHA256 of the raw body). Anything else is rejected with 401 and not stored.

## 6. Permissions summary

| Channel | Permissions | App Review / access | Token type |
|---|---|---|---|
| WhatsApp | `whatsapp_business_messaging`, `whatsapp_business_management` | Advanced Access is needed to serve businesses outside your own portfolio. Own-portfolio use: confirm in App Review → Permissions. Business verification for production limits. Tech Provider status only for Coexistence through your own Embedded Signup. | System-user token, never expires |
| Messenger | `pages_messaging`, `pages_manage_metadata`, `pages_show_list`, `pages_read_engagement` | `pages_messaging` Advanced Access (App Review) to message the public | System-user Page token, never expires |
| Instagram | `instagram_business_basic`, `instagram_business_manage_messages` | Standard Access for accounts you manage; Advanced Access (App Review) likely needed for public DMs. Verify. | Instagram long-lived token (~60 days, refresh) |

## 7. Production access checklist

- [ ] Business Portfolio verified
- [ ] App has a privacy policy URL and data-deletion instructions; app is in **Live** mode
- [ ] Permissions above granted at the required access level
- [ ] Non-expiring system-user tokens for WhatsApp and Messenger; Instagram refresh reminder set
- [ ] Webhooks verified (green tick) and fields subscribed for each product
- [ ] WhatsApp: number registered, display name approved, payment method added, templates approved
- [ ] `GET /health/ready` is `Healthy`; startup log shows each configured integration as `enabled`

## 8. Testing each platform

1. **Webhook handshake:** save the callback URL in the dashboard and check for the green tick. Locally:
   `curl "https://<host>/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=<token>&hub.challenge=42"` → `42`.
2. **WhatsApp:** with the test number, add your phone as a recipient, message the test number "hi" → menu. Book a
   slot and check that the owner's phone gets "NEW BOOKING".
3. **Messenger:** while the app is in development, use an account with a role on the app and message the Page.
4. **Instagram:** from another Instagram account (one with a role on the app while in development), DM the professional account.
5. **Cross-channel:** book a slot on one channel and ask for the same slot on another. It must be unavailable.
6. Check `GET /api/admin/status` (header `X-Admin-Key`) for pending or failed messages.
