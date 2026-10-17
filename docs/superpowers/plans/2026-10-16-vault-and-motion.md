# Vault And Transfer Motion Implementation Plan

> **For agentic workers:** Use focused parallel implementation for the independent server, browser, and WPF tasks, followed by whole-change review. Checkboxes record verification.

**Goal:** Ship the approved phone Vault and bounded file-type transfer motion in Safari, the Windows workspace, and the 164 x 48 pill.

**Architecture:** Keep the persisted PC HistoryIndex as the source of truth. Expose authenticated, paginated history and content without exposing local paths. The phone uses real upload/download progress and explicit server confirmation; Windows reacts to observed queue, receipt, and acknowledgement events without inventing network percentages.

**Tech Stack:** Existing ASP.NET Core/.NET 8, WPF, vanilla JavaScript/CSS, xUnit, Playwright Chromium/WebKit.

**Spec:** Approved in this conversation: Receive / Send / Vault on phone; history in both directions; search, type/direction filtering, thumbnails and repeat copy/download; thin cyan incoming / gold outgoing beam with file-type symbol, bounded trail, reduced motion; existing desktop Vault retained; 164 x 48 pill retained.

## Global Constraints
- No phone installation, cloud account, Shortcut requirement, or destructive history migration.
- Saving/copying removes an outbound item from pending Receive, not persisted history.
- Phone Vault depends on the running PC; it is not an offline backup or encrypted store.
- Never label a queued item as delivered or assume a browser download was saved to Photos.
- Failed, cancelled, missing-file, changed-token, and unavailable-PC states must remain actionable.
- Keep existing binary upload, clipboard fallback, and direct-tap Safari saving behavior.
- No external runtime CDN; use the existing Windows icon font and locally bundled browser icons.
- No release publication, push, or deletion of personal Beam files.

## Review Focus
- Stale authentication responses must not clear a corrected token or show previous-pair history.
- Repeated downloads and list refreshes must not strand prepared blobs or lose copy fallback text.
- History endpoints must authenticate, omit file paths, handle missing files, and disallow active image previews.
- File-type trails must stay inside their track at 0%, 1%, 11%, 50%, and 100%, both directions and viewport extremes.
- Desktop batch events and polling must neither replay old history nor report queued items as delivered.

## Task 1: Authenticated Vault API
Files: new `src/Beam.Server/Endpoints/VaultEndpoints.cs`, `tests/Beam.Server.Tests/VaultEndpointsTests.cs`; register in `BeamServerHost.cs`.

Interface: `GET /vault?search=&direction=inbound|outbound&kind=image|file|text&offset=0&limit=40` returns `{ items, total, hasMore }`. Each item includes id, direction, name, contentType, sizeBytes, timestampUtc, read, isText, textContent, available; never filePath. `GET /vault/{id}/content` downloads any retained non-text file in either direction. `GET /vault/{id}/preview` only serves safe raster images with no-sniff, read-only; no acknowledgement side effects.

- [x] Add failing tests for both directions, acknowledged items, filtering/pagination, auth, path omission, missing files, repeat download, and preview restrictions.
- [x] Verify the tests fail before implementation.
- [x] Implement bounded queries and safe content retrieval using existing storage.
- [x] Run all backend tests and review legacy endpoint compatibility.

## Task 2: Native Desktop And Pill Motion
Files: `src/Beam.App/ExpandedPanel.xaml[.cs]`, `PillWindow.xaml[.cs]`, focused new WPF animation/model helper if needed, `tests/Beam.App.Smoke/Program.cs`.

Interfaces: use existing `HistoryIndex.List()`, per-item read flags, and `TransferQueued` to identify queue, inbound arrival, and outbound acknowledgement. Seed known IDs/read states at startup; animate actual changes once. Use a message/image/document/video/archive/audio icon, optional bounded thumbnail, and the approved directional rail. Queue/save/ready/received text must describe actual state, not simulated transport percentages.

- [x] Add native smoke assertions for new inbound files/notes, outbound queue vs read acknowledgement, batched changes, safe trail bounds, and exact pill dimensions.
- [x] Implement motion without weakening drag, minimize, tray, quit, or UI responsiveness.
- [x] Honor `SystemParameters.ClientAreaAnimation`; stop timers/storyboards at close and avoid polling overwriting active state.
- [x] Build and run the native smoke suite and screenshot desktop/pill states.

## Task 3: Safari Vault And Motion
Files: `wwwroot/index.html`, `app.js`, `styles.css`, new focused `vault.js`, `motion.js`, locally bundled icons; browser tests.

Interfaces: Task 1 API. Existing receive and upload paths stay backward compatible. Vault pagination is 40 items; support search and direction/type selectors. Render text with textContent; previews only safe raster images, capped client fetching, visible-item lazy loading, revocable authenticated blob URLs. Repeat downloads use native direct Save taps and do not change inbound items into outbound items.

- [x] Add failing browser tests for all three tabs, Vault persistence after copy/save, repeat content access both directions, filtering, unavailable data, stale-token response handling, and motion start/confirm/failure/reduced motion.
- [x] Implement Vault with generation-guarded requests, retained filters, explicit refresh and load-more.
- [x] Add file-type rail tied to actual XHR upload and streamed download byte progress; use indeterminate/arrival motion only when bytes are unknown.
- [x] Preserve upload confirmation, cancellation/retry, and clipboard fallback; ensure active transfer progress is not replaced by background polling.
- [x] Run Chromium and WebKit browser suites against mocks and the real isolated test server. Inspect screenshots at 320, 390, and desktop widths; measure trail and text bounds.

## Task 4: Integration And User Test Build
- [x] Run sequential backend, fixture, WPF build/smoke, and browser verification; never race .NET output builds.
- [x] Get an independent whole-change review and fix important issues with regression tests.
- [x] Update Safari setup / verification documentation to reflect the Vault and truthful animation states.
- [x] Publish a local self-contained Windows candidate to a new ignored artifact folder, verify assets and API, gracefully replace the current running app, and provide test steps. Do not publish a GitHub release.

## Execution Record
- 2026-10-16: Existing linked worktree `worktree-beam-implementation` is clean and reused. The UI design and bounded-trail correction are already approved; proceed with implementation, not another design approval round.
- Final verification: Release solution builds with zero warnings/errors; 142 backend tests; 40 browser tests in Chromium and 40 in WebKit, including four real-server tests per engine; full native smoke passed.
- Review regressions fixed: loaded-page/manual-copy retention, saved-blob retirement, discarded preparation readiness, unknown upload totals, intervening native selection, and eager unbounded native thumbnail decoding. Native thumbnail cache is lazy, 24 entries, with two decoders.
- Local self-contained `0.1.0-alpha.2` built under `artifacts/vault-motion/ready`; all seven browser asset/license hashes match source. ZIP SHA256: `9719f6534e37e5812ab2401efa73a11fb3a4105061a9da41ed8261fe16010d8b`.
- Previous build closed through its normal save/shutdown handlers; new build launched and verified responding, `/app/` HTTP 200 with Vault/motion assets, authenticated history and limits available, and no local paths in history metadata. Existing pairing/data retained. No commit, push, or GitHub release.
- Physical iPhone and mixed-DPI multi-monitor acceptance remain manual checks.
