# Architecture and HTTP API

```text
iPhone Safari / optional Share Sheet Shortcuts
        | authenticated HTTP POST
        v
Windows WPF app -- embedded ASP.NET Core server -- Inbox + history.json
        |
        +-- Transfer workspace / Vault / compact pill / tray
        |
        +-- Outbox queue <-- phone browser polls, downloads, acknowledges
```

| Path | Purpose |
| --- | --- |
| `src/Beam.App/` | WPF desktop host, tray lifecycle, pill, vault, previews, and QR pairing |
| `src/Beam.Server/` | Embedded ASP.NET Core endpoints, token middleware, storage, and queue |
| `src/Beam.Server/wwwroot/` | Phone browser inbox; no frontend build step |
| `tests/Beam.Server.Tests/` | Backend unit/integration tests |
| `tests/Beam.App.Smoke/` | Windows desktop smoke harness with isolated data and a random port |
| `tests/Beam.Web.Tests/` | Chromium/WebKit browser regressions and real-server integration tests |
| `tests/Beam.Web.Host/` | Isolated real server fixture for browser integration tests |
| `docs/setup/` | Current setup, desktop behavior, and verification guides |
| `docs/superpowers/` | Historical design/implementation notes; some describe earlier behavior |

### HTTP API

All data endpoints require `X-Beam-Token`. The root and browser static assets
are public so a phone can load the pairing UI without already knowing the token.

| Method | Route | Behavior |
| --- | --- | --- |
| GET | `/` | Redirect to `/app/`; does not disclose the PC token |
| GET | `/app/` | Phone inbox and pairing UI |
| POST | `/receive` | Save multipart files/`text`, or a legacy direct file body; optional `X-Beam-Transfer-Id` enables single-item idempotent Safari uploads |
| GET | `/receive/receipts/{transferId}` | Retrieve a committed one-ID receipt (200), pending attempt (202), or unknown ID (404); never resaves or acknowledges an item |
| GET | `/capabilities` | Authenticated file/note limits and `safeRetryVersion: 1` |
| POST | `/connection/heartbeat` | Authenticated foreground-browser activity; bounded transient session ID, no acknowledgement or persisted device record |
| GET | `/outbox` | List unread outbound items |
| GET | `/outbox/{id}/content` | Stream stored file bytes |
| POST | `/outbox/{id}/ack` | Persist acknowledgement |
| GET | `/vault?search=&direction=inbound&kind=image&offset=0&limit=40` | Search retained history in both directions; default 40, maximum 100 records |
| GET | `/vault/{id}/content` | Repeat download without changing read state |
| GET | `/vault/{id}/preview` | Safe raster preview without changing read state |

Successful uploads return `{"ids":["<item-id>"]}`. An invalid token returns
`401`; an empty form or empty direct upload returns `400`. A filename extension
is inferred for common direct-upload media types; unknown types use `.bin`.
The outbox is polled approximately every four seconds; desktop history refreshes
approximately every two seconds.

### Safe Retry Contract

Current Safari jobs use one random `X-Beam-Transfer-Id` for their lifetime.
The PC stores its payload fingerprint with the received history item before
confirming success. Replaying the same ID/body returns the original item ID;
reusing it for different bytes/name/media type/text returns `409` without saving.
Receipt checks reveal only item IDs, never filenames, paths or note contents.
They are authenticated and non-cacheable. Coordinator capacity failures return
`429`; storage failures return `503` without claiming a committed receipt.

This protects retries on the same in-page job, including uncertain cancellations,
and keeps receipts across clean PC restarts. It does not deduplicate intentional
new sends, persist the phone queue, resume partial bytes, protect old headerless
clients, or guarantee atomic file/index recovery after sudden power/disk loss.
