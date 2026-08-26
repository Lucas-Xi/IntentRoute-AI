# IntentRoute AI v0.2.0 design

**Status:** Approved for implementation  
**Date:** 2026-08-25  
**Project:** ProxyManager -> IntentRoute AI  
**Release target:** v0.2.0

## Product definition

IntentRoute AI is a Windows control plane that turns plain-language network intent into locally validated routing-rule drafts for the existing sing-box TUN data plane. The AI feature is an authoring assistant, not an autonomous network controller: it cannot start routing, overwrite a configuration, or enable a rule without explicit user action.

The initial AI workflow supports two providers behind the same contract:

1. OpenAI Responses API, using a user-supplied API key from the `OPENAI_API_KEY` environment variable.
2. A locally running Ollama instance, using its loopback HTTP API and an already-installed model.

All current manual routing features remain available when neither provider is configured.

## User experience

The main navigation gains an **AI Rule Assistant** page. The page contains:

- A provider selector: OpenAI or Local Ollama.
- A model selector populated from safe defaults for OpenAI and the local model-list endpoint for Ollama.
- A natural-language intent editor with concise examples.
- A visible data-boundary notice describing exactly what will be sent.
- A generate/cancel action with bounded timeout and clear provider-specific failures.
- A structured draft preview showing process, host, IP, port, protocol, action, rationale, confidence, and warnings.
- A validation summary that distinguishes AI output from deterministic local checks.
- An explicit **Add as disabled rules** action.

Generated drafts never apply immediately. Accepting a validated draft adds new rules in a disabled state. The user must separately enable them through the existing rule-management workflow, which triggers the existing checked sing-box replacement process.

## Example flow

User intent:

> Route Chrome and Cursor traffic for GitHub and OpenAI through the proxy; keep everything else direct.

Possible draft:

- `chrome.exe`, `github.com, *.github.com, openai.com, *.openai.com`, Proxy, Both
- `Cursor.exe`, `github.com, *.github.com, openai.com, *.openai.com`, Proxy, Both

The interface labels this as model-generated content, displays any ambiguity warning, validates every supported field locally, and requires explicit acceptance. The user can reject the entire draft without changing disk state.

## Architecture

### Provider-neutral contract

`IAiRuleProvider` exposes:

- Provider identity and availability information.
- Model discovery where supported.
- `GenerateDraftAsync(AiRuleRequest, CancellationToken)`.

Both provider adapters return the same `AiRuleSuggestion` model. UI code does not parse provider-specific responses.

### Shared request

`AiRuleRequest` contains only:

- The user-authored intent.
- The selected model identifier.
- The supported rule schema and product constraints embedded by the application.

It must not contain the current proxy configuration, proxy credentials, API keys, generated sing-box configuration, runtime logs, full process inventory, filesystem paths, or environment-variable values.

### Shared response schema

The provider response is constrained to an object containing:

- `summary`: short description of the intended policy.
- `rules`: bounded array of rule drafts.
- `warnings`: bounded array of user-facing cautions.

Each rule draft contains:

- `processName`
- `targetHosts`
- `targetIps`
- `targetPorts`
- `protocol`: `TCP`, `UDP`, or `Both`
- `action`: `Proxy`, `Direct`, or `Block`
- `rationale`
- `confidence`: number from 0 through 1

All properties are required in the provider schema, with empty strings or arrays used where a filter does not apply. Additional properties are rejected.

### OpenAI adapter

The adapter sends a non-streaming request to `POST https://api.openai.com/v1/responses` with:

- Bearer authentication from `OPENAI_API_KEY`.
- A configurable model with a documented default.
- `store: false`.
- Strict JSON Schema structured output through `text.format`.
- A bounded output-token limit and request timeout.
- No tool definitions and no access to application state beyond the minimal request.

The response parser scans the response output collection for an `output_text` content item and does not assume the first output item contains the answer. Provider error bodies are mapped to short user-facing categories without exposing headers, credentials, prompts, or raw remote responses.

The application never writes the OpenAI API key to its configuration, logs, diagnostics, profile export, or crash messages.

### Ollama adapter

The adapter defaults to `http://127.0.0.1:11434` and permits only the literal HTTP addresses `127.0.0.1` and `::1` in v0.2.0. Hostnames, other loopback addresses, and LAN/public addresses are rejected to prevent accidental disclosure beyond the documented local endpoint boundary.

Model discovery uses `GET /api/tags`. Generation uses non-streaming `POST /api/chat` with:

- An already-installed model selected by the user.
- The shared JSON Schema in `format`.
- A low temperature for deterministic output.
- `stream: false`.
- No automatic model pull, installation, cloud fallback, or process launch.

The adapter reports Ollama-not-running, no-local-model, model-not-found, timeout, and malformed-output conditions without changing application configuration.

## Deterministic validation

`AiRuleDraftValidator` is a provider-independent trust boundary. It validates before any draft can be added:

- Overall response size and maximum rule count.
- Required fields and per-field length limits.
- Exact executable-name syntax with no paths, traversal, shell characters, or wildcard process names.
- Exact hosts and supported `*.suffix` patterns.
- Valid IPv4/IPv6 addresses and CIDRs.
- Valid ports and ascending port ranges.
- Supported protocol and action enumerations.
- Duplicate rules within the suggestion and conflicts with current rules.
- Proxy actions only when an enabled local proxy target exists.

The validator maps accepted drafts into ordinary `ProxyRule` objects and runs a candidate configuration through the existing `SingBoxConfigBuilder`. This preview dry-run is deterministic in-process construction and does not execute an external program. AI validation cannot bypass or weaken existing runtime validation; the separate enable/apply path performs `sing-box check -c` before replacing the managed runtime.

## State-change boundary

Generation and validation are read-only. Accepting a suggestion performs one atomic configuration save and adds rules with `IsEnabled = false`. It does not queue a materially changed runtime configuration because disabled rules are ignored. Enabling remains a separate existing action, followed by `sing-box check`, process replacement, startup-settle monitoring, and rollback.

The UI disables acceptance when any draft fails validation. Partial acceptance is out of scope for v0.2.0 to avoid ambiguous state.

## Branding and compatibility migration

User-facing branding becomes **IntentRoute AI**. The repository is renamed to `IntentRoute-AI`; the Windows executable and release archive use `IntentRouteAI`/`IntentRoute-AI` naming. Namespaces can remain stable where changing them would add risk without user value.

Existing data under `%APPDATA%\ProxyManager` is preserved. On first v0.2.0 launch:

1. Prefer existing IntentRoute AI data if present.
2. Otherwise detect the legacy ProxyManager directory.
3. Copy/migrate only known application-owned files into the new directory.
4. Keep the legacy directory as a recoverable fallback; do not delete it automatically.
5. Record credential-free in-progress and completion markers so interrupted copies retry only missing known files.

Migration must preserve DPAPI `CurrentUser` ciphertext on the same Windows user account and must not log decrypted credentials.

## Error handling

The AI page distinguishes:

- Provider not configured.
- Provider unavailable.
- Authentication failure.
- Rate limit or capacity failure.
- Timeout/cancellation.
- No installed local models.
- Schema-invalid or unsafe model output.
- Local candidate-validation failure.

Raw provider response bodies are not shown by default. Errors are bounded and redacted before reaching the UI or runtime logs. Cancellation leaves no partial configuration state.

## Security and privacy boundaries

- AI is optional and off by default.
- API keys are not accepted in the intent editor or persisted by the application.
- OpenAI requests set `store=false` and contain only the user intent plus the static rule schema/instructions.
- Ollama v0.2.0 accepts only literal `127.0.0.1` or `::1` HTTP endpoints.
- No provider receives proxy credentials, server addresses, existing rules, logs, process inventory, filesystem paths, or generated sing-box JSON.
- Model output is untrusted data and receives strict schema parsing plus local semantic validation.
- AI output cannot execute commands, access tools, select files, or directly mutate runtime state.
- All accepted AI rules start disabled and require a second user action to enable.

## Testing strategy

Provider tests use mocked HTTP handlers and do not require paid OpenAI calls or a live Ollama installation. Coverage includes:

- Equivalent OpenAI and Ollama results map to the same domain model.
- OpenAI payload includes `store=false`, strict schema, and no sensitive application state.
- Response parsing finds text content without assuming output-array ordering.
- API keys are absent from exceptions and logs.
- Ollama accepts literal `127.0.0.1`/`::1` and rejects hostnames, other loopback addresses, and LAN/public endpoints.
- Ollama model discovery and no-model behavior.
- Authentication failure and rate-limit mapping without exposing remote bodies.
- Invalid JSON, unexpected properties, missing/null fields, and illegal protocol/action enum values are rejected at the parser boundary.
- Excessive rule counts plus invalid executables, hosts, CIDRs, ports, duplicates, and missing proxy targets are rejected by local validation.
- Accepted rules are persisted disabled in one save operation.
- Legacy configuration migration is non-destructive, retryable after interruption, and serialized across concurrent starts.
- All existing configuration-builder, config-store, and runtime-lifecycle tests continue to pass.

## Documentation and release

The README, architecture, threat model, security policy, changelog, roadmap, contribution guidance, readiness report, assembly metadata, UI, workflow artifacts, and release notes are updated for IntentRoute AI v0.2.0. Documentation includes setup instructions for both providers, the exact data boundary, local-model prerequisites, failure recovery, and the fact that AI suggestions require human review.

The implementation is complete only after:

1. Restore, build, unit tests, vulnerability checks, and release packaging pass locally.
2. The packaged application launches in a clean smoke-test directory.
3. Provider contracts and redaction tests pass without live credentials.
4. A public-source review is completed and material findings are resolved or documented.
5. GitHub repository rename, badges, remote URLs, release links, CI, and v0.2.0 assets are publicly verified.

The OpenAI Codex for Open Source application form remains out of scope and must not be filled or submitted.

## Explicit non-goals for v0.2.0

- Autonomous rule activation or network self-healing driven by an LLM.
- Sending runtime logs or existing configurations to a model.
- Bundling an API key, Ollama, a local model, sing-box, or a proxy service.
- Automatically downloading or starting local models.
- Arbitrary OpenAI-compatible or remote Ollama endpoints.
- Multi-turn AI chat history, telemetry collection, or cloud accounts.
- Claiming that model suggestions are correct, complete, or production-safe without user verification.
