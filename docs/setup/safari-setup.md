# Safari: Send, Receive And Vault

No App Store app or Shortcut is needed for this workflow.

## First Connection

1. Extract the Windows ZIP and open Beam.App.exe. Keep the publish files together.
2. Allow the app on your trusted private network if Windows asks.
3. Connect the iPhone and PC to a network that allows them to reach each other.
4. Pairing opens on Beam's first launch; on later launches, click Pair phone.
   Select the PC adapter on the same reachable network as the iPhone, then scan
   its QR with the iPhone camera. You can defer setup and reopen it later.
5. Open the link in Safari. The browser adopts the token and removes it from the
   address bar. Connected means a request to the PC succeeded, not that every
   later request is guaranteed to succeed.

If the QR address is unreachable, check the PC's active IPv4 address, TCP port
8787, firewall, guest Wi-Fi, VPN, or extender/client isolation. A different PC IP
can require a new QR scan. Do not disable the firewall globally.

Refresh the address list if the PC changes network. Choosing a different adapter
updates the QR and manual address together. If no usable address exists, connect
the PC to a network before scanning. The local HTTP check only tests the PC's
own server; it cannot verify the phone's route or identify a router/firewall rule.
Copied diagnostics omit the token, pairing link and private transfer contents,
but include the selected local IP and port. Review them before sharing.

## Reset and Re-pair

On Windows, open Pair phone, choose Reset pairing, and confirm. This replaces the
shared token and QR, disconnecting every old browser and optional Shortcut.
Active transfers may be interrupted. Scan the new QR to pair each browser again;
update each Shortcut's X-Beam-Token header from the new PC token.

Beam keeps Vault files, history and saved receipts. After re-pairing, use Retry
on an interrupted upload's original Safari row to check its receipt before
resending. Uploads do not resume automatically. Reloading/reselecting a file loses
that row's retry identity. Old pages clear protected content on their next 401;
data already copied or saved on a phone cannot be recalled. If Windows reports a
reset failure, the previous pairing remains active and can be retried.

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
finish saving it. Use Retry on the existing upload row. Beam checks its receipt
first: an already received item is confirmed without another upload; an unknown
receipt uploads the retained bytes with the same transfer ID. Concurrent attempts
for that ID cannot create a second saved item. A pending receipt offers Check again.
Successful items are not automatically resent when a failed item is retried.
Clear finished removes completed/failed/cancelled/pending rows from this page,
not PC data. It also discards those rows' retry IDs.

Each newly selected file or note has a new transfer ID, even if its name/content
matches an older beam. Clearing/reloading the page and selecting a file again is
a new send, not a retry. Keep the original row until its receipt is confirmed.
The PC keeps receipts with history across restarts; deleting or losing that
history removes that protection. Abrupt power loss between file and history
writes is not an exactly-once guarantee. Notes have the same retry protection.

Refresh Safari after updating Beam. Sending requires a PC that advertises safe
retry version 1; older PC builds show an update-required message instead of
silently allowing unsafe sends. The optional headerless Shortcut workflow does
not use these receipt IDs and retains its older retry behavior.

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

## Previous Beams

1. Open Vault on the phone. It includes both Sent to PC and Received from PC,
   including items already copied or downloaded.
2. Search filenames and note contents, or choose a direction and file type.
3. Tap a note's title to expand its text, then its copy icon to copy it again.
4. Tap a file's download icon, then Save. This does not remove it from Vault.
5. Tap an available image thumbnail to expand its preview. Unsupported formats
   and large images keep a file-type icon and can still be downloaded.
6. Use Refresh to fetch current history and Load more for older items.

Vault reads the existing history and files on the PC. Keep Beam running and the
PC reachable; this is not a phone-local offline backup. Files deleted outside
Beam are labelled missing. Copying or saving a received item clears pending
Receive without deleting its history. Phone-origin file downloads do not change
PC-to-phone acknowledgement state.

## Transfer Motion

The travelling symbol identifies images, messages, documents, video, audio and
archives. Cyan indicates incoming; gold indicates outgoing, relative to the
device displaying it. Upload/download percentages use measured bytes when
available; an unknown-length operation uses activity motion without a percentage.
Saving on PC remains separate from confirmed receipt. Ready to save means the
phone has prepared a download, not that it is in Photos or Files yet.

The Windows workspace and 164 x 48 pill animate persisted queue, arrival and
acknowledgement events. Their glides are notifications, not invented network
progress. Safari's reduced-motion preference and Windows animation settings
are respected.

## Reconnecting and Safety

Connection status is visible above all three tabs. Use Retry connection after
a network failure, or return to Safari to recheck. The page does not automatically
replay uploads: already listed items and prepared downloads remain available
during a temporary failure. An invalid token returns the page to pairing and
pauses waiting uploads; it does not delete saved PC files.

The Windows activity status is based on authenticated remote-browser heartbeats
every five seconds while Safari is visible. After 15 seconds without a heartbeat,
it shows inactivity/last-seen rather than a permanent connection. Locking the
phone, hiding Safari or sleeping the PC can interrupt activity and transfers.
Activity is not proof of file receipt or a permanent paired-device identity.
Opening localhost on the PC does not make the phone appear active. A changed PC
IP still requires reopening the new address, normally with a fresh QR scan.

Safari's Add to Home Screen is optional, not a promise of offline/background
functionality. If that separate browsing context opens unpaired, pair it again.

This milestone is a trusted-LAN alpha: local HTTP is unencrypted, the pairing
token is shared, and expiring device approval/HTTPS are not implemented yet.
Never port-forward Beam or share a live token/QR.
