# Safari: Send and Receive

No App Store app or Shortcut is needed for this workflow.

## First Connection

1. Extract the Windows ZIP and open Beam.App.exe. Keep the publish files together.
2. Allow the app on your trusted private network if Windows asks.
3. Connect the iPhone and PC to a network that allows them to reach each other.
4. In Beam, click Pair phone and scan its QR with the iPhone camera.
5. Open the link in Safari. The browser adopts the token and removes it from the
   address bar. Connected means a request to the PC succeeded, not that every
   later request is guaranteed to succeed.

If the QR address is unreachable, check the PC's active IPv4 address, TCP port
8787, firewall, guest Wi-Fi, VPN, or extender/client isolation. A different PC IP
can require a new QR scan. Do not disable the firewall globally.

## Phone to PC

1. Open Send.
2. Choose files, or Photos & videos. Multiple selection uploads each item in turn.
3. Watch the result for each item. Sent to PC appears only after the server
   returns an ID for a saved item.
4. For text or a URL, enter Note or link and tap Send note.
5. Open the Vault on the PC to find the received files or notes.

Browser file uploads preserve original names and media types; duplicate names
are stored separately. The picker does not restrict arbitrary file extensions.
Default limits are 100 MiB per file and 64 KiB per note (shown as MB/KB in the UI).
The server enforces these limits; the UI reads them from /capabilities.

Cancel stops the browser request or removes a waiting item from the queue.
Cancellation, a timeout, or losing the response cannot guarantee the PC did not
finish saving it. Retry may create a duplicate for an unconfirmed transfer.
Successful items are not automatically resent when a failed item is retried.
Clear finished removes completed/failed/cancelled rows from this page, not PC data.

Keep Safari open during transfers. The queue is held in the current page, not
persisted across reloads, Safari termination, or phone restarts. This is not a
background/resumable transfer service.

## PC to Phone

1. On the PC, choose/drop files, send a note, or send clipboard text.
2. On the phone, open Receive. The pending count updates while the page is open.
3. Tap Copy for a note; if copying is blocked, select the displayed text manually.
4. For a file, tap Download, then Save when it has been prepared.

Downloads still use a browser-memory preparation step, so available phone memory
limits practical file sizes. Acknowledgement confirms the copy/download action,
not that a file was permanently saved into the phone's chosen folder.

## Reconnecting and Safety

The page refreshes its inbox when it becomes visible again. PC unavailable keeps
already listed received items visible for retry. An invalid token returns the
page to pairing and pauses waiting uploads; it does not delete saved PC files.

Safari's Add to Home Screen is optional, not a promise of offline/background
functionality. If that separate browsing context opens unpaired, pair it again.

This milestone is a trusted-LAN alpha: local HTTP is unencrypted, the pairing
token is shared, and expiring device approval/HTTPS are not implemented yet.
Never port-forward Beam or share a live token/QR.
