# Security

Beam is a trusted-local-network development tool. It is not designed for direct
internet exposure, multi-tenant hosting, or encrypted archival storage.

## Sensitive Data

Never commit or attach:

- The real pairing token or a QR/pairing URL containing it.
- Documents/Beam/token.txt or history.json.
- Inbox/Outbox files, private note contents, or unredacted diagnostic logs.
- Screenshots that reveal tokens, filenames, local paths, or private transfers.

Use synthetic files and replace tokens with placeholders in examples. A private
GitHub repository does not make it a safe place to store live credentials.

## Boundaries

The server listens on all interfaces at port 8787 and authenticates data endpoints
with X-Beam-Token. It uses HTTP and does not encrypt data at rest. Browser local
storage and the PC token file hold the same bearer secret. Protect the Windows
account and local network, and do not forward the port through a router.

## Reset Pairing

The Windows Pair phone window offers a confirmed Reset pairing action. It creates
a cryptographic random shared key, atomically replaces token.txt before activating
it, cancels old authenticated requests, and waits for their handlers to leave
before reporting success. Protected requests are rejected while those handlers
drain. There is no HTTP reset endpoint. A token-file replacement failure leaves
the previous key active.

Reset invalidates all old browser tokens, QR links and Shortcut headers together.
Browsers clear old Receive/Vault content and prepared download URLs when they
next receive 401; they must explicitly pair again. The PC keeps files, history,
acknowledgements and safe-retry receipts. Already copied or downloaded data cannot
be recalled. Re-pairing does not automatically resend interrupted uploads.

Beam suppresses query-bearing ASP.NET request-start/finish logs and keeps its
authentication errors free of token values. QR links remain sensitive: HTTP,
browser storage, clipboard copies and the PC token file retain the existing
trusted-LAN security boundaries. There is no individual-device revocation,
automatic token expiry, or encrypted transport.

## Windows installation and startup

Startup is an explicit current-user opt-in. It stores the quoted executable
path plus `--startup` in HKCU's Run key; no token or pairing URL is included.
The installer runs per-user, does not add firewall rules, and does not force-close
the app. Uninstall keeps Documents/Beam and deletes a startup value only when it
still matches this installed copy. Startup does not change the trusted-LAN HTTP
or storage boundaries above. The app and preview installer are unsigned.

## Reporting

Report vulnerabilities privately to the repository owner rather than publishing
tokens, exploits against a live PC, or personal transfer data in an issue. There
is currently no formal security support policy or audited release.
