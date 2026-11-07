# Kindwell

Kindwell is a single-administrator community fundraising site built with ASP.NET Core 8, SQLite, and Paylio hosted checkout.

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

The pasted product brief describes a larger platform than the current implementation. User accounts and email verification, organizer workspaces, multi-currency support, country eligibility, beneficiary verification and payouts, refunds, notifications, and support tooling are not implemented. New donations use Paylio's hosted checkout. Kindwell does not collect card numbers, CVV, PINs, or one-time codes. Configure Paylio and verify the full checkout before accepting donations, and review the legal and operational requirements for live fundraising.

## Administrator access

Set credentials in the process environment before starting the app. Do not commit secrets or put them in client-side files.

```powershell
$env:ADMIN_EMAIL = "owner@example.com"
$env:ADMIN_PASSWORD = "use-a-long-unique-password"
dotnet run --urls http://localhost:5080
```

Open `/admin` to manage campaign drafts, publishing, images, goals, and donation records. Admin APIs require a role-protected, HTTP-only, same-site cookie and antiforgery token. Login is rate limited. For deployment, use HTTPS; the admin cookie is marked Secure outside Development.

The Comments tab lets the administrator review and delete comments. Comment text is public; the submitted email address is stored for administration and is not included in public responses. Comments can contain information entered by visitors, so visitors should not include private details. Comment records are deleted when their fundraiser is deleted.

## Paylio hosted checkout

New donations use Paylio's hosted checkout. Kindwell offers Card without a payment-method selector, but Paylio may show its own provider choices, including non-card options; the Paylio API does not document a card-only restriction for those choices. Paylio settles in USDC on Polygon, not directly into a bank account. Confirm the wallet and fees in Paylio before accepting donations.

Set these server-side values (never commit keys):

```powershell
$env:PAYLIO_API_KEY = "your-paylio-api-key"
$env:PAYLIO_WALLET_ADDRESS = "your-approved-polygon-wallet"
$env:PUBLIC_BASE_URL = "https://kindwell.example.com"
```

The public HTTPS URL must be reachable by Paylio even for local testing. Approve the payout wallet in Paylio. Kindwell stores each payment's webhook secret privately, verifies the signed callback, and re-fetches the status from Paylio before confirming a gift. Paylio says webhooks are delivered only once; missed callbacks need reconciliation by checking payment status. Paylio's published API documents live keys, so verify whether a test environment is available before running a transaction. Paylio receives the donor email.

## Legacy 1pay.cx payments

Keep `ONEPAY_API_KEY` and `ONEPAY_WEBHOOK_SECRET` configured while previously created 1pay.cx payments are still pending. Their webhook and authenticated verification paths remain available, but new donations no longer use 1pay.cx.

## Legacy Paymentwings payments

The following legacy credentials/routes are only for previously created Paymentwings payments; new donation checkouts use Paylio.

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
- `PAYLIO_API_KEY`, `PAYLIO_WALLET_ADDRESS`: Paylio API key and approved Polygon payout wallet required for new payments.
- `ONEPAY_API_KEY`, `ONEPAY_WEBHOOK_SECRET`: retained only for verifying previous 1pay.cx payments.
- `PUBLIC_BASE_URL`: publicly reachable HTTPS Kindwell origin used for Paylio return and webhook URLs.
- Kindwell shows Card and redirects to Paylio, which may display other provider options. Kindwell never asks for or receives card numbers, expiry, CVV, PIN, or one-time codes. The donor's name and email are stored and shown in the admin Currency finance tab and CSV export.
- `DONATION_CURRENCIES`: configured currencies are limited to Paylio's documented USD, EUR, CAD, INR and GBP; USD is always offered. Non-USD currencies require an available exchange rate. Paylio accepts a maximum of 10,000 in the selected currency, and provider minimums may vary.
- `EXCHANGE_RATE_API_URL` (default `https://open.er-api.com/v6/latest/USD`): rate source returning `{ "result": "success", "rates": { ... } }` with USD as base. No key is needed for the default; if you switch to a keyed provider put the URL (including its key) in this environment variable only.
- `RATE_CACHE_MINUTES` (default 60) and `RATE_MAX_STALE_HOURS` (default 24): rates are cached and stored in the database; if the provider is down, stored rates are used (flagged as stale) until they exceed the maximum age, after which non-USD checkout is paused.
- The Currency finance tab defaults to the current Paylio key's mode; select All to view historical payments from other providers and modes.

## Multi-currency accounting

- Each donation keeps the original amount and currency, the exchange rate and the time it was recorded (when checkout started), and the resulting USD equivalent. Historical records are never recalculated with newer rates. Amounts are integer minor units; rates are stored as exact decimal text.
- The USD equivalent is an **estimate**. Kindwell does not convert or trade money. Charged amount, processor fee and settlement amount are stored only when the payment provider reports them.
- Paylio settles to the configured Polygon wallet in USDC. Reconcile using the settlement figures shown by the provider; Kindwell does not execute payouts.
- Paylio webhooks use per-payment HMAC-SHA256 signatures; payments are re-verified through its authenticated API before being recorded. Legacy Paymentwings and 1pay.cx records continue to use their existing verification paths.
- Refunds are not implemented, so the refunded count only reflects rows whose status is `refunded`.
- Donor email is sent to Paylio for new payments; the app stores the donor's chosen display name.
- Admin endpoints: `GET /api/admin/finance`, `GET /api/admin/finance/payments`, `GET /api/admin/finance/export.csv`; public: `GET /api/rates`.
- `ASPNETCORE_ENVIRONMENT`: set to `Production` in production.
- Back up `App_Data/kindwell.db` and `wwwroot/uploads/` together.
- Deploy behind HTTPS with an accurately forwarded external scheme and host. Restrict access to the server filesystem and keep secrets in the host's secret manager.
- The included campaigns are illustrative sample records, not verified beneficiaries or live appeals.#   k i n d w e l l -  
 #   k i n d w e l l -  
 #   k i n d w e l l -  
 