# Pairing Reset and Token Rotation Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans in the existing implementation worktree, with test-first changes and one independent final review.

**Goal:** Let the Windows user revoke every existing browser/Shortcut pairing and obtain a new persisted QR/token without losing Vault data or transfer receipts.

**Spec:** User request on 2026-10-17: add reset pairing and token rotation to the .NET 10 milestone.

**Architecture:** A server-owned PairingTokenStore supplies live credentials to authentication middleware. It generates cryptographic random keys, atomically persists a replacement before activating it, cancels/drains authenticated old-generation requests, clears browser-presence state, then enables the new key. The desktop pairing window provides a confirmed reset action and updates its displayed token/QR from the live store. Rotation has no HTTP endpoint.

**Tech Stack:** Existing .NET 10/WPF/ASP.NET Core, xUnit v2 and Playwright. No new package.

## Global Constraints

- Reset revokes all shared-token pairings, including old QR links and Shortcuts; periodic/device-specific rotation is outside this request.
- Keep files, Vault history, queue/acknowledgement state and safe-retry receipts intact.
- A failed token-file replacement must retain the previous active key and must not announce success.
- Reset is complete only after old authenticated handlers finish/cancel; already delivered/copied bytes cannot be recalled.
- New protected requests are rejected during the revocation/drain boundary. No old request remains after success is reported.
- Keep token values out of logs, errors and diagnostics; compare credentials with fixed-time hash comparison and reject multiple token header values.
- The PC action requires a product confirmation explaining disconnection and possible interruption of active transfers.
- Keep the existing trusted-LAN HTTP transport and shared-key model; do not add a network reset API.

## Review Focus

- Disk-write failure and restart recovery preserve a coherent token.
- In-flight requests cannot keep old access after reset reports success.
- Concurrent rotation/authorization/disposal does not deadlock or revive an old key.
- QR caching, clipboard controls and reopened windows show only current credentials.
- Safari clears old-pairing content on 401 and can re-pair without duplicate transfers.

## Task 1: Live credential ownership and revocation

**Files:** src/Beam.Server/Security/PairingToken.cs; new PairingTokenStore.cs; TokenAuthMiddleware.cs; BeamServerHost.cs; Connection/ConnectionPresence.cs; tests/Beam.Server.Tests/PairingRotationTests.cs; TokenAuthMiddlewareTests.cs.

**Interfaces:** PairingTokenStore.CurrentToken, Task<string> RotateAsync(), and an IDisposable authenticated request lease carrying a revocation CancellationToken. Existing BeamServerOptions.ExpectedToken is the initial seed; middleware uses the live store thereafter.

- [x] Add failing integration regressions for duplicate headers, old/new key authorization, persistence/restart, preserved receipts/data, failed file replacement, active-request cancellation/drain, and concurrent rotations.
- [x] Implement atomic credential persistence, serialized rotation and generation leases; register the store and use it in middleware.
- [x] Clear browser presence after old handlers drain and before new authorization resumes.
- [x] Run the backend suite; expected old-key rejection, new-key success and all existing transfer checks green.

## Task 2: Desktop control and browser recovery

**Files:** src/Beam.App/App.xaml.cs; PairingWindow.xaml/.cs; tests/Beam.App.Smoke/Program.cs; tests/Beam.Web.Host/Program.cs; tests/Beam.Web.Tests/integration.test.cjs; current security/setup/readme/verification docs.

**Interfaces:** Desktop obtains CurrentToken from the server store, confirms Reset pairing, awaits rotation off the dispatcher, regenerates QR/token/link and reports an actionable failure. A test-only stdin command rotates the isolated browser fixture; no production HTTP control is introduced.

- [x] Add a failing native smoke case for token/QR refresh, preserved history, duplicate-action prevention, failure state and reopening with the current key.
- [x] Implement the desktop control, busy state, confirmation and QR cache invalidation.
- [x] Add and run a real-server browser regression for revoked access, stale-content clearing and explicit re-pairing.
- [x] Run Release builds, backend, native smoke and both browser engines; inspect synthetic pairing screenshots.
- [x] Update current docs and record actual security boundaries, then request one independent read-only review.
- [x] Resolve material findings with regressions; verify and package a local preview.

## Execution Record

- Baseline: clean worktree at 673f275, .NET 10 SDK F:/beam-tooling/dotnet10; prior checkpoint passed 192 backend checks, 92 per browser engine, native/mDNS/package checks and CI.
- Scope decision: Reset pairing is PC-only and rotates the one shared key, so all existing browsers and optional Shortcuts must re-pair. The Vault and receipts remain reusable after authorization with the new key.

- Test-first evidence: raw repeated token headers returned 200 before rejection was implemented; missing live-store/reset controls failed backend/native checks before implementation. The captured-log regression exposed both QR secrets before the framework category filter.
- Final local verification: Release solution and browser host build with zero warnings/errors; 199 backend checks; 93 checks each in Chromium and WebKit; complete native smoke harness. The real browser reset also passes against the final logging-filter build. Synthetic success/failure screenshots were visually inspected at the final 800px window height.
- Independent read-only review: generation locking, persistence failure, request revocation/drain, disposal and stale desktop credentials accepted. One important QR query logging issue was fixed with a failing/passing regression; follow-up review reports no remaining findings.
- Local preview 0.1.0-alpha.2.preview.2: checksum bc7879f1a6288679e3a5ada8948ebed597fedf8f66e28aa9aa6cc3f0e9df212d; verified three .NET 10.0.13 runtimes, current browser/setup assets, no user-data files and no legacy vulnerable dependency chain.
- Integration gate: commit and push the verified changes to main, then require green GitHub build/backend and both browser jobs. Physical-iPhone acceptance remains a release gate.
- CI follow-up: Windows/backend and Chromium passed; WebKit reproducibly missed the native click on a low Vault row, before rotation or after re-pair. Temporary DOM-click/HTTP diagnostics showed no click event/content request. The reset test now searches its fixture to keep the target visible and explicitly foregrounds the original page after the old-QR check. No assertion, timeout or production behavior was weakened. The complete 11-case WebKit integration sequence passes twice.
