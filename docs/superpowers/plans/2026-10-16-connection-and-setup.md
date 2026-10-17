# Connection and Setup Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Track verified work below.

**Goal:** A new user can open Beam, pair Safari and understand whether the server is ready and a remote browser has actually reached it.

**Architecture:** Keep WPF, the embedded ASP.NET Core server and the existing plain-HTTP Safari client. A dedicated authenticated heartbeat provides transient browser activity, separate from transfer acknowledgements. WPF owns first-run setup, adapter selection and local checks; Safari owns retry and foreground reconnection.

**Tech Stack:** .NET 8, WPF, ASP.NET Core, vanilla JavaScript, QRCoder, bundled Lucide, xUnit, Playwright and the existing native smoke harness.

**Spec:** The approved connection/setup milestone in chat plus the bounded design below. The user requested immediate implementation; no additional design approval round or publication is required.

## Scope and Constraints

- First launch offers pairing automatically once; later launches retain the normal workspace. Pair phone remains available from the workspace, pill and tray.
- The pairing screen shows a QR and manual address for a selected active LAN IPv4 adapter, with refresh and copy controls. No usable adapter means no misleading QR or invented hostname.
- Checks distinguish local HTTP reachability, selected adapter availability and remote browser activity. They cannot determine a remote router/firewall's exact policy and must say so.
- No firewall modification, administrator requirement, account, cloud service, certificate installation, token rotation or new device permission model.
- Recent browser activity is not device identity, a live socket, or proof of receipt. A hidden/sleeping Safari page becomes inactive after 15 seconds; last-seen remains visible for this app session.
- Heartbeats from loopback or the PC's own local endpoint do not complete phone pairing.
- No tokens, pairing URLs, filenames or note contents in copied diagnostics. No remote IP/device labels retained in presence.
- Preserve existing transfers, Vault, motion, queue-on-quit, 164 x 48 pill, download gestures and stale-token protections.
- Keep all existing uncommitted work. No push, public repository change or GitHub release in this milestone.

## Shared Interfaces

- New `Beam.Server.Connection.ConnectionPresence` is registered as a singleton by `BeamServerHost`.
- `ConnectionPresence(TimeProvider? clock = null)`, `void RecordHeartbeat(string sessionId)`, `ConnectionSnapshot Snapshot()`.
- `ConnectionSnapshot(bool PhoneActive, int ActiveBrowsers, DateTimeOffset? LastSeenUtc)`; `ActiveWindow = 15 seconds`. At most 32 recent browser session IDs held in memory; no persisted device history.
- `POST /connection/heartbeat`, existing `X-Beam-Token`, JSON `{ "sessionId": "32 hexadecimal characters" }`. Success 204 with `Cache-Control: no-store`; invalid JSON/id 400, invalid auth 401, non-JSON 415, oversized body 413. Body limit 1024 bytes.
- The browser creates an in-memory session ID, sends every 5 seconds only while visible, aborts superseded/hidden attempts, uses a 5-second timeout and never treats a heartbeat as a transfer receipt.
- Desktop resolves `ConnectionPresence` from the existing server DI container. UI polls its snapshot, not HTTP; diagnostic HTTP GETs never create activity.

## Review Focus

1. Browser suspended, network lost, PC asleep and foreground return must not leave permanently green activity.
2. Old token or delayed successful request must not reset or relabel a newly paired browser.
3. Adapter disappears or no LAN adapter exists: QR/link must reflect current choice and checks must stay responsive.
4. Port already occupied or local check fails: app must explain startup failure and not leave a tray/server process behind.
5. First-run persistence and diagnostic copies must not expose secrets or alter transfer history.

## Task 1: Server Activity Contract (Parent)

**Files:** `src/Beam.Server/Connection/ConnectionPresence.cs`, `src/Beam.Server/Endpoints/ConnectionEndpoints.cs`, `src/Beam.Server/BeamServerHost.cs`, `tests/Beam.Server.Tests/ConnectionPresenceTests.cs`.

- [x] Add failing tests for authenticated heartbeat, malformed/missing/oversized JSON, local self-check suppression, time expiry, multiple tabs, bounded sessions, restart reset and no transfer/history mutation.
- [x] Run the new tests and confirm failures before implementation.
- [x] Implement the bounded clock-driven tracker and endpoint; register in the host.
- [x] Run the full backend suite; maintain existing authentication and storage contracts.

## Task 2: Windows Setup and Diagnostics (Native Worker)

**Files:** `src/Beam.App/App.xaml.cs`, `PairingWindow.xaml`, `PairingWindow.xaml.cs`, `PillWindow.xaml`, `PillWindow.xaml.cs`, new focused setup/network helpers, `tests/Beam.App.Smoke/Program.cs`.

- [x] Write regression checks first: adapter filtering/selection, setup marker lifecycle, real activity and expiry, diagnostic redaction, startup failure handling, QR/address synchronization and layout/lifecycle.
- [x] Implement a restrained first-run pairing/check flow in the current Beam theme. Native QR and existing icon family are the visual assets; no new decorative illustration.
- [x] Prefer active IPv4 Wi-Fi/Ethernet adapters with a gateway, excluding loopback/link-local; allow explicit alternate adapter selection and refresh.
- [x] Add asynchronous local HTTP checks and safe copied diagnostics. Distinguish verified results from suggested remote-network checks.
- [x] Add bounded/truncated header and idle-pill activity with last-seen detail, preserving current transfer motion priority and exact pill size.
- [x] Run the native smoke harness with screenshots and verify close/minimize/tray behavior.

## Task 3: Safari Connection and Retry (Browser Worker)

**Files:** `src/Beam.Server/wwwroot/app.js`, `index.html`, `styles.css`, optional `connection.js`, `tests/Beam.Web.Tests/connection.test.cjs` and static mock allowlists if needed.

- [x] Write and run failing browser tests for pairing validation, visible-only heartbeat, loss/recovery, retry, stale tokens and narrow layout.
- [x] Add clear connecting/connected/unavailable/pairing states and accessible retry action. Surface network trouble globally, not only in Receive.
- [x] Recheck on foreground/pageshow/online, without replaying uploads or discarding Vault/download state. Avoid duplicate in-flight polls/heartbeats.
- [x] Keep auth errors separate from network/server errors and validate token before claiming connection. No automatic token typo diagnosis.
- [x] Run browser regressions in Chromium and WebKit with screenshots at 320/390/1440px.

## Task 4: Integration, Review and Local Test Build (Parent)

**Files:** browser integration tests, `README.md`, `docs/setup/safari-setup.md`, `docs/setup/verification.md`, `scripts/Publish-Beam.ps1` if asset list changes.

- [x] Add real-server integration coverage for the new contract and preserved transfer flow.
- [x] Document first-run steps, what activity means and the diagnostic limits; no promises of background transfer or automatic IP rediscovery.
- [x] Run Release solution build, complete backend suite, complete Chromium/WebKit suites and complete Windows smoke harness sequentially for shared .NET outputs.
- [x] Obtain an independent review and fix verified important findings with regression tests.
- [x] Package a fresh local portable build with verified browser assets. Gracefully replace the old Beam process only after tests pass, leaving personal files/history untouched.
- [x] Report what changed, what was verified and the remaining physical-phone/network acceptance checks.

## Execution Record

- Baseline: 142/142 backend tests passed before new edits. Existing worktree and all earlier uncommitted Vault/motion work preserved.
- Ruling: Execute immediately as requested, with parent owning server/integration and disjoint browser/native workers. Do not create commits that sweep earlier uncommitted work into this milestone.
- Task 1 RED: new tests failed on missing `Beam.Server.Connection.ConnectionPresence`. GREEN: full backend suite 167/167. Two further boundary/regression checks added, pending the final suite run while the native worker owns the shared .NET build lane.
- Task 4 RED: real-server heartbeat integration failed 404 versus expected 204 against the old compiled fixture. The fixture will be rebuilt after native verification releases the build lane.
- Task 1 final: 171/171 backend checks pass. Strict 32-character session validation had two additional RED cases because .NET Guid parsing trims surrounding whitespace; explicit length checks made both GREEN. Chunked-body bounds and foreground reactivation also pass.
- Task 4 integration: Release solution and rebuilt browser fixture both build with zero warnings/errors. Complete Chromium and WebKit suites each pass 56/56, including the two new real-server checks.
- Task 2/3: native and browser workers observed RED before their implementations. Native checks include non-fatal optional mDNS, stable adapter/QR refresh, 15-second activity expiry and the full existing lifecycle suite. Browser regressions cover all three tabs, foreground recovery, overlapping/stale requests, prepared download/Vault retention and preservation of confirmed transfer results.
- Final independent review: one P2 found, minimized singleton pairing did not restore via Pair Phone. Parent reproduced it using the actual tray command (RED), restored both minimized owner and existing pairing window, then passed the entire native smoke harness (GREEN). No other concrete connection/setup finding or deferred minor was reported. Existing Vault/motion and future HTTPS/device authorization remain outside this review's scope.
- Ruling: native copy uses "Server ready" and "Browser active/inactive" rather than pretending to identify a phone or prove a socket/receipt. A first-run offer marker is separate from history and contains one byte; no devices are persisted.
- Final verification after review fix: Release solution zero warnings/errors; backend 171/171; full Chromium 56/56 and WebKit 56/56; complete Windows smoke exit 0 including real tray minimize/restore, exact pill bounds and clean quit. Screenshots inspected for Windows setup/desktop/pill and Safari at 320/390/1440px.
- Local package: `artifacts/connection-setup/ready/Beam-0.1.0-alpha.3-win-x64.zip`, SHA-256 `e73d234427f8e76db0bb34308748cf17f6ea6abd044a9c4f154ea0f09f9f440a`. All eight browser assets and START-HERE match source. Nothing was published or pushed.
- Live check: alpha.2 PID 31668 closed gracefully; alpha.3 PID 40776 responds on port 8787. `/app/` returns 200 with cache v7/global retry; authenticated heartbeat returns 204; Vault retains 20 records. Token and history SHA-256 hashes are identical before/after launch. Physical Safari/network acceptance remains for the user; no claim of automatic router diagnosis, background transfer or changed-IP rediscovery.
