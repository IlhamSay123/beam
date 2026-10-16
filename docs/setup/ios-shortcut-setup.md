# Optional Share Sheet Shortcut

Shortcuts is **not required**. Safari now sends and receives photos, videos,
files, notes and links directly. Start with the [Safari guide](safari-setup.md).
Only follow this guide if you also want a Send to Beam action in the iOS
Share Sheet.

The PC app must be running and both devices must be on the same WiFi.
Right-click Beam's tray icon and choose **Pair Phone**. Scan the QR
with the phone camera to pair the browser automatically. In Safari, use
Share > Add to Home Screen to keep Beam handy.

The QR pairs the browser; it does not install a Shortcut. Build the
Shortcut below once. Use the actual host, port and token from the pairing
window rather than assuming the example hostname resolves on your LAN.

## Build the Shortcut

1. Open Shortcuts, create a shortcut, and name it **Send to Beam**.
2. Enable **Show in Share Sheet** and accept Files, Images, Media, Text,
   and URLs (names vary slightly by iOS version).
3. In the shortcut's input action, change the response for no input from
   Continue to **Ask For > Text**. Running it directly now asks for a note.
4. Add **Get Type**, with **Shortcut Input** as its input. For a batch,
   inspect its first item. Use **If** to select the appropriate branch:
   Text and URL inputs use the text request below; other inputs use the
   file request. If your version cannot combine two conditions, nest a
   second If for URL inside the first Otherwise branch.
5. In each branch add **Get Contents of URL**, configured as described
   below. Explicitly select **Shortcut Input** for the body value; do not
   use the preceding Get Type action's result.
6. Allow local-network access if iOS requests it. Run a test while Beam
   is running on the PC.

### Text request

- URL: `http://<PC-host>:8787/receive`.
- Method: POST; Request Body: Form.
- One field: `text`, type Text, value Shortcut Input.
- Header: `X-Beam-Token`, value the PC pairing token.

### File request

- For one photo or file, use the same URL, POST method and header.
- Set **Request Body: File**, then select **Shortcut Input** as the file.
  Beam accepts the direct bytes and assigns a generated filename. Common
  media types receive the corresponding extension; others use `.bin`.
- Leave Content-Type unset in Headers; Shortcuts supplies it.

For multiple selected files, put **Repeat with Each: Shortcut Input**
inside Otherwise. Put the file request inside that loop and change its
File value to **Repeat Item**. Each selected item uploads separately.
This path has been confirmed on a real iPhone with six photos.

### Multipart batch request (alternative)

- Use the same URL, POST method and header, with **Request Body: Form**.
- One field: `files`, type File, value the complete Shortcut Input.
- This alternative sends the complete list in one request rather than
  the tested Repeat with Each setup above.
- Do not also send a text field containing the file input.

Beam accepts repeated file parts in one multipart request. Whether the
phone serializes a selected batch this way still needs a real-device test.
Do not manually set Content-Type; Shortcuts supplies the form boundary.

## Verify on the phone

- Share one photo, then three selected photos, and check PC history.
- Run Send to Beam directly and enter a note; double-click the PC history
  entry to copy it.
- On the PC, open Beam via its tray icon, type a note and press Send, or
  use Send Clipboard. The phone should show it within about four seconds.
- For a file, tap Download, wait for preparation, then tap Save. Text has
  a Copy action; if automatic copying fails, selectable text stays visible.
- Use the [verification checklist](verification.md) for the remaining
  restart, authentication and network checks.

## If it stops working

- Connection failure: check Beam is running and both devices share WiFi.
  If Windows asks about the firewall, allow Beam on your private network.
- Invalid token: rescan the PC QR for the browser and update the Shortcut
  header if the token changed.
- Hostname cannot resolve: try the PC's WiFi IPv4 address with port 8787.
  Switching from hostname to IP creates a separate browser pairing, so
  paste the token into the setup form at that address.
- No text prompt: check the no-input response is Ask For > Text.
- Only one file arrives: use Repeat with Each and set the File value to
  Repeat Item, not the full Shortcut Input list.

Apple documents [Form request bodies](https://support.apple.com/guide/shortcuts/request-your-first-api-apd58d46713f/ios)
and [Ask For when no input is received](https://support.apple.com/guide/shortcuts/limit-the-input-for-a-shortcut-apd8195f96d6/ios).
