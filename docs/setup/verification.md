# Beam verification

## Alpha.3 installer/startup: 2026-10-18

Alpha.3 adds per-user opt-in sign-in startup into the tray, a process
lifetime gate, and an Inno Setup installer. Native checks use a unique HKCU test
subtree and verify command quoting/validation, menu error rollback/recovery,
ownership-aware removal, and duplicate/released mutex handles. Full native smoke
also verifies that hidden startup leaves the workspace unloaded while HTTP and
polling remain active, then opens the workspace normally.

The installer suite uses a unique test AppId, mutex, non-startup registry subtree,
shortcuts and temporary installation folder. It installs, upgrades and uninstalls
the complete payload without touching real startup or personal data. Checks cover
default/explicit choices, app-side opt-out/opt-in retention, paths with spaces,
running-app refusal, registered metadata/shortcuts, foreign-command preservation,
unowned files and synthetic user-data hashes. Packaging refuses personal data,
version mismatches and output nested inside its input payload.

```powershell
# Isolated startup/menu/instance checks, suitable for Windows CI
dotnet run --project tests/Beam.App.Smoke/Beam.App.Smoke.csproj --configuration Release -- --startup-only

# Build a package, then exercise an isolated installer identity
./scripts/Publish-Beam.ps1 -Version 0.1.0-alpha.3
$beamCompiler = & ./scripts/Get-InnoSetup.ps1
./tests/Beam.Installer.Tests/Invoke-InstallerTests.ps1 -PublishDirectory artifacts/release/Beam-0.1.0-alpha.3-win-x64 -IsccPath $beamCompiler
./scripts/Publish-BeamInstaller.ps1 -PublishDirectory artifacts/release/Beam-0.1.0-alpha.3-win-x64 -Version 0.1.0-alpha.3 -IsccPath $beamCompiler
```

Physical Windows acceptance reported on 2026-10-18: startup worked after sign-in
and Beam stayed in the tray using 0.1.0-alpha.3.preview.1. This does not claim an
additional phone transfer or disabled-startup sign-in check. Check Windows'
independent Startup setting if an enabled entry does not launch.

The implementation checkpoint d2a5171 passed all four jobs in
[GitHub CI](https://github.com/IlhamSay123/beam/actions/runs/39078961707), including
startup registration and full-payload install/upgrade/uninstall. Local native
smoke, 199 backend checks and 93 checks in each browser engine also passed.
Independent review is clear. Release preparation changes version/setup text and
documentation; application and installer behavior are unchanged from that tested
checkpoint. The final release packages are rebuilt from the release commit,
with versions, runtime, payload assets, privacy and checksums verified.

## Previous snapshot: 2026-10-17

The pairing-security milestone adds a confirmed PC-only reset. A new random key
is persisted atomically before activation; old authenticated requests are
cancelled/drained before success, old QR/browser/Shortcut keys are rejected, and
presence is cleared. Vault files/history, pending items and safe-retry receipts
remain intact. Failed token replacement preserves the current pairing. The
desktop hides QR/copy controls while resetting and reloads current credentials
when reopened. ASP.NET query-bearing request logs are suppressed; a captured-log
regression covers both original and replacement QR tokens. This remains shared
bearer authentication over trusted-LAN HTTP, without per-device identity/expiry.

Physical-iPhone acceptance reported on 2026-10-17: reset/re-pair succeeded in
0.1.0-alpha.2.preview.2. The user then reported the requested interruption/retry
and Beam restart checks working: one saved copy after retry, retained pairing
and Vault history, and rejection of the old QR. Old-Shortcut rejection and reset
during an active transfer remain separate manual cases.

Alpha.2 release preparation updates the default packaging version, setup text,
download link and release notes. The application code is unchanged from the
reviewed pairing-security milestone at 449cfba. The final Windows ZIP is rebuilt
from the release commit, with the bundled runtime, browser/setup assets, archive
checksum and absence of personal data checked before publication.

The .NET 10 source milestone targets all five projects and both GitHub workflows
at .NET 10. Local verification uses Microsoft SDK 10.0.402 and runtime 10.0.13.
The mDNS dependency graph promotes IPNetwork2 to compatible 2.6.618; xUnit remains
on its v2 API at 2.9.3 with updated test SDK/runner tooling. Neither desktop nor
backend tests now resolves the legacy NETStandard.Library chain. The current
NuGet audit reports no vulnerable direct or transitive packages in any of the
five projects. Directory.Build.props keeps full auditing enabled and makes
NU1901 through NU1904 fail restore/build and CI.

The .NET 10 rerun passes the 192 backend checks, all 92 browser checks in each
engine and the native smoke harness. A separate genuine mDNS advertise/dispose
probe also passes with the promoted IPNetwork2 binary. The local self-contained
0.1.0-alpha.2.preview.1 verification ZIP bundles all three framework runtimes at
10.0.13; its dependency manifest contains none of the legacy flagged packages,
and all browser/license assets match source. Its SHA-256 checksum was verified.
This preview package is local verification output, not a published release.

The safe-retry source milestone adds durable, authenticated PC receipts for
keyed Safari uploads and notes. Retrying checks the receipt before sending retained
bytes again; matching replays return the original item, conflicting payloads are
rejected, and simultaneous attempts serialize. It preserves the connection/setup
and Vault/motion milestones described below.

The connection/setup source milestone adds a one-time pairing offer, refreshable
LAN adapter selection, synchronized QR/manual addresses, asynchronous local HTTP
checks, redacted diagnostics, startup/optional-mDNS failure handling, and recent
remote-browser activity on Windows. Safari has visible-only authenticated
heartbeats, global error/retry controls and foreground recovery with stale-request
protection. It preserves the previous Vault/motion milestone.

The Release solution builds without warnings or errors. The backend suite has
199 passing checks. All 93 browser checks pass in Chromium and all 93 in WebKit
with serialized browser fixtures, including eleven real-server integration checks.
Pairing regressions cover repeated raw token headers, old/new authorization,
clean-restart persistence, failed writes/recovery, concurrent rotation, handler
revocation/drain and QR-secret logging. The native harness verifies responsive
reset, current QR/token/link, duplicate-action suppression, preserved data,
failure feedback and reopening with the live key. The real-browser reset case
revokes prepared Vault blobs, rejects the old QR, explicitly re-pairs, and checks
unchanged history.

The wider suite covers malformed/oversized
heartbeats, authentication, time expiry, inactive/local-browser suppression,
stale token races, retry, original file bytes, retained downloads/history,
thumbnails, responsive states, reduced motion and trail bounds. Retry coverage
includes lost responses after commit, transparent socket-reset replays, receipt
status/error handling, same-key resend, changed-payload conflicts, stale attempts,
unsupported-capability races and deliberate new sends. The lost-response fixture
streams the browser's actual multipart bytes through a local proxy before
discarding the PC response; it verifies one exact file/history record.

The 2026-10-17 GitHub checkpoint reran the Release build, 192 backend checks,
92 checks per browser engine and the native smoke harness. Independent review
found a cross-tab pairing case that retained the old Receive content while new
validation failed. A real two-tab regression failed before the fix and passes
with shared pairing invalidation clearing the old list, count, motion and blob
URLs immediately. The replacement token is preserved. `npm test` now serializes
browser fixtures, so CI uses the same command as local verification.

Before this migration, the initial 2026-10-17 checkpoint reported transitive
System.Net.Http 4.3.0 and System.Text.RegularExpressions 4.3.0 audit findings in
the desktop/test graphs. The .NET 10 dependency updates above remove those
legacy chains; the earlier source checkpoint did not change package references.

The native desktop smoke harness passes with real app/tray/server lifecycle,
queued-save shutdown, activity batching/deduplication, photo-thumbnail lifecycle,
reduced motion, missing-file actions and measured pill/layout bounds. Screenshots
use synthetic files and notes, never personal transfers.
The harness also verifies metadata-only refresh with 72 photos, bounded lazy
thumbnail caching, and preservation of a newer selection during slow refresh.
Connection/setup checks cover adapter/QR synchronization and stable refresh,
first-offer persistence, diagnostic redaction, local-check cancellation, inactive
browser expiry, occupied-port cleanup and optional-mDNS failure. The real tray
Pair Phone command restores a minimized pairing window and owner without creating
a duplicate. The occupied-port case intentionally logs a failed bind; the smoke
harness's zero exit code and PASS result determine success for that case.

The user smoke-tested the phone Vault/motion interface and reported it working,
including opening a photo and saving it into Photos. Reset/re-pair and the
follow-up interruption/retry and Beam restart checks also passed as reported
above. Remaining manual scenarios are listed below. WebKit automation is engine
coverage, not proof of every iOS picker, memory limit or save-sheet behavior.
Use [Safari setup](safari-setup.md) next.

Real-device testing during development confirmed PC-to-iPhone text/files,
iPhone-to-PC notes/photos, and six selected photos sent individually through
Repeat with Each. This does not verify every iOS version, file type, large-file
case, Windows toast configuration, or mixed-DPI monitor arrangement.

The dated continuation notes below are historical checkpoints. Their test
counts and statements about uncommitted work are not the current release status.

A local 0.1.0-alpha.2.preview.2 package verifies the pairing-reset milestone with
the three .NET 10.0.13 runtimes, matching browser/setup assets, no personal data,
a clean dependency manifest and a verified SHA-256 checksum. It is not a published
release. Independent security review found a QR query logging gap; a regression
reproduced it before the category-filter fix and passes afterward. Follow-up
review found no further issues.

The first pairing-reset CI run passed Windows/backend and Chromium, but WebKit
missed the download button click on a low Vault row before reset. Local event
and request diagnostics reproduced the missing click. The reset regression now
searches for its fixture file and waits for one rendered result before clicking,
and restores the original page to the foreground after checking an old QR.
Every security assertion remains enabled. The complete 11-case real-server
sequence passes twice in WebKit with this adjustment.

## Automated checks

From the implementation worktree:

```powershell
dotnet build Beam.sln
dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj
dotnet run --project tests/Beam.App.Smoke/Beam.App.Smoke.csproj
dotnet build tests/Beam.Web.Host/Beam.Web.Host.csproj --configuration Release
cd tests/Beam.Web.Tests
npm install
npx playwright install webkit
node --test --test-concurrency=1 *.test.cjs
```

Browser tests use installed Microsoft Edge by default. Set BEAM_BROWSER
to another Playwright Chromium channel if needed. Set BEAM_SCREENSHOTS
to an output directory to capture the 320, 390 and 1440 pixel layouts.
Build tests/Beam.Web.Host/Beam.Web.Host.csproj in Release first for the integration
fixture. Set BEAM_ENGINE=webkit to run the browser suite in WebKit.
Each test file launches a browser; the verification command serializes those
processes to bound resource use. Parallel Chromium runs have exposed intermittent
download-event and running-clock test timing failures; the serialized command
keeps every assertion and test enabled.

Backend tests exercise token authentication, multipart files/text,
outbox downloads/acknowledgements, Vault filtering/pagination, safe previews,
missing files, storage, restart persistence and static assets. Safe-retry checks
include same-key replay, payload conflicts, pending receipts, concurrent attempts,
cancelled HTTP bodies, index-write rollback and clean-restart persistence.
Real-server browser checks drop the first response after a file/note is saved,
then verify Retry confirms the original item without another POST. They also
verify an unknown receipt resends the same key/bytes and a deliberate new send
creates a separate item. Browser tests use
the real HTML, CSS and JavaScript with mocked responses and a real isolated
ASP.NET Core server. They do not automate a physical iPhone.

## Manual release gate

Run `dotnet run --project src/Beam.App/Beam.App.csproj` or the built
Beam.App.exe. Open Pair phone or the tray menu's Pair Phone and follow
[Safari setup](safari-setup.md). Shortcuts are optional.

- [ ] Tray icon and expanded widget appear; text input and history fit.
- [ ] First launch offers pairing once; Set up later keeps Beam open; later launches do not repeat the offer.
- [ ] Pair phone offers the correct reachable adapter; refresh/switch updates QR and manual address together.
- [ ] Unchanged adapter refresh leaves the picker and QR stable; a removed adapter does not leave a stale QR.
- [ ] With no usable LAN address, setup explains the missing adapter and does not offer an invented hostname/QR.
- [ ] Local checks stay responsive; copied diagnostics omit token/pairing links, transfer names and contents.
- [ ] Open Safari through the QR: Windows reports remote browser activity. A local PC preview does not count.
- [ ] Hide/lock Safari for at least 20 seconds: Windows becomes inactive with last-seen; returning rechecks activity.
- [ ] Disconnect Wi-Fi and return: the global retry action works from Send, Receive and Vault without replaying uploads.
- [ ] A changed PC IP requires an updated address/QR; no automatic rediscovery is claimed.
- [ ] A second manual Beam launch directs you to the existing tray; automatic duplicates stay quiet. No second tray/server process remains.
- [ ] Single photo arrives in Inbox, history, glow and Windows toast.
- [ ] Multiple selected photos arrive directly from Safari; check batch count and notification behavior.
- [ ] Refresh Safari after updating the PC. A PC without safeRetryVersion 1 blocks new sending instead of silently using unprotected retries.
- [ ] Interrupt Wi-Fi during a Safari upload, reconnect without reloading the page, and Retry its existing row. Check exactly one received item/file with the original bytes.
- [ ] If the PC is still saving, Check again only checks its receipt. An unconfirmed receipt is never shown as Sent.
- [ ] After an unconfirmed transfer, cleanly restart Beam and Retry the retained Safari row; a saved receipt confirms without another file.
- [ ] Selecting the same file as a new job intentionally creates another item; confirmed siblings in a batch are not resent.
- [ ] Typed phone note arrives and double-click copies it on the PC.
- [ ] Drop a PC file, then Download > Save in Safari; inspect saved bytes.
- [ ] PC Send and Send Clipboard produce phone text; Copy works on the phone.
- [ ] Copied/saved PC items leave Receive but remain reusable in phone Vault after reload.
- [ ] Phone-origin files and notes appear in phone Vault and can be downloaded/copied again.
- [ ] Vault search, direction/type filters, image previews, missing-file labels and Load more work.
- [ ] File-type motion is bounded in both directions and never reports queued/unconfirmed files as delivered.
- [ ] Reduced motion works in iOS and Windows; the pill stays 164 x 48 without overlaps.
- [ ] Unreachable PC shows a connection error and browser polling recovers.
- [ ] Wrong token returns 401 without saving input; browser shows setup.
- [ ] Cancel the Reset pairing confirmation: current token and phone access stay unchanged.
- [ ] Confirm Reset pairing: QR/token/link change; old pages, QR links and Shortcuts lose access. Active transfers stop or finish before reset reports success.
- [ ] Scan the new QR/update Shortcut headers: access returns; Vault files/history and receipts remain. Retry an interrupted Safari upload's original row without creating a duplicate.
- [ ] Restart Beam after reset: the new key still works and the old key is rejected.
- [ ] PC restart retains received history and queued/acknowledged items.
- [ ] Private-network firewall permission permits phone access.
- [ ] Setup defaults Windows startup off; the tray/pill checkbox can enable and disable it.
- [ ] Sign out/in with startup enabled: Beam stays in the tray, can be opened, and receives from the iPhone.
- [ ] Sign out/in after disabling startup: Beam stays closed.
- [ ] Upgrade keeps the current startup choice; uninstall keeps Documents/Beam.

Physical iPhone checks and Windows visual/notification checks remain
release-specific checks even where earlier development tests passed. A browser download being
started is not proof that the user finished saving it on the phone.

Safe retry applies to the existing in-page Safari job, not global file
deduplication. Reloading Safari, clearing the row or choosing the file again loses
that retry identity. Headerless legacy Shortcuts are not covered. Receipts require
the saved PC history; disk loss, external history deletion and abrupt power loss
between file/index writes are outside the guarantee. No chunk-resume or
OS-level prevention of saving a download twice is claimed.

## 2026-09-29 continuation

Implemented browser clipboard fallback, direct-tap prepared downloads,
visible connection/acknowledgement errors, stale-item clearing on 401,
PC text/clipboard sending, and the setup form's hidden-state CSS fix.
Preserved QR token adoption and static-file root/no-cache fixes.
Notification polling starts with the tray host, groups newly seen items
per poll, and the widget moves into the desktop work area before expanding.

Review found and corrected the file timeout and a race between polling
and file preparation. Both browser regressions failed before their fixes
and passed afterward. File transfers are not subject to the 30-second
metadata timeout; prepared state survives newly arriving outbox items.

Ruling: root redirects to token entry instead of embedding the secret in a
public connection page. QR pairing remains automatic; manual IP pairing
requires pasting the token.

Ruling: downloads use a preparation tap followed by a native Save link.
This keeps the download in a user gesture, at the cost of one extra tap.

No merge or commit was made; the existing implementation branch and its
pending changes remain available for review.

Verified on this PC: solution builds with zero warnings/errors; 29/29
backend tests and 10/10 Edge browser tests pass. Mobile/desktop screenshots
were inspected. The launched WPF app serves /app/ with HTTP 200 on 8787.
An independent read-only review identified the two download regressions
documented above; the added tests cover both fixes.

## File-drop freeze fix

The first real file-drop test exposed a UI-thread deadlock: QueueFile
synchronously waited for SaveFileAsync, whose continuation needed the
blocked dispatcher. A single-threaded-context regression reproduced it
before the fix. Storage awaits no longer capture that context, and the
WPF drop handler performs file/history work on a worker thread. The panel
shows progress, completion and expected file errors.

Verified: 30/30 backend tests pass, solution builds cleanly, and the WPF
smoke test raises real Drop events on a dispatcher, checks exact copied
bytes, confirms dispatcher responsiveness and verifies missing-file errors.
Beam was rebuilt and restarted with the existing token and history.
