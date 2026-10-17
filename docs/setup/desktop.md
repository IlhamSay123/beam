# Beam desktop

Beam opens a resizable Transfer workspace. The Vault tab searches saved
files and note contents, filters sent/received and images/files/notes,
and switches between newest/oldest first. Selecting an item shows its
preview and open/copy, reveal-in-Explorer and resend actions. Missing
files are labelled and their actions disabled.

## Installation and Windows startup

Alpha.3 provides a per-user Setup EXE and a portable ZIP.
Setup installs to `%LOCALAPPDATA%\Programs\Beam` by default, adds a Start menu
shortcut, and registers Beam in Windows Installed Apps. It does not require
administrator access. Keep a portable ZIP's extracted files together.

**Start Beam with Windows** and the desktop shortcut are optional/off on a new
installation. You can change startup later in the tray icon's menu or by
right-clicking the compact pill. Automatic sign-in launches stay in the tray;
receiving stays active. Choose **Open Beam** from the tray to open the workspace.
The first pairing offer waits for a normal manual launch.

The menu reflects this copy's startup registration. Windows Settings > Apps >
Startup can separately disable or delay sign-in launching. Beam does not change
Windows' independent approval settings. A portable copy needs a stable folder:
turn startup off before moving it, then enable it from its new location.
Enabling it from a different Beam copy replaces the previous startup command.

Quit Beam before running a newer Setup or uninstalling through Installed Apps.
Setup refuses to change a running app. Upgrades in the same folder retain the
current startup choice, including changes made in Beam after installation.
Uninstall removes installed app files and its own startup command, while keeping
Documents/Beam (Vault, history, receipts and token) and the pairing-offer marker.
It also keeps any startup command now belonging to another copy.
For a portable ZIP, disable startup before deleting its extracted folder.

Only one Beam receiver can run in a Windows session. Opening another copy
manually directs you to the existing tray icon; a duplicate automatic launch
exits quietly.

The alpha.3 app and installer are unsigned. On 2026-10-18 the user confirmed that
Windows sign-in startup worked with the installed preview and Beam stayed in the
tray. Windows or account policies can still override startup. Automated tests
cannot prove Windows will allow an entry that the user or a policy disabled.

## Window controls

Window controls have separate meanings:

- Pill mode: a draggable 164 x 48 on-screen widget. Click its body to
  expand; drop files onto it to queue them. Right-click for its menu.
- Hide to tray: hides the window but keeps the receiving server active.
- Minimize: normal Windows taskbar minimization.
- Close or Quit Beam: waits for an active desktop queue operation, then
  closes the application, disposes the tray icon and stops the server.

Queued means waiting for the phone's acknowledgement. Delivered means
the phone acknowledged its copy/download action, not a guarantee that
the user finished saving a file into permanent phone storage. LAN ready
means the PC server is running, not that a phone is currently connected.

The vault uses Documents/Beam/Inbox, Outbox and history.json. New records
are no longer automatically pruned at 50 items or seven days. Explicit
pruning remains available in the storage API. Older records already
removed by earlier versions cannot be reconstructed from history alone.
History writes now replace a completed temporary file. The vault is
local storage, not encrypted storage.

Desktop checks:

```powershell
dotnet run --project tests/Beam.App.Smoke/Beam.App.Smoke.csproj
```

The smoke harness uses isolated temporary data and a random server port.
It covers drop responsiveness and bytes, filename/note search, filters,
sorting, previews, acknowledgement status, resending, missing files,
window resizing, pill mode, minimizing, hiding/reopening, and quitting
from both window/tray while a file save is pending. It verifies tray
disposal, server-port release and retained history after shutdown.
An optional `--screenshots <folder>` captures actual WPF renderings.

Mixed-DPI multi-monitor dragging still needs a real hardware check.
