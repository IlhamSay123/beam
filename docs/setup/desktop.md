# Beam desktop

Beam opens a resizable Transfer workspace. The Vault tab searches saved
files and note contents, filters sent/received and images/files/notes,
and switches between newest/oldest first. Selecting an item shows its
preview and open/copy, reveal-in-Explorer and resend actions. Missing
files are labelled and their actions disabled.

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
