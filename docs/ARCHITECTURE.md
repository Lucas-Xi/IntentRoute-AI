# Architecture

## Supported path

`ProxyManager.Standalone` is the retained .NET 8 WPF project/namespace for the IntentRoute AI control plane. It does not capture packets itself.

1. The UI edits an `AppConfig` model.
2. `AppConfigStore` persists settings atomically and protects passwords with DPAPI.
3. `SingBoxConfigBuilder` validates supported semantics and produces full and redacted JSON representations.
4. `SingBoxRuntime` discovers a separately installed sing-box executable.
5. A candidate configuration is written atomically and checked with `sing-box check -c`. This validates schema and configuration, not live network reachability.
6. A checked candidate is promoted and the managed process is replaced. If the replacement exits during its startup-settle window, IntentRoute AI atomically restores and restarts the previous checked configuration when one exists.
7. sing-box creates dual-stack IPv4/IPv6 Windows TUN routes and applies process/destination route rules.

## AI authoring path

1. The AI page creates an `AiRuleRequest` containing only the user-entered intent and selected model.
2. `OpenAiRuleProvider` uses the Responses API with strict JSON Schema and `store=false`, or `OllamaRuleProvider` uses a non-streaming loopback-only `/api/chat` request with the same schema.
3. Provider-specific JSON is converted to the shared `AiRuleSuggestion` model with unknown properties rejected.
4. `AiRuleDraftValidator` enforces local size, process, host, IP/CIDR, port, protocol, action, duplicate, and proxy-availability rules.
5. New rules are temporarily enabled only in a cloned candidate and passed through `SingBoxConfigBuilder`; this prevents disabled-rule filtering from turning validation into a no-op.
6. After explicit user confirmation, `AiRuleAcceptance` saves the whole draft atomically as disabled rules without replacing the running sing-box process.
7. Enabling remains a separate manual action through the supported path above.

## Trust boundaries

| Boundary | Responsibility |
| --- | --- |
| WPF input to config builder | Validate process patterns, hosts, IP/CIDR, ports, protocols, proxy references, and server endpoints |
| Config builder to file system | Never log full JSON; write atomically to the per-user application directory |
| File system to sing-box | Execute an explicit discovered path without a command shell; validate before run |
| sing-box output to UI | Redact common password, secret, token, and credential patterns |
| Profile export | Remove passwords rather than exporting DPAPI ciphertext |
| Runtime ownership | Hold an exclusive per-config-directory lock and record the child PID, start time, and executable path without credentials |
| User intent to AI provider | Send only intent plus static schema/instructions; never include proxy settings, existing rules, logs, paths, or process inventory |
| AI output to domain model | Reject oversized, malformed, missing, or additional fields; treat all model text as untrusted data |
| Ollama client to local service | Permit HTTP loopback only; disable proxy use and redirects; never pull models or launch a process |
| Accepted AI draft to config | Validate all-or-nothing, dry-run enabled clones, persist disabled rules once, and require a second action to enable |

## Failure behavior

Invalid replacement configuration does not intentionally terminate a healthy managed process. A checked replacement that immediately fails to run triggers a best-effort rollback to the prior generated configuration and process. Process output is bounded in memory.

On stop, normal shutdown, or an unexpected managed-child exit, IntentRoute AI removes the generated configuration. On the next launch after an application or OS crash, it verifies a recorded orphan by PID and start time, checks the executable path when Windows permits it, terminates that process tree, and removes stale configs/candidates. Recovery and deletion remain best effort under administrator interference, filesystem failure, or corrupted state.

Provider failure, cancellation, rate limiting, missing local models, malformed output, or local validation failure leaves configuration unchanged. Raw provider error bodies are not surfaced.

## Dependency boundary

sing-box, Ollama, local models, and OpenAI service access are separate dependencies selected/configured by the user. None are linked, vendored, downloaded, or redistributed by IntentRoute AI. Upstream behavior, availability, pricing, data handling, and licensing remain the responsibility of their respective projects/providers.
