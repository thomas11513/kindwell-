# Kindwell

Kindwell is a single-administrator community fundraising site built with ASP.NET Core 8, SQLite, and Paymentwings Checkout.

## Run locally

Install the .NET 8 SDK, then run:

```powershell
dotnet run --urls http://localhost:5080
```

The first run creates `App_Data/kindwell.db` and seeds three example campaign pages with no prior gifts or raised balance. Campaigns, donation records, and uploaded images persist locally across restarts. The example campaign stories are sample content; replace them before public launch.

## Current application scope

The application uses ASP.NET Core 8, SQLite, and a small browser-native JavaScript frontend. The public fundraiser list supports database-backed text search, category filters, sorting, and pagination; administrators can manage drafts, categories, images, publication, and campaign updates. Administrator payment history is filterable by status and date. Paymentwings-confirmed gifts are recorded as balanced journal postings (processor clearing debit and campaign funds payable credit) in the private `/api/admin/ledger` API. Weekly, monthly, all-time, and custom-date reports are based on stored donation/payment data and ledger entries; daily aggregates use UTC payment confirmation dates and can be exported as CSV. These entries do not represent processor settlements or beneficiary payouts. Processor fees, refunds, reserves, and payouts are not yet modeled.

## Implementation checklist

- [x] SQLite database with startup schema migrations and persistent campaign, donation, and update records.
- [x] Server-enforced administrator access using a rate-limited login, role-protected HTTP-only cookie, and antiforgery tokens.
- [x] Administrator campaign create, edit, publish, unpublish, delete-protection, image upload, and update workflows.
- [x] Payment history with status/date filtering and pagination; only Paymentwings-confirmed payments count as successful.
- [x] Immutable payment ledger entries with balanced debit/credit postings, confirmation timestamps, and idempotent Paymentwings webhook handling.
- [x] Weekly, monthly, all-time, and custom UTC financial reports with daily totals and CSV export.
- [ ] Multi-user accounts, email verification, organizer workspaces, and account recovery.
- [ ] Refund processing, processor fee/reconciliation data, settlements, reserves, beneficiary verification, and payouts.
- [ ] Multi-currency and country eligibility, notifications, support tooling, and the full operational/legal requirements for live fundraising.

The pasted product brief describes a larger platform than the current implementation. User accounts and email verification, organizer workspaces, multi-currency support, country eligibility, a full financial ledger, beneficiary verification and payouts, refunds, notifications, and support tooling are not implemented. Paymentwings Checkout is the only payment integration and is available only after the business and Paymentwings account are eligible and configured. Do not accept live donations until the remaining legal, operational, security, and payment requirements have been completed.

## Administrator access

Set credentials in the process environment before starting the app. Do not commit secrets or put them in client-side files.

```powershell
$env:ADMIN_EMAIL = "owner@example.com"
$env:ADMIN_PASSWORD = "use-a-long-unique-password"
dotnet run --urls http://localhost:5080
```

Open `/admin` to manage campaign drafts, publishing, images, goals, and donation records. Admin APIs require a role-protected, HTTP-only, same-site cookie and antiforgery token. Login is rate limited. For deployment, use HTTPS; the admin cookie is marked Secure outside Development.

## Paymentwings payments

Create a Paymentwings account and configure the API secret key and webhook secret hash on the server (never commit them):

```powershell
$env:PAYMENTWINGS_SECRET_KEY = "FLWSECK_TEST-..."
$env:PAYMENTWINGS_WEBHOOK_HASH = "your-secret-hash"
```

In the Paymentwings dashboard (Settings > Webhooks) set the URL to `https://<your-public-host>/api/paymentwings/webhook` and the secret hash to the same value as `PAYMENTWINGS_WEBHOOK_HASH`. Use a tunnel such as ngrok locally. A gift is counted only after the server verifies the transaction with Paymentwings's API (status, tx_ref, currency and exact amount); webhook payloads and redirect URLs are never trusted on their own. The donor email is sent to Paymentwings for the receipt. Use test keys until business verification is complete.

Processing fees depend on the donor's country and payment method and are deducted by Paymentwings. Refund requests may be submitted within 30 days and are reviewed individually; provider fees may not be refundable. Review this policy and applicable fundraising, charitable solicitation, privacy, tax, and consumer-protection requirements with qualified counsel before accepting live donations.

## Configuration and operations

- `ADMIN_EMAIL`, `ADMIN_PASSWORD`: required for administrator login.
- `PAYMENTWINGS_SECRET_KEY`, `PAYMENTWINGS_WEBHOOK_HASH`: required for checkout and confirmed donations.
- Kindwell's own card form (default in test mode): the donor enters card number, expiry and CVV on Kindwell's page, the server forwards them to Paymentwings's card charge API (3DES-encrypted with your encryption key, derived from the secret key or set as `PAYMENTWINGS_ENCRYPTION_KEY`), then Kindwell asks for the PIN and OTP and verifies the result with Paymentwings. Card data is never stored or logged. Live mode needs `CARD_FORM_PCI_CONFIRMED=true`, which you should set only after confirming PCI DSS compliance and that Paymentwings has enabled direct card charging for your account; otherwise live donors are sent to Paymentwings's hosted page.
- Kindwell never receives or stores card numbers, CVV or PINs. The donor's name and email are stored and shown in the admin Currency finance tab and CSV export.
- `PUBLIC_BASE_URL`: canonical HTTPS origin used for Paymentwings success and cancel redirects in production.
- `DONATION_CURRENCIES` (default `USD,NGN,GBP,EUR`): currencies offered at checkout. A currency is only shown when an exchange rate is available, and Paymentwings must also accept it for your account; if it rejects a currency the donation is marked failed and the donor is asked to try another. Only two-decimal currencies are supported.
- `EXCHANGE_RATE_API_URL` (default `https://open.er-api.com/v6/latest/USD`): rate source returning `{ "result": "success", "rates": { ... } }` with USD as base. No key is needed for the default; if you switch to a keyed provider put the URL (including its key) in this environment variable only.
- `RATE_CACHE_MINUTES` (default 60) and `RATE_MAX_STALE_HOURS` (default 24): rates are cached and stored in the database; if the provider is down, stored rates are used (flagged as stale) until they exceed the maximum age, after which non-USD checkout is paused.
- Test and live modes are detected from the key prefix (`FLWSECK_TEST-` = test). Each donation records its mode, and the Currency finance tab filters by mode.

## Multi-currency accounting

- Each donation keeps the original amount and currency, the exchange rate and the time it was recorded (when checkout started), and the resulting USD equivalent. Historical records are never recalculated with newer rates. Amounts are integer minor units; rates are stored as exact decimal text.
- The USD equivalent is an **estimate**. Kindwell does not convert or trade money. Charged amount, Paymentwings fee and settlement amount are stored only when Paymentwings's verification response provides them; the settlement currency is stored only if Paymentwings reports it.
- Paymentwings settlement currency and any currency conversion are account-level settings in your Paymentwings dashboard (they may need activation or approval from Paymentwings support). No automatic conversion is implemented; request the settlement currencies you need from Paymentwings, then use the reported settlement figures in the Currency finance tab to reconcile.
- Webhooks are accepted when `verif-hash` equals your secret hash or `flutterwave-signature` is a valid base64 HMAC-SHA256 of the body; every payment is still re-verified with Paymentwings's API (status, reference, currency and exact amount).
- Refunds are not implemented, so the refunded count only reflects rows whose status is `refunded`.
- Donor email is sent to Paymentwings only; the app stores the donor's chosen display name.
- Admin endpoints: `GET /api/admin/finance`, `GET /api/admin/finance/payments`, `GET /api/admin/finance/export.csv`; public: `GET /api/rates`.
- `ASPNETCORE_ENVIRONMENT`: set to `Production` in production.
- Back up `App_Data/kindwell.db` and `wwwroot/uploads/` together.
- Deploy behind HTTPS with an accurately forwarded external scheme and host. Restrict access to the server filesystem and keep secrets in the host's secret manager.
- The included campaigns are illustrative sample records, not verified beneficiaries or live appeals.#   k i n d w e l l -  
 #   k i n d w e l l -  
 #   k i n d w e l l -  
 