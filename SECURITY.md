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

## Reporting

Report vulnerabilities privately to the repository owner rather than publishing
tokens, exploits against a live PC, or personal transfer data in an issue. There
is currently no formal security support policy or audited release.
