# Optional Windows Startup and Installer Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans in the existing worktree, test-first changes, then an independent final review.

**Goal:** Add optional per-user Windows sign-in startup into the tray and a repeatable installer that preserves user data.

**Architecture:** A registry-backed startup registration helper and reusable menu item manage a quoted per-user Run command. App startup honors `--startup` and owns a process-lifetime mutex. An Inno Setup template consumes the existing self-contained publish folder; PowerShell builds and tests its lifecycle with isolated identity/registry/shortcuts.

**Tech Stack:** Existing .NET 10/WPF/native smoke; PowerShell and pinned Inno Setup 6.7.3. No new application NuGet dependency.

**Spec:** `docs/superpowers/specs/2026-10-17-windows-installation-design.md`.

## Global Constraints

- Startup is optional/off for new installs; automatic launches stay in the tray.
- Preserve Documents/Beam, token, history, receipts and setup-offer marker.
- No force-closing Beam, machine-level startup, firewall changes or updates to alpha.2 release assets.
- Registry command is the exact quoted exe plus ` --startup`, at most 260 chars.
- Production AppId/mutex, startup key/value and preview version are fixed by the spec.
- Tests isolate installation identity, startup registry, shortcuts, payload and data.

## Review Focus

- Spaces and long paths must not mislaunch or partially change startup settings.
- Upgrading must retain an app-side opt-out rather than re-enable a remembered setup task.
- Uninstall must retain other-copy startup commands and every user-data file.
- Background launches must not flash UI, consume pairing setup or duplicate the receiver.
- Installer tests/CI must never enable real Beam startup or install over user files.

## Task 1: Startup registration, tray launch and lifetime

**Files:** new `src/Beam.App/WindowsStartupRegistration.cs`, `StartupMenuItem.cs`, `AppInstanceGate.cs`; modify `App.xaml.cs`, `TrayIconService.cs`, `PillWindow.xaml`; new `tests/Beam.App.Smoke/StartupChecks.cs`; modify smoke `Program.cs`.

**Interfaces:** `IStartupRegistration.IsEnabled`, `SetEnabled(bool)`; `WindowsStartupRegistration(string executablePath, string runSubKey = default, string valueName = "Beam")`; `StartupMenuItem(IStartupRegistration?, Action<string>?)`, `RefreshState()`; `AppInstanceGate.TryAcquire(string name = MutexName)` returning a nullable lifetime `Mutex`; `TrayIconService.Initialize(bool startInTray = false)`.

- [x] Write a native regression for missing startup helper/menu, quoted commands, persistence/removal, foreign values and validation failure; run RED.
- [x] Implement helpers and menu with error rollback; use only per-user registration and explicit user toggles.
- [x] Add hidden-tray initialization and duplicate lifetime gate, honor `--startup`, retain first-manual pairing behavior.
- [x] Extend real native smoke for hidden window/active HTTP+polling, reopening, duplicate/released gate, and unchanged normal lifecycle; run GREEN.

## Task 2: Installer, build tooling and isolated lifecycle

**Files:** new `packaging/Beam.iss`, `scripts/Get-InnoSetup.ps1`, `Publish-BeamInstaller.ps1`, `tests/Beam.Installer.Tests/Invoke-InstallerTests.ps1`; modify package workflow and CI.

**Interfaces:** `Publish-BeamInstaller.ps1 -PublishDirectory -OutputDirectory -Version -IsccPath` produces `Beam-<version>-win-x64-Setup.exe` and `.sha256`; compiler helper returns `ISCC.exe`; test-only template parameters isolate identity, Run subtree and shortcut roots.

- [x] Write lifecycle assertions and run against missing installer build/template to capture RED.
- [x] Implement pinned compiler acquisition, per-user template, preference preservation, safe uninstall and mutex checks. Validate version/payload paths and refuse personal-data payloads.
- [x] Build and silently install/upgrade/uninstall test identity; verify default/explicit startup choices, app-side opt-out retention, command quoting, foreign-command preservation, app/data cleanup boundaries and blocked running-app mutation.
- [ ] Build actual preview setup and portable ZIP; inspect versions, payload assets and SHA-256.
- [x] Extend CI/package workflow to compile/test and upload ZIP/setup/checksums.

## Task 3: Verification, review and delivery

**Files:** current README, setup/verification guide, START-HERE and security notes as needed.

- [x] Document startup opt-in/tray behavior, uninstall data retention, Windows overrides, installer/portable upgrade and unsigned preview status.
- [x] Run Release builds, backend and both browser engines, native startup/lifecycle and installer lifecycle checks.
- [ ] Resolve independent review findings with regressions; verify actual preview package contents/checksum.
- [ ] Commit/push source and verify GitHub CI. Leave the published alpha.2 release unchanged; provide the new local installer for user testing.

## Execution record

- Baseline: clean worktree at `2a9f0a0`, released alpha.2; SDK `F:/beam-tooling/dotnet10`.
- User selected automatic startup in the tray. Agent chose per-user Inno Setup and opt-in defaults.

- Task 1: RED missing WindowsStartupRegistration; GREEN isolated registry/gate checks and full native tray/server suite. Additional RED tooltip recovery test fixed by restoring the normal hint. Native hidden startup keeps the workspace unloaded while authenticated HTTP and polling are active.

- Task 2: isolated full-payload install/upgrade/uninstall passed. Nested-output regression failed before the input guard and passed afterward. Independent review found unrelated /MERGETASKS could discard enabled startup; a new lifecycle case reproduced it, then task parsing was corrected and explicit merged on/off cases added.
- Task 3: Release build clean; full native suite, 199 backend checks and 93 checks in each browser engine passed. Final source delivery, preview-package inspection and CI results are recorded in the ignored artifacts/windows-installation-work/progress.md ledger.
- Review fix verified: full isolated installer suite passed with unrelated merged shortcut changes while enabled/disabled, explicit merged startup on/off, owned/foreign uninstall and all input guards. Independent follow-up review reports no remaining findings.
