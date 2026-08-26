# Threat model

## Assets

- Proxy credentials and endpoint details.
- Integrity of Windows network routing.
- Integrity of the executable selected as sing-box.
- Availability of the user's network connection.
- Confidentiality of local process names and runtime diagnostics.
- Confidentiality of the OpenAI API key and user-authored AI intent.
- Integrity of the boundary between untrusted model output and routing state.

## Trusted components

- The current Windows user and administrator approval prompt.
- IntentRoute AI binaries obtained from a trusted build or built from reviewed source.
- A sing-box binary independently obtained and verified by the user.
- Windows DPAPI and the current user's profile protections.

## In-scope threats and controls

### Configuration injection

IntentRoute AI constructs JSON through typed objects and invokes sing-box with `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, and no command shell. Host, port, CIDR, protocol, process wildcard, and proxy references are validated. Upstream proxy hosts must be literal loopback IP addresses; hostnames, LAN/public addresses, and IPv4-mapped IPv6 forms are rejected.

### Corrupted or undecryptable persisted configuration

Malformed JSON, invalid UTF-8, null documents or collection entries, and proxy passwords whose `dpapi:` value cannot be decoded for the current Windows user are treated as an unusable configuration. The original `config.json` is left untouched, a timestamped recovery copy is attempted, every save path is blocked, and no sing-box apply is queued. Recovery import is semantically validated before replacement; import and reset are unavailable unless the recovery copy still exists. Filesystem or ACL failures can prevent creation of the additional recovery copy, but they do not authorize overwriting the original.

### Malicious or incorrect AI output

Model output is untrusted. Provider responses are size-bounded, parsed against a closed schema that rejects unknown properties, and independently checked for executable names, domains, CIDRs, ports, protocols, actions, duplicates, rule counts, and proxy availability. Candidate rules are enabled only in a cloned, in-process construction dry-run so `SingBoxConfigBuilder` actually parses them; preview does not execute the external binary. Accepted rules are saved disabled and require a second explicit action to enable, whose state-changing runtime path performs `sing-box check -c`. Model text is displayed only as plain WPF text and cannot invoke commands or tools.

### AI data disclosure

OpenAI receives only the user-entered intent plus static schema/instructions. The key is read at request time from `OPENAI_API_KEY` and is never stored in application configuration, profiles, logs, exports, or diagnostics. Requests set `store=false`, but provider-side processing remains governed by OpenAI's current policies and the user's account.

Ollama requests use only literal HTTP `127.0.0.1` or `::1`, with proxy use and redirects disabled. Hostname, other-loopback, LAN/public, and credentialed endpoints are rejected. IntentRoute AI never sends proxy credentials, proxy addresses, existing rules, logs, filesystem paths, or a full process inventory to either provider.

Another local process can impersonate an Ollama service by binding the selected loopback port. Its output receives no special trust: it is size-bounded, strictly parsed, locally validated, previewed, and can only be accepted as disabled rules.

### Upgrade migration interference

Legacy migration holds a per-directory exclusive lock, copies only top-level `config.json` and `*.profile.json`, and uses atomic file moves plus credential-free in-progress/completion markers. A crash leaves the legacy directory intact; the next launch with an in-progress marker fills only missing known files. Pre-existing current-version user data prevents a new migration and is never overwritten. Filesystem denial or deliberate marker tampering can still prevent migration and is outside the application's ability to repair automatically.

### Secret disclosure

Stored passwords are DPAPI-protected. UI/runtime error paths expose only redacted JSON or redacted output. Profile exports clear passwords. Tests assert these properties.

The packaged-WPF CI smoke process can opt into a one-shot unhandled-exception diagnostic path through `INTENTROUTE_SMOKE_DIAGNOSTIC_PATH`. The file contains only the exception text after the standard secret redactor; it does not include configuration JSON, provider prompts, credentials, or runtime logs, and the smoke script removes it after the process exits.

The generated sing-box configuration necessarily contains a usable password while running. It lives under the current user's application-data directory and is removed on stop, clean shutdown, and unexpected child exit. A per-directory lock prevents two IntentRoute AI instances from owning the same runtime. After an application or OS crash, the next launch uses a credential-free PID/start-time lease to recover a recorded orphan and removes stale generated configs and candidates. Abrupt termination can still leave files present until that next launch, and cleanup can fail under filesystem or administrator interference.

### Executable substitution

Discovery checks a saved path, `INTENTROUTE_SING_BOX`, the legacy `PROXYMANAGER_SING_BOX`, the application directory, then `PATH`. Every saved, imported, migrated, environment, application-directory, and `PATH` result is an unapproved hint at the start of each elevated session. IntentRoute AI may display the resolved candidate but does not execute `version`, `check`, or `run` until the user reselects that exact file through the Settings file picker in the current session. This prevents a user-writable configuration or profile from turning an imported path into elevated code execution. A malicious compatible program can still replace an approved file later in the same session; content fingerprints, handle-based execution, and signature verification remain future hardening. Users should obtain and verify sing-box independently.

### Local proxy probing and credential exposure

Authenticated proxy settings are limited to literal loopback IP addresses and DPAPI-protected storage. The Settings test performs only a bounded TCP connection to that local address and never transmits a username or password. A malicious local listener can make the port test pass, so the result is explicitly not presented as authentication, protocol, internet-reachability, or routed-traffic success.

### Runtime replacement and network lockout

TUN and strict routing can disrupt connectivity when a rule or upstream proxy is wrong. IntentRoute AI uses IPv4 and IPv6 TUN addresses and checks configuration schema/syntax before replacing its managed process, but `sing-box check` cannot prove adapter creation, firewall compatibility, or upstream reachability. If a checked replacement exits during startup, IntentRoute AI attempts to restore and restart the previous checked configuration. Closing IntentRoute AI normally stops its child process. Users should retain an administrative recovery path because rollback and orphan recovery are best effort.

Version/check helper processes are killed on timeout and caller cancellation. WPF shutdown cancels readiness and apply work, then asynchronously waits for runtime cleanup so the UI dispatcher does not deadlock against status callbacks while a child process is being stopped.

### Log manipulation

sing-box output is untrusted text. It is displayed as text rather than interpreted as markup. Common credential patterns are redacted and the in-memory queue is bounded.

## Out of scope

- A compromised Windows administrator account or kernel.
- Vulnerabilities inside sing-box, Windows TUN, or a proxy provider.
- Traffic observation by a selected proxy or destination.
- Verification of third-party binary signatures or checksums.
- Protection against an attacker who can read another process's memory as administrator.
- Guaranteed cleanup after disk failure, ACL interference, deliberate state-file tampering, or a crash before the next IntentRoute AI launch.
- Correctness or completeness of AI-generated service-domain knowledge.
- Security, privacy, licensing, or behavior of an installed Ollama model or upstream AI provider.
