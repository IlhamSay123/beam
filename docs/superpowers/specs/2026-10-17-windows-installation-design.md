# Optional Windows Startup and Installer

## Intent and authorized scope

The user requested optional Windows startup and an installer after releasing
alpha.2. They explicitly chose **Stay in the tray** for automatic startup.
Keep the existing Windows/.NET 10/WPF application and portable ZIP.

## Product behavior

- New installations leave startup unchecked. Setup and the tray menu offer
  **Start Beam with Windows**. This affects the current Windows account only.
- An automatic launch uses `--startup`: the HTTP receiver, polling and tray icon
  start normally, with no workspace, pill or pairing dialog flash. It does not
  consume the first-manual-launch pairing offer. Manual launch keeps current UI.
- A second automatic launch exits quietly; a duplicate manual launch explains
  that Beam is already available from its tray. A process-lifetime named mutex
  prevents a second instance before token/history access and lets setup detect
  a running app. Setup/uninstall ask for Beam to be closed; never force-kill it.
- A checkable startup item is available from the tray and pill context menus.
  Read actual registration when opening a menu. Failed writes show an actionable
  error and restore the displayed state. Windows can independently disable or
  delay startup; Beam does not edit Windows' StartupApproved internals.

## Implementation choices made by the agent

- Inno Setup 6.7.3, per-user `PrivilegesRequired=lowest`, installed by default
  under `{localappdata}\Programs\Beam`, Windows x64, minimum 10.0.19041.
- Stable application ID `{6B0808A8-7811-4EBF-9223-BD041CAB8D4C}` and mutex
  `Local\Beam.Desktop.6B0808A8-7811-4EBF-9223-BD041CAB8D4C`.
- Startup registration: HKCU `Software\Microsoft\Windows\CurrentVersion\Run`,
  value `Beam`, exact quoted executable path plus ` --startup`; maximum 260
  characters. The app's enabled state means this copy's command is registered.
  Disable/uninstall remove only the command owned by that copy. Explicit enable
  may replace another Beam copy's command. No launch rewrites registration.
- Setup preserves the current startup preference on upgrades, including a
  preference changed from the app since the last install. Explicit `/TASKS` or
  `/MERGETASKS` takes precedence. A fresh install without an existing Beam startup
  command defaults off. Setup applies the selected task to the installed path.
- Start-menu shortcut and Windows Installed apps/uninstaller entry; optional
  desktop shortcut; optional launch after interactive setup, skipped when silent.
- Keep Documents/Beam (files, history, receipts, token) and the setup-offer marker
  outside installation ownership. Upgrade and uninstall never remove these.
  Do not use broad wildcard deletion during upgrades. Uninstall removes its
  application files, shortcuts, installed-app entry and owned startup command.
- Use a local preview version `0.1.0-alpha.3.preview.1` for new artifacts; do not
  replace or mutate the published alpha.2 release or publish a new release now.
- Build an unsigned setup EXE plus SHA-256 alongside a portable ZIP. Existing
  alpha security/transport limits remain. No service, updater, firewall changes,
  machine-wide install, signing purchase or automatic startup enablement on this
  PC is included.

## Verification and isolation

Native tests use a unique HKCU test subtree, synthetic executable paths/data and
unique mutex names. Test registration quoting, enable/disable persistence,
unrelated values/other-copy preservation, invalid path failure and menu rollback.
Test hidden startup with a real isolated HTTP host, active polling, normal manual
open, and lifetime disposal/duplicate prevention.

Installer lifecycle tests compile the same template with a test-only identity,
test startup registry subtree and temporary shortcuts/install directory. They
silently install/upgrade/uninstall without launching Beam or changing the user's
real startup preference. Verify default off, explicit on/off, app-side preference
changes retained on upgrade, owned cleanup, foreign command preservation, blocked
install while mutex exists, exact payload/assets and untouched synthetic data.
Production defaults are tested separately when compiling the actual preview.

Compiler acquisition is pinned to the official GitHub release and SHA-256
`9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`.
CI builds and exercises installer lifecycle on Windows with the test identity.
Final gates: Release build, existing backend/browser/native regressions,
installer lifecycle, package content/hash checks and independent review.

## Builder discretion

Keep helpers small, use existing test harness patterns, and settle routine API,
error wording and test-fixture naming choices without expanding scope.
