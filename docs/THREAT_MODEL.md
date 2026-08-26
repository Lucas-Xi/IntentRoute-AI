# Threat model

## Assets

- Proxy credentials and endpoint details.
- Integrity of Windows network routing.
- Integrity of the executable selected as sing-box.
- Availability of the user's network connection.
- Confidentiality of local process names and runtime diagnostics.

## Trusted components

- The current Windows user and administrator approval prompt.
- ProxyManager binaries obtained from a trusted build or built from reviewed source.
- A sing-box binary independently obtained and verified by the user.
- Windows DPAPI and the current user's profile protections.

## In-scope threats and controls

### Configuration injection

ProxyManager constructs JSON through typed objects and invokes sing-box with `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, and no command shell. Host, port, CIDR, protocol, process wildcard, and proxy references are validated.

### Secret disclosure

Stored passwords are DPAPI-protected. UI/runtime error paths expose only redacted JSON or redacted output. Profile exports clear passwords. Tests assert these properties.

The generated sing-box configuration necessarily contains a usable password while running. It lives under the current user's application-data directory and is removed on clean shutdown. Abrupt termination can leave it behind; stale-file cleanup is roadmap work.

### Executable substitution

Discovery checks `PROXYMANAGER_SING_BOX`, the application directory, then `PATH`. A malicious file placed earlier in one of those trusted locations could be executed with administrator privileges. Users should install sing-box from its official release, verify it independently, and prefer an explicit environment path. Future work should add version and checksum visibility.

### Network lockout

TUN and strict routing can disrupt connectivity when a rule or upstream proxy is wrong. ProxyManager checks configuration syntax before replacing its managed process, but it cannot prove upstream reachability. Closing ProxyManager normally stops its child process. Users should retain an administrative recovery path.

### Log manipulation

sing-box output is untrusted text. It is displayed as text rather than interpreted as markup. Common credential patterns are redacted and the in-memory queue is bounded.

## Out of scope

- A compromised Windows administrator account or kernel.
- Vulnerabilities inside sing-box, Windows TUN, or a proxy provider.
- Traffic observation by a selected proxy or destination.
- Verification of third-party binary signatures or checksums.
- Protection against an attacker who can read another process's memory as administrator.
