# Kindwell

Kindwell is a single-administrator community fundraising site built with ASP.NET Core 8, SQLite, and 1pay.cx hosted card checkout.

## Run locally

Install the .NET 8 SDK, then run:

```powershell
dotnet run --urls http://localhost:5080
```

The first run creates `App_Data/kindwell.db` and seeds three example campaign pages with no prior gifts or raised balance. Campaigns, donation records, and uploaded images persist locally across restarts. The example campaign stories are sample content; replace them before public launch.

## Current application scope

The application uses ASP.NET Core 8, SQLite, and a small browser-native JavaScript frontend. The public fundraiser list supports database-backed text search, category filters, sorting, and pagination; administrators can manage drafts, categories, images, publication, and campaign updates. Administrator payment history is filterable by status and date. Provider-confirmed gifts are recorded as balanced journal postings (processor clearing debit and campaign funds payable credit) in the private `/api/admin/ledger` API. Weekly, monthly, all-time, and custom-date reports are based on stored donation/payment data and ledger entries; daily aggregates use UTC payment confirmation dates and can be exported as CSV. These entries do not represent processor settlements or beneficiary payouts. Processor fees, refunds, reserves, and payouts are not yet modeled.

## Implementation checklist

- [x] SQLite database with startup schema migrations and persistent campaign, donation, and update records.
- [x] Server-enforced administrator access using a rate-limited login, role-protected HTTP-only cookie, and antiforgery tokens.
- [x] Administrator campaign create, edit, publish, unpublish, delete-protection, image upload, and update workflows.
- [x] Public fundraiser comments with private email addresses, rate-limited submissions, and paginated administrator moderation and deletion.
- [x] Payment history with status/date filtering and pagination; only provider-confirmed payments count as successful.
- [x] Immutable payment ledger entries with balanced debit/credit postings, confirmation timestamps, and idempotent payment confirmation.
- [x] Weekly, monthly, all-time, and custom UTC financial reports with daily totals and CSV export.
- [ ] Multi-user accounts, email verification, organizer workspaces, and account recovery.
- [ ] Refund processing, processor fee/reconciliation data, settlements, reserves, beneficiary verification, and payouts.
- [ ] Multi-currency and country eligibility, notifications, support tooling, and the full operational/legal requirements for live fundraising.

The pasted product brief describes a larger platform than the current implementation. User accounts and email verification, organizer workspaces, multi-currency support, country eligibility, beneficiary verification and payouts, refunds, notifications, and support tooling are not implemented. New donations use 1pay.cx's hosted card checkout. Kindwell does not collect card numbers, CVV, PINs, or one-time codes. Configure a branded checkout host and test credentials before accepting donations, and review the legal and operational requirements for live fundraising.

## Administrator access

Set credentials in the process environment before starting the app. Do not commit secrets or put them in client-side files.

```powershell
$env:ADMIN_EMAIL = "owner@example.com"
$env:ADMIN_PASSWORD = "use-a-long-unique-password"
dotnet run --urls http://localhost:5080
```

Open `/admin` to manage campaign drafts, publishing, images, goals, and donation records. Admin APIs require a role-protected, HTTP-only, same-site cookie and antiforgery token. Login is rate limited. For deployment, use HTTPS; the admin cookie is marked Secure outside Development.

The Comments tab lets the administrator review and delete comments. Comment text is public; the submitted email address is stored for administration and is not included in public responses. Comments can contain information entered by visitors, so visitors should not include private details. Comment records are deleted when their fundraiser is deleted.

## 1pay.cx hosted card checkout

Configure 1pay.cx's white-label checkout using its [white-label checkout guide](https://1pay.cx/docs/white-label). Finish its host/DNS and branding setup, then set the exact hostname in `ONEPAY_CHECKOUT_HOST`. The card checkout will use your configured brand and domain, though the donor will leave the Kindwell page URL while entering payment details and return to Kindwell afterward.

Set these server-side values (never commit keys):

```powershell
$env:ONEPAY_API_KEY = "pk_test_..."             # use the live key only after sandbox testing
$env:ONEPAY_WEBHOOK_SECRET = "whsec_test_..."   # matching mode's signing secret from GET /v1/me
$env:ONEPAY_CHECKOUT_HOST = "pay.example.com"   # exact branded hostname; no scheme
$env:PUBLIC_BASE_URL = "https://kindwell.example.com"
```

Kindwell creates a 1pay.cx payment with `method: "card"` and an idempotency key. Checkout links are accepted only over HTTPS from the configured branded host. The `/api/onepay/webhook` endpoint validates 1pay.cx's timestamped HMAC-SHA256 signature and re-fetches the payment through the authenticated API before confirming it. The donor's email is sent to 1pay.cx for the receipt. Test the complete flow with sandbox API and webhook credentials before switching to live mode.

## Legacy Paymentwings payments

The following legacy credentials/routes are only for previously created Paymentwings payments; new donation checkouts use 1pay.cx.

Create a Paymentwings account and copy your key pair from the dashboard (Settings > API). Configure the server (never commit keys):

```powershell
$env:PAYMENTWINGS_PUBLIC_KEY = "pub_..."   # sandbox or live public key
$env:PAYMENTWINGS_SECRET_KEY = "sec_..."   # matching private key
$env:PAYMENTWINGS_WEBHOOK_KEY = "..."      # webhook key, used only to verify webhooks
```

Requests authenticate as `Authorization: Bearer <public>:<private>`. A sandbox key pair automatically runs in test mode — no real funds move. The webhook URL is set per payment at checkout to `https://<your-public-host>/api/paymentwings/webhook`; use a tunnel such as ngrok locally. Webhooks are verified with `md5(txn|timestamp|webhook_key)`, then the payment is re-fetched from Paymentwings's lookup API and only recorded when the status, currency and exact amount match. Webhook payloads and redirect URLs are never trusted on their own. The donor email is sent to Paymentwings for the receipt.

Processing fees depend on the donor's country and payment method and are deducted by Paymentwings. Refund requests may be submitted within 30 days and are reviewed individually; provider fees may not be refundable. Review this policy and applicable fundraising, charitable solicitation, privacy, tax, and consumer-protection requirements with qualified counsel before accepting live donations.
## Configuration and operations

- `ADMIN_EMAIL`, `ADMIN_PASSWORD`: required for administrator login.
- `ONEPAY_API_KEY`, `ONEPAY_WEBHOOK_SECRET`: required for new 1pay.cx payments and signed webhook verification.
- `ONEPAY_CHECKOUT_HOST`: exact HTTPS checkout hostname configured in 1pay.cx; a response with any other host is rejected.
- `PUBLIC_BASE_URL`: canonical HTTPS Kindwell origin used for the 1pay.cx return and webhook URLs in production.
- New donations use card-only 1pay.cx hosted checkout. Kindwell never asks for or receives card numbers, expiry, CVV, PIN, or one-time codes. The donor's name and email are stored and shown in the admin Currency finance tab and CSV export.
- `DONATION_CURRENCIES` (default `USD,NGN,GBP,EUR`): currencies offered at checkout. A currency is only shown when an exchange rate is available and 1pay.cx supports it; only two-decimal currencies are supported.
- `EXCHANGE_RATE_API_URL` (default `https://open.er-api.com/v6/latest/USD`): rate source returning `{ "result": "success", "rates": { ... } }` with USD as base. No key is needed for the default; if you switch to a keyed provider put the URL (including its key) in this environment variable only.
- `RATE_CACHE_MINUTES` (default 60) and `RATE_MAX_STALE_HOURS` (default 24): rates are cached and stored in the database; if the provider is down, stored rates are used (flagged as stale) until they exceed the maximum age, after which non-USD checkout is paused.
- Test and live modes are detected from the 1pay.cx API key prefix. Each donation records its mode, and the Currency finance tab filters by mode.

## Multi-currency accounting

- Each donation keeps the original amount and currency, the exchange rate and the time it was recorded (when checkout started), and the resulting USD equivalent. Historical records are never recalculated with newer rates. Amounts are integer minor units; rates are stored as exact decimal text.
- The USD equivalent is an **estimate**. Kindwell does not convert or trade money. Charged amount, processor fee and settlement amount are stored only when the payment provider reports them.
- 1pay.cx card settlement and any currency conversion are configured in your 1pay.cx account. Reconcile using the settlement figures shown by the provider; Kindwell does not execute payouts.
- 1pay.cx webhooks use timestamped HMAC-SHA256 signatures; all new payments are re-verified through its authenticated API before being recorded. Legacy Paymentwings and Paylio records continue to use their existing verification paths.
- Refunds are not implemented, so the refunded count only reflects rows whose status is `refunded`.
- Donor email is sent to 1pay.cx for new payments; the app stores the donor's chosen display name.
- Admin endpoints: `GET /api/admin/finance`, `GET /api/admin/finance/payments`, `GET /api/admin/finance/export.csv`; public: `GET /api/rates`.
- `ASPNETCORE_ENVIRONMENT`: set to `Production` in production.
- Back up `App_Data/kindwell.db` and `wwwroot/uploads/` together.
- Deploy behind HTTPS with an accurately forwarded external scheme and host. Restrict access to the server filesystem and keep secrets in the host's secret manager.
- The included campaigns are illustrative sample records, not verified beneficiaries or live appeals.#   k i n d w e l l -  
 #   k i n d w e l l -  
 #   k i n d w e l l -  
 