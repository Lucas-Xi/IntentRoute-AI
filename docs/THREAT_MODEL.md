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

The generated sing-box configuration necessarily contains a usable password while running. It lives under the current user's application-data directory and is removed on stop, clean shutdown, and unexpected child exit. A per-directory lock prevents two ProxyManager instances from owning the same runtime. After an application or OS crash, the next launch uses a credential-free PID/start-time lease to recover a recorded orphan and removes stale generated configs and candidates. Abrupt termination can still leave files present until that next launch, and cleanup can fail under filesystem or administrator interference.

### Executable substitution

Discovery checks `PROXYMANAGER_SING_BOX`, the application directory, then `PATH`. A malicious file placed earlier in one of those trusted locations could be executed with administrator privileges. Users should install sing-box from its official release, verify it independently, and prefer an explicit environment path. Future work should add version and checksum visibility.

### Runtime replacement and network lockout

TUN and strict routing can disrupt connectivity when a rule or upstream proxy is wrong. ProxyManager uses IPv4 and IPv6 TUN addresses and checks configuration schema/syntax before replacing its managed process, but `sing-box check` cannot prove adapter creation, firewall compatibility, or upstream reachability. If a checked replacement exits during startup, ProxyManager attempts to restore and restart the previous checked configuration. Closing ProxyManager normally stops its child process. Users should retain an administrative recovery path because rollback and orphan recovery are best effort.

### Log manipulation

sing-box output is untrusted text. It is displayed as text rather than interpreted as markup. Common credential patterns are redacted and the in-memory queue is bounded.

## Out of scope

- A compromised Windows administrator account or kernel.
- Vulnerabilities inside sing-box, Windows TUN, or a proxy provider.
- Traffic observation by a selected proxy or destination.
- Verification of third-party binary signatures or checksums.
- Protection against an attacker who can read another process's memory as administrator.
- Guaranteed cleanup after disk failure, ACL interference, deliberate state-file tampering, or a crash before the next ProxyManager launch.
