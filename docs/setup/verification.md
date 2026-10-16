# Beam verification

## Current snapshot: 2026-10-16

Fresh publication checks pass: 35 backend tests, 10 Edge browser tests from a
clean npm install, and the native desktop smoke harness. The harness exercises
the real app/tray/server lifecycle with isolated data, including quitting from
window and tray while an indexed file save is pending. Demo screenshots are
generated from synthetic data under docs/assets.

Real-device testing during development confirmed PC-to-iPhone text/files,
iPhone-to-PC notes/photos, and six selected photos sent individually through
Repeat with Each. This does not verify every iOS version, file type, large-file
case, Windows toast configuration, or mixed-DPI monitor arrangement.

The dated continuation notes below are historical checkpoints. Their test
counts and statements about uncommitted work are not the current release status.

## Automated checks

From the implementation worktree:

```powershell
dotnet build Beam.sln
dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj
dotnet run --project tests/Beam.App.Smoke/Beam.App.Smoke.csproj
cd tests/Beam.Web.Tests
npm install
npm test
```

Browser tests use installed Microsoft Edge by default. Set BEAM_BROWSER
to another Playwright Chromium channel if needed. Set BEAM_SCREENSHOTS
to an output directory to capture the 320, 390 and 1440 pixel layouts.

Backend tests exercise token authentication, multipart files/text,
outbox downloads/acknowledgements, storage, restart persistence and static
assets. Browser tests use the real HTML, CSS and JavaScript with fixture
HTTP responses; they do not exercise Safari or Windows tray interactions.

## Manual release gate

Run `dotnet run --project src/Beam.App/Beam.App.csproj` or the built
Beam.App.exe. Open Pair phone or the tray menu's Pair Phone and follow
[Shortcut setup](ios-shortcut-setup.md).

- [ ] Tray icon and expanded widget appear; text input and history fit.
- [ ] Single photo arrives in Inbox, history, glow and Windows toast.
- [ ] Multiple selected photos arrive through Repeat with Each; check notification behavior.
- [ ] Typed phone note arrives and double-click copies it on the PC.
- [ ] Drop a PC file, then Download > Save in Safari; inspect saved bytes.
- [ ] PC Send and Send Clipboard produce phone text; Copy works on the phone.
- [ ] Unreachable PC shows a connection error and browser polling recovers.
- [ ] Wrong token returns 401 without saving input; browser shows setup.
- [ ] PC restart retains received history and queued/acknowledged items.
- [ ] Private-network firewall permission permits phone access.

Physical iPhone checks and Windows visual/notification checks remain
release-specific checks even where earlier development tests passed. A browser download being
started is not proof that the user finished saving it on the phone.

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
