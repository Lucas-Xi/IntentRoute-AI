# Security policy

## Supported versions

Security fixes are provided for the latest published preview release. Older previews may be asked to upgrade before a report is investigated.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private vulnerability reporting feature on the Security tab of this repository. Include affected versions, impact, prerequisites, reproduction steps, and a minimal proof of concept. Remove real proxy credentials, tokens, personal information, and unrelated network data.

Please allow a reasonable time for acknowledgement, triage, a coordinated fix, and release. Do not test against systems or accounts you do not own or have explicit permission to assess.

## Security boundaries

- The application requires administrator rights because it manages TUN routing through sing-box.
- ProxyManager executes only the discovered sing-box path and uses argument-list process invocation without a shell.
- Stored passwords use Windows DPAPI for the current user.
- The managed sing-box configuration contains credentials in plaintext while in use and is deleted on clean exit. A crash or forced termination can leave the file behind.
- Exported profiles omit credentials.
- sing-box is an external dependency and must be obtained from its official project; ProxyManager does not update or verify that binary.

See [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md) for the complete model.
