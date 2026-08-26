# Architecture

## Supported path

`ProxyManager.Standalone` is the retained .NET 8 WPF project/namespace for the IntentRoute AI control plane. It does not capture packets itself.

1. The UI edits an `AppConfig` model.
2. `AppConfigStore` persists settings atomically and protects passwords with DPAPI.
3. `SingBoxConfigBuilder` validates supported semantics and produces full and redacted JSON representations.
4. `SingBoxRuntime` discovers a separately installed sing-box candidate, but only an exact path explicitly approved in Settings may run the bounded, no-shell `sing-box version` probe.
5. Only a recognized v1.13+ result crosses the readiness gate. Missing, old, timed-out, or unrecognized binaries do not receive configuration data and are not started.
6. A candidate configuration is written atomically and checked with `sing-box check -c`. This validates schema and configuration, not live network reachability.
7. A checked candidate is promoted and the managed process is replaced. If the replacement exits during its startup-settle window, IntentRoute AI atomically restores and restarts the previous checked configuration when one exists.
8. sing-box creates dual-stack IPv4/IPv6 Windows TUN routes and applies process/destination route rules.

## AI authoring path

1. The AI page creates an `AiRuleRequest` containing only the user-entered intent and selected model.
2. `OpenAiRuleProvider` uses the Responses API with strict JSON Schema and `store=false`, or `OllamaRuleProvider` uses a non-streaming literal-`127.0.0.1`/`::1` `/api/chat` request with the same schema.
3. Provider-specific JSON is converted to the shared `AiRuleSuggestion` model with unknown properties rejected.
4. `AiRuleDraftValidator` enforces local size, process, host, IP/CIDR, port, protocol, action, duplicate, and proxy-availability rules.
5. New rules are temporarily enabled only in a cloned candidate and passed through `SingBoxConfigBuilder`; this prevents disabled-rule filtering from turning validation into a no-op. This AI-preview dry-run is in-process construction, not execution of the external `sing-box` binary.
6. After explicit user confirmation, `AiRuleAcceptance` saves the whole draft atomically as disabled rules without replacing the running sing-box process.
7. Enabling remains a separate manual action through the supported path above, where a candidate file is checked by `sing-box check -c` before process replacement.

## Trust boundaries

| Boundary | Responsibility |
| --- | --- |
| WPF input to config builder | Validate process patterns, hosts, IP/CIDR, ports, protocols, proxy references, and server endpoints |
| Config builder to file system | Never log full JSON; write atomically to the per-user application directory |
| File system to sing-box | Display unapproved discovery hints; execute only a persisted user-selected path without a command shell, then validate before run |
| sing-box output to UI | Redact common password, secret, token, and credential patterns |
| Profile export | Remove passwords rather than exporting DPAPI ciphertext |
| Runtime ownership | Hold an exclusive per-config-directory lock and record the child PID, start time, and executable path without credentials |
| Legacy data to current directory | Hold an exclusive migration lock, copy only missing known files atomically, retain the source, and resume only when an in-progress marker proves ownership |
| Persisted configuration to application state | Preserve unreadable input, create a recovery copy when possible, block all writes and runtime apply, and require explicit import/reset recovery |
| Proxy settings to outbound | Accept only literal loopback IP addresses and valid ports; protect stored passwords with DPAPI; never send credentials during the TCP-listener check |
| User intent to AI provider | Send only intent plus static schema/instructions; never include proxy settings, existing rules, logs, paths, or process inventory |
| AI output to domain model | Reject oversized, malformed, missing, or additional fields; treat all model text as untrusted data |
| Ollama client to local service | Permit only literal HTTP `127.0.0.1` or `::1`; disable proxy use and redirects; never pull models or launch a process |
| Accepted AI draft to config | Validate all-or-nothing, dry-run enabled clones, persist disabled rules once, and require a second action to enable |

## Failure behavior

Invalid replacement configuration does not intentionally terminate a healthy managed process. A checked replacement that immediately fails to run triggers a best-effort rollback to the prior generated configuration and process. Process output is bounded in memory.

Cancellation or shutdown kills any in-flight `sing-box version` or `sing-box check` process tree before propagating cancellation. Window shutdown cancels outstanding readiness/apply work and asynchronously waits for runtime ownership and secret-bearing generated files to be released instead of synchronously blocking the WPF dispatcher.

An unreadable persisted configuration does not fall back to an active empty/default configuration. The UI remains available in a recovery state, while configuration mutations and sing-box application are blocked. The original file is not rewritten until the user explicitly imports a valid replacement or confirms reset.

On stop, normal shutdown, or an unexpected managed-child exit, IntentRoute AI removes the generated configuration. On the next launch after an application or OS crash, it verifies a recorded orphan by PID and start time, checks the executable path when Windows permits it, terminates that process tree, and removes stale configs/candidates. Recovery and deletion remain best effort under administrator interference, filesystem failure, or corrupted state.

Provider failure, cancellation, rate limiting, missing local models, malformed output, or local validation failure leaves configuration unchanged. Raw provider error bodies are not surfaced. Provider/model controls remain locked while a generation request is in flight so a response cannot be attributed to a newly selected provider.

## Dependency boundary

sing-box, Ollama, local models, and OpenAI service access are separate dependencies selected/configured by the user. None are linked, vendored, downloaded, or redistributed by IntentRoute AI. Upstream behavior, availability, pricing, data handling, and licensing remain the responsibility of their respective projects/providers.
