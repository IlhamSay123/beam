# Beam — Design Spec

Date: 2026-09-13
Status: Approved for planning

## Purpose

A personal, fast, no-cloud way to send text, files, photos, and videos between one iPhone and one Windows PC, over the same home WiFi. The primary differentiator versus existing tools (LocalSend, Snapdrop, Telegram Saved Messages) is UX: near-zero-friction sending from the iOS Share Sheet, and a small, unobtrusive, polished PC-side widget instead of a full app window.

## Constraints

- iPhone: no Mac available, so no custom-compiled native iOS app (no Xcode build, no Apple Developer account). The Share Sheet integration must be achieved without compiling anything — via Apple Shortcuts.
- PC: Windows only, built in .NET (C#).
- Network: same WiFi only. No relay server, no internet-facing component, no user accounts.
- Devices: exactly one iPhone and one PC. No multi-device pairing/management UI.
- No persistent iPhone widget needed (Apple disallows true floating overlay widgets anyway; Home/Lock Screen tile widgets were considered and explicitly dropped as unnecessary).

## Architecture

```
iPhone                                PC (Windows)
──────                                ────────────
Share Sheet → "Send to PC" Shortcut   .NET tray app (WinUI/WPF)
   → HTTP POST (multipart) ────────►  embedded ASP.NET Core/Kestrel
                                       server, LAN-only, token-gated
                                            │
Safari → installed PWA ("Beam")            │ saves to local Inbox folder
   (view/download received items,          │ + JSON history index
    optional Web Push notify) ◄────────────┤
                                       Tray icon + floating pill widget
                                       (expand → drop zone, history,
                                        "send to phone" action)
```

Two independent codebases:
1. **Beam PC app** (.NET, C#, WinUI or WPF) — tray icon, floating pill widget, embedded HTTP server, local storage/history.
2. **Beam phone artifacts** — an Apple Shortcut (`Send to Beam`, no code, built in the Shortcuts app / distributed as an `.shortcut` file) for phone→PC sends, and a small PWA (static web app served by the same PC server, added to iPhone Home Screen) for viewing/downloading PC→phone items.

No shared code between the two; they only share the HTTP contract below.

## Pairing & Security

- On first run, the PC app generates a random pairing token and displays it as a QR code in the expanded widget.
- Scanning the QR code (Camera app) opens a one-time setup link that:
  - Installs the `Send to Beam` Shortcut pre-filled with the PC's `.local` hostname and the token.
  - Prompts to add the Beam PWA to the Home Screen.
- Every request (Shortcut POST, PWA fetch) carries the token in a header (`X-Beam-Token`); the PC server rejects requests without a valid match.
- The PC advertises itself via mDNS/Bonjour (e.g. `beam-pc.local`) so the Shortcut and PWA keep working across DHCP lease changes/router reboots without re-pairing.
- This is intentionally lightweight (shared-secret, not full auth) — appropriate for a single trusted device pair on a home LAN, not a multi-tenant or internet-facing system.

## HTTP Contract (PC server, LAN-only)

- `POST /receive` — multipart body, one or more file blobs and/or a plain text field in a single request. Header: `X-Beam-Token`. Used by the Shortcut. Returns 200 + one item id per part received.
- `GET /outbox` — returns unread items PC→phone has queued (metadata: id, name, type, timestamp). Used by the PWA on open/poll.
- `GET /outbox/{id}/content` — downloads a specific outbox item's bytes.
- `POST /outbox/{id}/ack` — marks an item as delivered/read, removing it from the "unread" badge count.
- (Optional, later) `POST /push/subscribe` — registers a Web Push subscription for outbox notifications.

## Data Flow

**Phone → PC:** User shares from any app (Photos, Files, Safari, etc.) → taps "Send to Beam" in Share Sheet → Shortcut POSTs to `/receive`. Selecting multiple photos/files before tapping Share sends them as one batch in a single request, not one send per file. PC server writes each part to `Inbox/` + appends to history index → floating pill animates (brief pop/glow) + Windows toast (one toast for the whole batch, e.g. "3 items received") → items appear at top of expanded history list.

**PC → Phone:** User drags one or more files onto the floating pill, or clicks "Send to phone" in the expanded view (also works for arbitrary clipboard text via a hotkey) → PC server writes to `Outbox/`, marks unread → next time the PWA is opened it calls `/outbox`, shows new items with a subtle entrance animation. Files download on tap; text items get a "Copy" action that puts the text directly on the phone's clipboard instead of downloading a `.txt` file. If Web Push is added later, the phone gets a notification without needing to open the PWA first.

## Storage & History

- Files land in plain folders: `Documents\Beam\Inbox` and `Documents\Beam\Outbox` — no database.
- A single JSON index file tracks history metadata (id, direction, name, type, size, timestamp, read/unread).
- The expanded widget shows a scrollable history (most recent first), capped to a configurable window (default: last 50 items or 7 days). Clicking a file entry opens its file location (PC) or downloads it (phone); clicking a text entry copies it straight to the clipboard on either side instead of opening a file.
- No enforced file-size cap — it's LAN-only transfer; large videos just show a progress bar in the widget during transfer.

## UX / Visual Design Principles

This is a stated priority, not an afterthought:

- **PC widget:** idle state is a small tray icon plus an optional floating pill (toggleable) that can sit always-on-top. Expanding is an animated morph (pill → panel), not an abrupt window swap. Receiving/sending an item triggers a brief, tasteful animation (e.g. glow pulse, item slide-in) rather than a jarring popup. Visual style should feel modern/native (Fluent Design conventions), not a bare debug UI.
- **iOS Shortcut:** named and iconed clearly ("Send to Beam") so it's instantly recognizable in the Share Sheet; kept to the minimum number of Shortcut steps so it runs near-instantly with minimal "Running Shortcut" UI flash.
- **PWA:** clean, mobile-first styling, smooth transitions when new items appear, obvious tap targets for downloading — should feel like a small polished app, not a raw file listing.

## Error Handling

- PC server unreachable/off: Shortcut shows a native "couldn't connect" failure (Shortcuts' own error UI) — no custom retry logic needed for v1.
- Wrong/missing token: server returns 401; Shortcut surfaces a clear error rather than failing silently.
- PC hostname/IP changed and mDNS resolution fails: widget shows a "re-scan QR to reconnect" prompt.
- Partial/interrupted large-file transfer: server discards incomplete uploads rather than saving a corrupt partial file.

## Testing

- PC server: unit/integration tests against the HTTP contract (token validation, multipart handling, outbox ack flow) — runnable without any real device.
- PC widget UI: manual verification (animations/visual polish aren't practical to unit test) plus basic WinUI/WPF component tests where feasible.
- End-to-end: manual pass using the real Shortcut and PWA against a running PC instance before considering a milestone done.

## Explicitly Out of Scope (v1)

- Cross-network / "anywhere" access (relay server) — noted as a possible future stretch goal, not designed here.
- Multi-device pairing/trust list.
- Native compiled iOS app, iOS home/lock screen widgets.
- Web Push notifications (mentioned as an easy future add, not required for v1).
- User accounts / multi-user auth.
