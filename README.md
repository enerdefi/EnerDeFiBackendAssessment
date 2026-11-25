# EnerDeFi .NET Backend Assessment

Thanks for taking the time to do this. The full brief was sent to you separately. This README covers the repository and the simulated verification providers.

## What's in this repo

```
.
├── EnerDeFi.Assessment.sln
├── providers/
│   └── MockProviders/      # Simulated third-party verification providers. DO NOT MODIFY.
├── src/                    # YOUR CODE GOES HERE
└── tests/                  # YOUR TESTS GO HERE
```

- **`providers/MockProviders`** simulates three external company verification providers. Treat them as third-party services you don't control: call them over HTTP and don't change their code or settings.
- **`src/`** is empty. Create your own API project (or projects) here, structured however you think is best, and add them to the solution.
- **`tests/`** is empty. Put your test project(s) here.

## Getting started

You need the .NET 8 SDK or later.

1. Save the `mocksecrets.json` file attached to the assessment email into `providers/MockProviders/`. It holds the credentials the mock providers check against. It's git-ignored; don't commit it.
2. Run the providers:

   ```bash
   dotnet run --project providers/MockProviders
   ```

Each provider runs on its own port, as real providers run on their own hosts. Port 5100 is a test control panel, not a provider.

| Provider | Base URL | Style | Company types supported | Cost per check | Speed and reliability |
| --- | --- | --- | --- | --- | --- |
| QuickVerify | `http://localhost:5101` | Synchronous REST | All | ₦50 | ~150 ms, but about 30% of calls fail with 500, about 10% take 5 s, and it allows 5 requests per second |
| SureCheck | `http://localhost:5102` | Synchronous REST | Business names and limited companies only | ₦120 | Reliable, ~1.5 s per call |
| TrustLink | `http://localhost:5103` | Asynchronous, result by webhook | All | ₦30 | Accepts in ~200 ms (about 10% of submissions fail with 500); the result arrives 3–10 s later; about 10% of results are "issuer unavailable" |

Every provider looks up the same fake company register, so results are consistent:

| RC number | Result |
| --- | --- |
| Ends in **0** | Company not found |
| Ends in **9** | Company found, status INACTIVE |
| Anything else | Company found, status ACTIVE |

The credentials for your API were in the assessment email. They're the same values as in `mocksecrets.json`.

---

## QuickVerify

**Request**

```
GET /api/v1/kyc/cac/basic?rc_number=1234567&company_type=COMPANY
Authorization: <QuickVerify secret key>      (sent as-is, no "Bearer" prefix)
AppId: <QuickVerify app id>
```

- `rc_number`: digits only, without the `RC` prefix.
- `company_type`: `BUSINESS_NAME`, `COMPANY` or `INCORPORATED_TRUSTEES`.

**Response (200)**

```json
{
  "entity": {
    "company_name": "DELTA ENERGY LIMITED",
    "type_of_company": "COMPANY",
    "status": "ACTIVE",
    "rc_number": "1234567",
    "state": "Rivers",
    "date_of_registration": "2012-08-05T09:00:00.000Z"
  }
}
```

**Errors** are returned as `{ "error": "..." }`: 400 (bad parameters), 401 (bad credentials), 404 (company not found), 429 (rate limited; see the `Retry-After` header), 500, 503.

---

## SureCheck

**Request**

```
POST /v1/verifications/identities/cac
Authorization: Bearer <SureCheck API key>
Content-Type: application/json

{ "type": "limited_company", "rcNumber": "1234567" }
```

- `type`: `limited_company` or `business`. Incorporated trustees are **not supported** (400).
- `rcNumber`: digits only, without the `RC` prefix.

**Response (200)**

```json
{
  "status": "success",
  "data": {
    "rcNumber": "1234567",
    "companyName": "DELTA ENERGY LIMITED",
    "companyType": "PRIVATE_COMPANY_LIMITED_BY_SHARES",
    "registrationDate": "2012-08-05T09:00:00Z",
    "state": "Rivers",
    "status": "ACTIVE"
  }
}
```

**Errors** are returned as `{ "status": "error", "message": "..." }`: 400, 401, 404 (company not found), 500, 503.

---

## TrustLink (asynchronous, with webhooks)

TrustLink doesn't return the result straight away. You submit a job with a callback URL, and TrustLink POSTs the result to that URL a few seconds later.

### 1. Submit a job

```
POST /v1/async_business_verification
Content-Type: application/json

{
  "partner_id": "<TrustLink partner id>",
  "timestamp": "2026-11-25T10:00:00.000Z",
  "signature": "<request signature, see below>",
  "callback_url": "https://your-api/your-webhook-route",
  "country": "NG",
  "id_type": "BUSINESS_REGISTRATION",
  "id_number": "RC1234567",
  "business_type": "co",
  "partner_params": { "job_id": "<unique per request>", "user_id": "<your own reference>" }
}
```

- `id_number`: the RC number **with** the `RC` prefix.
- `business_type`: `bn` (business name), `co` (limited company) or `it` (incorporated trustees).
- `partner_params.job_id` must be unique. Reusing one returns **409**.
- `timestamp` must be within 5 minutes of the current time.

**Request signature:**

```
signature = Base64( HMAC-SHA256( key = TrustLink API key,
                                 message = timestamp + partner_id + "sid_request" ) )
```

**Response (202)**

```json
{ "success": true, "trustlink_job_id": "tl_3f9c1a2b7d4e5f60", "message": "Job accepted. The result will be sent to your callback_url." }
```

Errors are returned as `{ "success": false, "error": "..." }`: 400, 401, 409, 500 (the job was not created), 503.

### 2. Receive the result (webhook)

TrustLink sends a `POST` to your `callback_url`:

```
X-TrustLink-Event: business_verification.completed
X-TrustLink-Delivery: dlv_1a2b3c4d5e6f
X-TrustLink-Timestamp: 1795600000                  (Unix seconds)
X-TrustLink-Signature: sha256=<hex signature>
Content-Type: application/json

{
  "event": "business_verification.completed",
  "trustlink_job_id": "tl_3f9c1a2b7d4e5f60",
  "partner_params": { "job_id": "...", "user_id": "..." },
  "result_code": "1001",
  "result_text": "Business verified.",
  "actions": { "verify_business": "Verified" },
  "company_information": {
    "legal_name": "DELTA ENERGY LIMITED",
    "registration_number": "RC1234567",
    "company_type": "co",
    "status": "ACTIVE",
    "registration_date": "2012-08-05",
    "state": "Rivers"
  },
  "completed_at": "2026-11-25T10:00:05.123Z"
}
```

| result_code | verify_business | Meaning |
| --- | --- | --- |
| `1001` | `Verified` | Company found (check `company_information.status` for ACTIVE or INACTIVE) |
| `1002` | `Not Verified` | Company not found |
| `1003` | `Issuer Unavailable` | The registry couldn't be reached. The check is inconclusive and should be tried again |

**Webhook signature:**

```
expected = "sha256=" + hex( HMAC-SHA256( key = TrustLink webhook secret,
                                         message = X-TrustLink-Timestamp + "." + raw request body ) )
```

The webhook secret is **not** the same as the API key.

**Delivery behaviour (as with real providers):**

- Respond with a 2xx status within 5 seconds to acknowledge receipt.
- If your endpoint returns anything else or times out, TrustLink retries after 5, 15 and 30 seconds, then gives up.
- Delivery is **at least once**: the same event is sometimes delivered more than once.
- Your callback URL must be reachable from the mock, e.g. `http://localhost:<your port>/...`.

---

## Test control panel (port 5100)

Use this to check how your API reacts. It isn't part of any provider, and your API must not depend on it. Every request below is also in [`providers/MockProviders/control-panel.http`](providers/MockProviders/control-panel.http), which you can run from VS Code (REST Client extension), Visual Studio or Rider.

```bash
# Current mode and call count of each provider
curl http://localhost:5100/admin/status

# Change a provider's mode: Normal | Healthy | Slow | Down
curl -X PUT http://localhost:5100/admin/quickverify/mode/Down
curl -X PUT http://localhost:5100/admin/trustlink/mode/Healthy

# Recent TrustLink webhook deliveries and how your API responded
curl http://localhost:5100/admin/trustlink/webhooks

# Re-send a webhook exactly as before (same timestamp and signature)
curl -X POST http://localhost:5100/admin/trustlink/webhooks/<trustlink_job_id>/replay

# Send a webhook with an invalid signature
curl -X POST http://localhost:5100/admin/trustlink/webhooks/<trustlink_job_id>/forged
```

| Mode | Behaviour |
| --- | --- |
| `Normal` | Default. Random failures, slow responses, duplicate webhooks and "issuer unavailable" results as described above |
| `Healthy` | Never fails, never slow, no duplicates |
| `Slow` | Every response is slow; TrustLink webhooks take 30 seconds |
| `Down` | Every request returns 503 immediately |

Your automated tests shouldn't depend on this service running or on its random behaviour. Fake the HTTP calls in your tests so the results are the same every run.

## Submitting

1. Create your own **private** GitHub repository from this one and push your work to it.
2. Add the GitHub user you were given as a collaborator.
3. Reply to the assessment email with the repository link.

Make sure `dotnet build`, `dotnet test`, and running your API all work from a fresh clone (plus `mocksecrets.json` for the providers). Update this README, or add your own in `src/`, with how to run your API and the decisions you made.
