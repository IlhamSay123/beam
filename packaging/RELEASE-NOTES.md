# Beam 0.1.0-alpha.1: Safari First

No Shortcut or App Store app is required for the normal transfer workflow.

## New

- Send and Receive tabs in the phone browser.
- Select multiple photos, videos, or arbitrary files and upload them individually.
- Send notes and URLs through the same Safari page.
- Original browser filenames, media types and bytes are preserved.
- Per-item upload progress, confirmed receipt, cancel, retry and clear-finished.
- Explicit 100 MiB file / 64 KiB UTF-8 note limits and cleanup of interrupted saves.
- Self-contained Windows x64 ZIP, START-HERE instructions and SHA-256 checksum.
- Chromium and WebKit tests, including browser-to-real-server saved-byte checks.

## Try It

1. Download the Windows ZIP and extract all its files.
2. Open Beam.App.exe. No .NET installation is required.
3. Click Pair phone and scan the QR with an iPhone.
4. In Safari, choose Send for files/notes and Receive for the PC's outbox.

The optional Share Sheet Shortcut remains compatible. Existing Documents/Beam
data is retained. The PC app must be running and reachable on TCP port 8787.

## Alpha Boundaries

This is an unsigned trusted-LAN test build, not a production security release.
It uses unencrypted HTTP and shared-token pairing. Do not expose the port to the
internet or share a live token/QR. Native iPhone testing of the new browser-send
workflow is still required; WebKit engine tests are not a physical iPhone test.

Keep Safari open while transferring. Queues do not survive page reloads and
uploads are not resumable. Cancelling or losing a response cannot guarantee the
PC did not save the item; retrying an unconfirmed item can create a duplicate.
Downloads still prepare a browser-memory blob before the Save tap.

.NET 8 is still used in this milestone. Migration to a supported longer-lived
runtime, encrypted/device-scoped pairing, installer signing and update handling
remain release-hardening work before a broader public launch.
