# Safe Upload Retries Implementation Plan

> **For agentic workers:** Use test-driven-development and verification-before-completion. Parent implements server/storage/integration; a disjoint browser worker implements the client. Preserve all earlier uncommitted milestones.

**Goal:** Retrying a Safari upload whose response was lost must reuse its saved PC record, not create a second file or note.

**Architecture:** Each in-page upload job gets one random transfer ID. The PC stores the ID and a payload fingerprint in the same atomic history update as its received item. Retry checks a receipt first; repeated/concurrent POSTs with the same ID serialize and return the original ID. Intentional new sends use new IDs.

**Tech Stack:** Existing .NET 8/ASP.NET Core, history.json, WPF and vanilla Safari JavaScript. No database, cloud, HTTPS setup or new browser dependency.

**Spec:** The user's approved safer-retries milestone and the bounded contracts below; implement directly as requested.

## Constraints and Honest Limits

- Protection is for keyed Safari uploads and notes. Existing headerless Shortcut/direct-body requests keep their old behavior and do not acquire a duplicate-prevention guarantee.
- A keyed request contains exactly one multipart file OR one nonempty text field; mixed/batch keyed bodies are rejected before writing. Safari already sends one item per POST.
- Keys identify user intent, not filenames or content globally. Identical files sent as new jobs remain separate, including identical filenames in a batch.
- Same key with different bytes/name/media type/text returns 409 without overwriting or creating another item.
- Durable receipts survive a clean PC/server restart with the existing history. No exactly-once claim for disk loss, external history deletion or abrupt power loss between file and index writes.
- Failed persistence must not leave an in-memory false-success receipt or a newly created file. Cancelled/failed streams are cleaned by ItemStore; only files created by the current request may be cleaned.
- Keep file/note limits, plain HTTP support, stale-pairing protection, connection UI, Vault, native motion and tray/quit semantics.
- Keep queues in the current browser page. Refresh/clearing jobs/reselecting a file creates a new intent; do not promise durable browser queues or resumable chunks.
- No automatic replay of failed jobs after reconnection. Retry is explicit and receipts may be checked without sending bytes.
- Never publish, push, reset user files or rotate the token in this milestone.

## Shared Protocol

- `GET /capabilities` adds `safeRetryVersion: 1` to its existing two numeric limits. New client sending is disabled if this capability is missing/unsupported, rather than silently using unsafe retries against an old PC build.
- `POST /receive` adds optional header `X-Beam-Transfer-Id`, exactly 32 hexadecimal characters (Guid N). Malformed, multiple or empty supplied values return 400, never fall back to unkeyed behavior.
- Success remains `{ "ids": ["<original-history-id>"] }` with HTTP 200, including matching replays. Different payload on an existing key returns 409. Busy bounded coordinator returns 429 without saving.
- `GET /receive/receipts/{transferId}` is authenticated: 200 with the same one-ID JSON for a committed receipt, 202 while an attempt for that key is pending, 404 if no committed/pending attempt is known, 400 for a malformed key, 401 for invalid auth. Set `Cache-Control: no-store`.
- Receipt replies contain no token, file path, filename, note contents or fingerprint. They never acknowledge outbound downloads or mutate history.
- `HistoryItem.TransferId` and `HistoryItem.TransferFingerprint` are optional persisted fields, absent/null for old records. Safe lookup only matches inbound records. History Add must roll back its in-memory mutation if Save fails.
- Server uses SHA-256 over an unambiguous versioned representation of original name/media type/length/content hash, or exact UTF-8 note. Hash file streams off the UI thread without loading a 100 MiB file into another giant buffer.
- Server serializes keyed requests through a bounded, ref-counted per-key coordinator (32 distinct active keys, at most 4 queued/in-flight attempts per key); leases are removed/disposed when their last holder/waiter exits, including cancellation.
- Browser job `transferId` is generated once with `crypto.getRandomValues` (works on current plain HTTP). Retries and uncertain cancellations reuse it; new jobs get new IDs.
- Retry first checks receipt. 200 confirms the original saved item without another POST; 404 permits uploading the same job/key; 202 asks the user to check again; errors/malformed success never silently trigger another upload. 409 is a non-retryable conflict for that job.
- Guard all receipt checks and XHR callbacks by job/attempt identity and pairing token/version; an old request cannot settle or reset a newly paired attempt. Checking is cancellable and cannot be cleared out from under an active request.

## Review Focus

1. Response lost after commit, first request still running and simultaneous retries: one history item/file only.
2. Disk/index write failure or cancellation: never claim success from an uncommitted memory record; cleanup only the new file.
3. Old/malformed receipt response or old token: never falsely mark a new job sent or submit it against an unsupported server.
4. Intentional same-file/name sends, notes and batches: do not over-deduplicate distinct intents or retransmit confirmed siblings.
5. Upgrade/restart/legacy history and old Shortcuts: migrate optional metadata without breaking retained history or old uploads.

## Task 1: Durable Server Receipts (Parent)

Files: new focused safe-receive endpoint/coordinator, ReceiveEndpoints, HistoryIndex, HistoryItem, CapabilitiesEndpoints, BeamServerHost and backend regression tests.

- [x] Write and run failing tests for same-key replay, changed payload, receipt auth/status/privacy, concurrent requests, restart, malformed keys, capacity/cancellation and index-failure rollback.
- [x] Implement optional metadata, atomic in-memory rollback, bounded keyed coordinator and single-item keyed receive path; preserve the legacy route.
- [x] Run the complete backend suite and verify legacy names/bytes/limits/auth remain correct.

## Task 2: Safari Safe Retry Flow (Browser Worker)

Files: app.js, index.html cache v8, optional styles/motion changes, browser fixture capability fields, new safe-retry browser tests.

- [x] Write/run failing tests before implementation for lost response, same-ID resend, receipt-confirmed success, 202/404/401/409/error handling, cancellation, double retry, fresh jobs and stale-pairing callbacks.
- [x] Add stable per-job IDs, capability gating, explicit checking state and receipt-first retry without automatic upload replay.
- [x] Preserve original input bytes until confirmed, retain confirmed siblings, prevent overlapping retry checks and render truthful statuses/animations.
- [x] Run complete mock suites in Chromium and WebKit; inspect 320/390px states.

## Task 3: Integration, Review and Local Build (Parent)

- [x] Add real-server browser tests that save the first request but drop its response, then retry: verify one physical file/history record, original bytes and no extra POST after known receipt. Cover missing receipt/full retry, intentional new send and notes; backend tests cover clean restart.
- [x] Update quickstart/README/verification with the exact safe-retry boundary and legacy/browser-reload limits.
- [x] Run Release build, full backend, Chromium/WebKit and Windows smoke checks. Obtain independent read-only review and fix important findings with RED/GREEN tests.
- [x] Package a local alpha.4, verify assets, gracefully replace the running alpha.3 only after checks pass, and verify existing token/history untouched by the update itself.

## Execution Record

- Baseline: 171/171 backend tests pass. Working in the existing linked worktree on `worktree-beam-implementation`; preserve all previous pending changes. Parent retains the .NET build lane; worker runs browser mocks only until the fixture is rebuilt.
- Ruling: This is per-transfer idempotency, not global content deduplication, automatic resends, durable phone queues or an OS-level guarantee about saving a download twice.
- Task 1 RED: missing Transfers/ReceiveCoordinator prevented the new contract tests from compiling. GREEN: full backend 192/192, including actual HTTP-body cancellation, pending/unknown receipts, identical concurrent requests, mismatch 409, clean restart and legacy requests.
- History rollback regression independently reproduced RED with the previous Add implementation (failed Save left an item in memory), then passed GREEN with rollback restored. Test accepts Windows UnauthorizedAccessException or Unix IOException for an intentionally blocked history destination.
- Integration RED: previous fixture saved two different item IDs for the same transfer header. Rebuilt real fixture passes the same boundary-independent replay test with one item/file.
- Browser worker: complete mock suites 81/81 in Chromium and 81/81 in WebKit. Inspected checking, pending and receipt-confirmed states at 320/390px; confirmation does not replay an upload animation.
- Independent read-only review found one P2 race: capability rejection during a pending receipt check could let its eventual 404 trigger a POST anyway. Two regressions (missing and unsupported version) failed before the fix, then passed after POST entry rechecked readiness; bytes/key remain retained and recovery needs explicit Retry. No other scoped concrete defect was reported.
- Final Release build: zero warnings/errors; backend 192/192; native smoke PASS including real tray/server shutdown, Vault, motion and exact 164 x 48 pill. Initial parallel Chromium runs exposed existing download-event and running-clock timing failures in different tests. The download case passed in isolation; final complete suites passed 91/91 in Chromium and 91/91 in WebKit with serialized browser processes. Every assertion/test remains enabled; production download behavior is unchanged.
- Fixture correction: WebKit route.fetch forwarded multipart file metadata without the selected file bytes, so fault injection now uses a real local streaming proxy and drops only the committed response. Chromium may transparently replay socket-reset POSTs; every such response must have the same receipt, one file/history record must remain, and explicit Retry must add no POST. Both engine cases pass with exact original bytes. WebKit's existing reduced-motion assertion now waits for the media setting/change handler to settle before requiring zero running animations; its immediate assertion failed before this synchronization and passes after it.
- Local alpha.4 package built under artifacts/safe-retries/ready; all eight browser assets and START-HERE match source hashes. ZIP SHA-256: e24955c041d4f2d7fb0acb23ed2aa18c1512a7e78b14718855fa9ae07248dbc9. Alpha.3 closed gracefully; alpha.4 is running and responding. Live checks: /app/ 200 with v8 assets, safeRetryVersion 1, authenticated unknown receipt 404/no-store, Vault total 20. Existing token/history hashes and all 20 records are unchanged. No GitHub publishing, push, token rotation or firewall change. Physical-iPhone interruption testing remains the user's acceptance check.
