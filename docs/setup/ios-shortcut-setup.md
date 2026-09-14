# Setting up "Send to Beam" on iPhone

Do this once, using the host/port/token shown by the PC app's
"Show Pairing QR" window.

1. Open the **Shortcuts** app → **+** (new shortcut).
2. Tap the shortcut's name at the top, rename it to **Send to Beam**.
3. Tap the settings icon (ⓘ) → enable **Show in Share Sheet** →
   under "Share Sheet Types" select **Files, Images, Text, Videos, URLs**.
4. Add action **Get Contents of URL**:
   - URL: `http://beam-pc.local:8787/receive` (use the host/port from
     the pairing screen if different).
   - Method: **POST**.
   - Request Body: **Form**.
   - Add a form field named `files`, type **File**, value: **Shortcut Input**.
   - Add a second form field named `text`, type **Text**, value:
     **Shortcut Input** (leave blank/skip when sharing files — Shortcuts
     only sends fields with a value).
   - Under **Headers**, add `X-Beam-Token` with the token value from the
     pairing screen.
5. Tap **Show More** on the Get Contents of URL action and enable
   **Allow Insecure HTTP Requests** (required since Beam uses plain HTTP
   on the LAN, per the spec's transport decision).
6. Done. From any app's Share Sheet, selecting one or more photos/files
   and tapping **Send to Beam** now POSTs them to your PC in one request.

## Sending plain text

To send typed text (not from another app's share sheet): open the
Shortcuts app → run **Send to Beam** directly → when prompted for input,
choose **Text** and type your note. (Shortcuts passes whatever input
type you give it straight through as Shortcut Input.)

## If it stops working

- "Could not connect": the PC app isn't running, or you're on a
  different network — check the tray icon is present on the PC.
- 401 error: the token changed (e.g. `token.txt` was deleted) — reopen
  "Show Pairing QR" on the PC and update the header value in step 4.
- Can't resolve `beam-pc.local`: some routers block mDNS — use the PC's
  local IP address (shown on the pairing screen) instead of the hostname.
