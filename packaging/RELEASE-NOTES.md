# Beam 0.1.0-alpha.3: Windows installer and optional tray startup

A self-contained Windows x64 alpha for transferring files and notes between a
PC and iPhone over a trusted local network. Choose the Setup installer or the
portable ZIP; neither needs a separate .NET installation.

## Changes since alpha.2

- Per-user Windows installer with a Start menu shortcut, optional desktop
  shortcut, and an Installed Apps uninstaller. No administrator access is needed.
- Optional **Start Beam with Windows** in setup and the tray/pill menus. It is
  off for new installs. Automatic sign-in launches stay in the system tray while
  receiving remains active, without opening the workspace or pairing offer.
- One Beam instance per Windows session; duplicate automatic launches exit
  quietly and manual duplicates direct you to the existing tray icon.
- Upgrades retain the current startup preference, including changes made in
  Beam after installation. Explicit setup choices are honored.
- Uninstall removes its installed files and owned startup entry, while retaining
  Documents/Beam (Vault files, history, pairing and receipts). Another copy's
  startup command and unrelated files are preserved.
- Repeatable installer builds with a verified, pinned compiler, payload privacy
  checks and isolated install/upgrade/uninstall coverage in CI.

Alpha.2's Vault, safe Safari retries, pairing reset/token rotation and bundled
.NET 10.0.13 runtime are retained.

## Install or upgrade

1. Download **Beam-0.1.0-alpha.3-win-x64-Setup.exe** and its **.sha256** file,
   or choose **Beam-0.1.0-alpha.3-win-x64.zip** for portable use.
2. Quit the running Beam instance, including any older portable copy.
3. Run Setup. For an installed preview, keep its existing installation folder.
   **Start Beam with Windows** and the desktop shortcut are optional.
   For portable use, extract the complete ZIP into a fresh, stable folder.
4. Open Beam and refresh Safari. Existing Documents/Beam data and pairing are
   reused; upgrading alone does not rotate the pairing token.

If switching from a portable copy, disable startup from that copy before
removing its folder. Enabling startup from the installed copy replaces the old
startup command. Windows Settings > Apps > Startup can independently override
or delay sign-in launching.

Compare `Get-FileHash <download> -Algorithm SHA256` with the full hash in that
download's .sha256 file. Keep a portable ZIP's extracted files together.

## Verification

- Windows Release build, 199 backend checks and 93 browser checks in each of
  Chromium and WebKit.
- Full local native desktop/tray/server smoke, including hidden startup,
  responsive pairing reset, retained data and normal workspace reopening.
- Isolated Windows installer lifecycle checks for default and explicit startup,
  upgrades after app-side opt-in/opt-out, paths with spaces, running-app refusal,
  uninstall data retention and preservation of another copy's startup command.
- The user confirmed Windows sign-in startup worked and Beam stayed in the tray
  with alpha.3.preview.1 on 2026-10-18.
- The final Setup EXE and ZIP are rebuilt with the alpha.3 version; bundled
  runtime, browser/setup assets, absence of personal data and SHA-256 are checked.

## Alpha limits

The app and installer are unsigned. This remains an unencrypted trusted-LAN
alpha with a shared bearer token; do not port-forward Beam or share a live
token/QR. There is no automatic updater, per-device revocation or token expiry.
Uninstall deliberately retains personal Beam data and pairing.

Keep Safari open for transfers. Retry the original upload row to preserve its
receipt identity; reloading or selecting the file again starts a new job.
Uploads are not chunk-resumable. Default limits remain 100 MiB per file and
64 KiB UTF-8 per note. Not every Windows policy, iOS version, large-file case,
notification setup or monitor configuration has been verified.