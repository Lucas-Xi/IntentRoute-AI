# Threat model

## Assets

- Proxy credentials and endpoint details.
- Integrity of Windows network routing.
- Integrity of the executable selected as sing-box.
- Availability of the user's network connection.
- Confidentiality of local process names and runtime diagnostics.
- Confidentiality of the OpenAI API key and user-authored AI intent.
- Confidentiality of existing rule values while requesting an optional policy explanation.
- Confidentiality of local Route Decision Queries, rule traces, and simulated results.
- Integrity of the boundary between untrusted model output and routing state.

## Trusted components

- The current Windows user and administrator approval prompt.
- IntentRoute AI binaries obtained from a trusted build or built from reviewed source.
- A sing-box binary independently obtained and verified by the user.
- Windows DPAPI and the current user's profile protections.

## In-scope threats and controls

### Configuration injection

IntentRoute AI constructs JSON through typed objects and invokes sing-box with `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, and no command shell. Host, port, CIDR, protocol, process wildcard, and proxy references are validated. Upstream proxy hosts must be literal loopback IP addresses; hostnames, LAN/public addresses, and IPv4-mapped IPv6 forms are rejected.

The active configuration is owned by a Configuration Workspace. UI and validation callers receive detached snapshots, so changing a returned object cannot mutate active routing state. Every supported edit is applied to a cloned complete candidate, normalized, validated through the supported sing-box construction semantics, and atomically persisted before publication. A validation, DPAPI, or filesystem failure leaves both the active in-memory state and persisted bytes unchanged and does not queue a runtime apply.

### Corrupted or undecryptable persisted configuration

Malformed JSON, invalid UTF-8, null documents or collection entries, missing required rule/process identities, duplicate rule/server IDs, an omitted `Id` JSON property, any non-empty proxy-chain collection, and proxy passwords whose persisted `dpapi:` value cannot be decoded for the current Windows user are treated as an unusable configuration. Required-member enforcement occurs during deserialization, before model property initializers could disguise an omitted identity with a new GUID. Proxy chains are parsed only to reject unsupported legacy/imported semantics; they are never accepted and ignored. Empty process names are never treated as global rules; only the explicit `*` literal can omit a process filter. The original `config.json` is left untouched, a timestamped recovery copy is attempted, every save path is blocked, and no sing-box apply is queued. Recovery import is semantically validated before replacement; import and reset are unavailable unless the recovery copy still exists. Filesystem or ACL failures can prevent creation of the additional recovery copy, but they do not authorize overwriting the original.

Rule, proxy, mode, AI acceptance, and profile changes share the same candidate commit path. Local edits retain an in-session sing-box approval only if the committed executable path is unchanged. Profile replacement, recovery import, and reset always clear approval, preventing imported configuration data from acquiring execution authority. Approval clearing cancels an outstanding replacement apply. A previously running process is retained for connectivity and marked `RunningStale`; if a candidate process has already replaced it, cancellation terminates that candidate and restarts the prior generated configuration. The cancellation check and green-state transition are serialized with stale marking, so the UI cannot report an older configuration as current or green while re-approval is required.

### Malicious or incorrect AI output

Model output is untrusted. Provider responses are size-bounded, parsed against a closed schema that rejects unknown properties, and independently checked for executable names, domains, CIDRs, ports, protocols, actions, duplicates, rule counts, and proxy availability. Candidate rules are enabled only in a cloned, in-process construction dry-run so `SingBoxConfigBuilder` actually parses them; preview does not execute the external binary. Accepted rules are saved disabled and require a second explicit action to enable, whose state-changing runtime path performs `sing-box check -c`. Model text is displayed only as plain WPF text and cannot invoke commands or tools.

Policy explanation uses a separate closed schema and interface. The model can reference only finding codes present in the exact confirmed Policy Disclosure; unknown or duplicate codes are rejected. Explanation cannot change a local finding's category/severity/evidence, create a rule, write a note, reorder or enable a rule, commit a configuration, or apply the runtime. A policy fingerprint is compared before preview, again after confirmation but before the provider request, and after the provider returns. A stale summary is never sent, and a result for an older snapshot is discarded rather than attached to changed rules.

Local policy analysis runs on a cancellation-aware background task. A newer configuration snapshot or window shutdown cancels superseded comparison and disabled-rule validation work; the UI clears the old report while scanning instead of presenting stale findings as current. Equivalent supported domain, integer-port, and CIDR unions are canonicalized locally, but cross-kind destination overlap remains unknown and never triggers DNS resolution or a deterministic overlap claim.

### AI data disclosure

For rule authoring, OpenAI receives only the user-entered intent plus static schema/instructions. For optional policy explanation, it receives only the exact Policy Disclosure shown in a per-request confirmation dialog: aggregate rule/action/proxy counts plus selected finding code, category, severity, relation, and affected-rule count. The disclosure type has no fields for process names, domains, IPs, ports, rule/server IDs, notes, executable paths, proxy addresses, usernames, passwords, logs, generated configuration, runtime identity, or process inventory. Local finding display text and builder redacted JSON are never serialized into this request.

The OpenAI key is read at request time from `OPENAI_API_KEY` and is never stored in application configuration, profiles, logs, exports, or diagnostics. Requests set `store=false`, but provider-side processing remains governed by OpenAI's current policies and the user's account. There is no persistent policy-disclosure consent; canceling the exact-preview dialog sends nothing.

Ollama requests use only literal HTTP `127.0.0.1` or `::1`, with proxy use and redirects disabled. Hostname, other-loopback, LAN/public, and credentialed endpoints are rejected. IntentRoute AI never sends proxy credentials, proxy addresses, existing rule values or identifiers, logs, filesystem paths, or a full process inventory to either provider.

Another local process can impersonate an Ollama service by binding the selected loopback port. Its output receives no special trust: it is size-bounded and strictly parsed. Rule-authoring output is locally validated and can only be accepted as disabled rules; policy-explanation output can only become plain text attached to already-local finding codes.

Existing rule values and notes can contain prompt-injection text. They remain on the local side of the Policy Disclosure seam, so neither provider receives or interprets them during policy explanation.

Route Decision Simulation has no provider interface. Its hypothetical process/domain/IP/port input, local trace, matched rule identity, resolved proxy identity, and result remain in memory for local display only and are never added to OpenAI or Ollama requests.

### Policy-analysis overclaim

Policy Intelligence analyzes static supported configuration, not packets, DNS results, proxy authentication, TUN state, or live connections. It shares Canonical Runtime Order with the builder and models the documented sing-box default-rule grouping: destination matcher types are ORed, ports/port ranges are ORed, and the remaining groups are ANDed. Empty legacy protocol, `Both`, and `TCP/UDP` are compiled as TCP plus UDP so sing-box v1.13 ICMP support is not silently included.

Containment findings are conservative: a shadow is reported only when an earlier enabled rule is proven to contain a later enabled rule on every supported matching dimension. Disabled rules are prospective only. Uncertain partial overlap is omitted in v1 rather than described as disjoint or proven. Same-priority overlap is reported because secondary creation/source ordering otherwise decides evaluation. A clean report is explicitly not a connectivity or correctness guarantee.

### Route-simulation overclaim

The Route Decision Simulator evaluates a detached static snapshot and never observes a connection. It first requires the same production builder to accept the snapshot, uses Canonical Runtime Order, evaluates at most 500 active rules, and returns no action for invalid input, invalid policy, or missing cross-kind destination context. A matching domain or CIDR can prove the destination OR group, but a miss in one matcher kind cannot exclude an earlier rule that also contains the other kind without the corresponding resolved-IP or domain context. Evaluation stops at that first uncertainty instead of selecting a later rule.

The query contract rejects wildcards, paths, CIDRs as query values, scoped IPv6, and ambiguous short/leading-zero IPv4 forms. The simulator performs no DNS lookup, reverse lookup, proxy probe, process inspection, runtime-log read, filesystem write, packet capture, sing-box execution, or configuration mutation. Results are fingerprinted to the policy plus normalized query, rechecked before display, hidden when input/configuration changes, and disabled entirely during Recovery Protection. The UI repeatedly labels them static what-if results rather than observed routes, live traffic, or connectivity evidence.

### Upgrade migration interference

Legacy migration holds a per-directory exclusive lock, copies only top-level `config.json` and `*.profile.json`, and uses atomic file moves plus credential-free in-progress/completion markers. A crash leaves the legacy directory intact; the next launch with an in-progress marker fills only missing known files. Pre-existing current-version user data prevents a new migration and is never overwritten. Filesystem denial or deliberate marker tampering can still prevent migration and is outside the application's ability to repair automatically.

### Secret disclosure

The display-language preference is stored in `ui-preferences.json` next to the routing configuration, deliberately outside the Configuration Workspace: it holds a single `language` token (`zh`, `en`, or `system`), never routing data or credentials, is not covered by Recovery Protection, and any unreadable or unknown value falls back to the deterministic Chinese default.

Stored passwords are DPAPI-protected. The in-memory model contains plaintext and every save creates a new DPAPI envelope, even when the literal password begins with `dpapi:`; only persisted values are interpreted as DPAPI envelopes during deserialization. UI/runtime error paths expose only redacted JSON or redacted output. Profile exports clear passwords. Tests assert these properties.

The packaged-WPF CI smoke process can opt into a one-shot managed-exception diagnostic path through `INTENTROUTE_SMOKE_DIAGNOSTIC_PATH`. Only an explicit absolute path without relative-traversal segments is honored; relative or traversal-containing values are ignored. It covers unhandled failures and exceptions caught by the startup safety dialog, including an unexpected startup-window title. The file contains only the exception text after the standard secret redactor; it does not include configuration JSON, provider prompts, credentials, or runtime logs, and the smoke script removes it after the process exits or fails validation.

The generated sing-box configuration necessarily contains a usable password while running. It lives under the current user's application-data directory and is removed on stop, clean shutdown, and unexpected child exit. A per-directory lock prevents two IntentRoute AI instances from owning the same runtime. After an application or OS crash, the next launch uses a credential-free PID/start-time lease to recover a recorded orphan and removes stale generated configs and candidates. Abrupt termination can still leave files present until that next launch, and cleanup can fail under filesystem or administrator interference.

### Executable substitution

Discovery checks a saved path, `INTENTROUTE_SING_BOX`, the legacy `PROXYMANAGER_SING_BOX`, the application directory, then `PATH`. Every saved, imported, migrated, environment, application-directory, and `PATH` result is an unapproved hint at the start of each elevated session. IntentRoute AI may display the resolved candidate but does not execute `version`, `check`, or `run` until the user reselects that exact file through the Settings file picker in the current session. This prevents a user-writable configuration or profile from turning an imported path into elevated code execution. A malicious compatible program can still replace an approved file later in the same session; content fingerprints, handle-based execution, and signature verification remain future hardening. Users should obtain and verify sing-box independently.

The repository's explicit CI/Release compatibility gate is not application behavior. It downloads one official sing-box Windows archive into a fresh runner temp directory, verifies a pinned SHA-256 before execution, runs only `version` and `check` against dummy loopback fixtures generated by `SingBoxConfigBuilder`, emits no full configuration, and deletes the executable and generated configurations. Package-content verification remains a separate gate and rejects any sing-box binary in application artifacts.

### Local proxy probing and credential exposure

Authenticated proxy settings are limited to literal loopback IP addresses and DPAPI-protected storage. The Settings test performs only a bounded TCP connection to that local address and never transmits a username or password. A malicious local listener can make the port test pass, so the result is explicitly not presented as authentication, protocol, internet-reachability, or routed-traffic success.

### Runtime replacement and network lockout

TUN and strict routing can disrupt connectivity when a rule or upstream proxy is wrong. IntentRoute AI uses IPv4 and IPv6 TUN addresses and checks configuration schema/syntax before replacing its managed process, but `sing-box check` cannot prove adapter creation, firewall compatibility, or upstream reachability. Candidate identity is not published over a still-running process when build/check fails. If a checked replacement exits or is canceled during startup, IntentRoute AI attempts to restore and restart the previous checked configuration with its previous executable path/version. Closing IntentRoute AI normally stops its child process. Users should retain an administrative recovery path because rollback and orphan recovery are best effort.

Version/check helper processes are killed on timeout and caller cancellation. Cancellation before candidate promotion publishes a terminal `RunningStale` or `Failed` outcome rather than leaving a transient status, while cancellation after promotion uses the rollback behavior above. WPF shutdown cancels readiness and apply work, then asynchronously waits for runtime cleanup so the UI dispatcher does not deadlock against status callbacks while a child process is being stopped.

### Log manipulation

sing-box output is untrusted text. It is displayed as text rather than interpreted as markup. Common credential patterns are redacted and the in-memory queue is bounded.

## Out of scope

- A compromised Windows administrator account or kernel.
- Vulnerabilities inside sing-box, Windows TUN, or a proxy provider.
- Traffic observation by a selected proxy or destination.
- Application-side verification of user-selected third-party binary signatures or checksums.
- Protection against an attacker who can read another process's memory as administrator.
- Guaranteed cleanup after disk failure, ACL interference, deliberate state-file tampering, or a crash before the next IntentRoute AI launch.
- Correctness or completeness of AI-generated service-domain knowledge.
- Proof that a simulated route occurred, that DNS resolved as assumed, or that the selected proxy/TUN path is reachable.
- Security, privacy, licensing, or behavior of an installed Ollama model or upstream AI provider.
