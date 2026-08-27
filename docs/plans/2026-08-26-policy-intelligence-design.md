# Policy Intelligence v1 design

**Status:** Implemented on unreleased `main`
**Date:** 2026-08-26
**Product:** IntentRoute AI
**Release target:** v0.3.0 preview

## Problem

IntentRoute AI can create bounded rule drafts, but users cannot inspect how the complete accepted policy will be ordered, whether an earlier rule makes a later rule ineffective, or whether a disabled draft is currently safe to enable. The product therefore helps with creation but not policy maintenance.

## Product decision

Policy Intelligence v1 is a deterministic local analyzer with an optional AI explanation adapter. Local analysis owns facts. AI receives only an explicitly confirmed, closed structural disclosure for user-selected findings and can return plain-language explanation and human-review steps. Neither path changes configuration or runtime state.

## Functional requirements

### FR-PI-001: MUST — Analyze the current policy locally

Acceptance criteria:

- Analysis starts from a detached Configuration Snapshot.
- Analysis performs no filesystem write, configuration commit, runtime apply, executable probe, TCP connection, DNS query, or provider request.
- Recovery Protection never presents an empty placeholder snapshot as a clean policy.

### FR-PI-002: MUST — Use Canonical Runtime Order

Acceptance criteria:

- Builder, rules page, process-candidate page, and analyzer use priority ascending, creation timestamp ascending, then persisted source order.
- Same-priority overlapping rules produce a visible finding.
- Existing persisted order is not silently rewritten.

### FR-PI-003: MUST — Report conservative findings

Acceptance criteria:

- Exact duplicate match/outcome, exact match with different outcome, proven earlier-superset shadowing, broad process/global scope, invalid disabled rules, disabled duplicates, same-priority overlaps, and ProxyAll posture are classified.
- Disabled rules never appear as active runtime shadowing.
- A shadow finding exists only when containment is proven for process, destination union, port union, protocol, and evaluation order.
- Uncertain partial overlaps produce no false deterministic claim in v1.

### FR-PI-004: MUST — Keep Both limited to TCP and UDP

Acceptance criteria:

- Empty legacy protocol, `Both`, and `TCP/UDP` emit sing-box `network: ["tcp", "udp"]`.
- `Any`, `ALL`, and `ICMP` are rejected as unsupported product semantics.
- The analyzer applies the same TCP/UDP containment model.

### FR-PI-005: MUST — Provide an actionable WPF report

Acceptance criteria:

- A dedicated AI Policy Intelligence page shows active, disabled, critical, warning, and total-finding counts.
- Findings show local rule labels and actual evaluation ordinals.
- A selected finding can navigate to and select an affected rule.
- The UI does not call a finding a live-traffic event or claim that a configured proxy is reachable.

### FR-PI-006: MUST — Require request-level AI disclosure confirmation

Acceptance criteria:

- The user selects 1–20 findings.
- A confirmation dialog shows the exact logical JSON that will be sent, the selected provider, and the fields that are excluded.
- Canceling the dialog produces zero provider requests.
- There is no persistent “always allow” permission.

### FR-PI-007: MUST — Send only a closed Policy Disclosure

Acceptance criteria:

- The wire object contains aggregate rule/action/proxy counts and finding code, category, severity, relation, and affected-rule count.
- It cannot contain process names, domains, IPs, ports, rule/server IDs, notes, executable paths, proxy addresses, usernames, passwords, logs, generated configuration, runtime identity, or process inventory.
- Mocked OpenAI and Ollama request-body tests contain canary values for every excluded category.

### FR-PI-008: MUST — Treat AI explanation as untrusted and read-only

Acceptance criteria:

- OpenAI uses `store=false`, no tools, bounded output, and strict JSON Schema.
- Ollama remains non-streaming, literal-loopback-only, proxy-disabled, and redirect-disabled.
- Unknown properties, duplicate/unknown finding codes, null fields, oversize text, and out-of-range confidence are rejected.
- Explanation text is displayed as plain WPF text and is never parsed as a rule, note, command, or configuration mutation.

### FR-PI-009: MUST — Reject stale explanations

Acceptance criteria:

- A local fingerprint binds the request to the analyzed policy.
- The fingerprint is checked before preview and again after confirmation but before any provider request; a stale summary is not sent.
- If configuration changes before the provider response is accepted, the response is discarded and the report is refreshed.
- Cancellation, timeout, provider failure, and invalid output leave the local report and configuration intact.

## Non-functional requirements

- **NFR-PI-001: MUST — Privacy.** The Policy Disclosure is an allowlist DTO, not serialized `AppConfig`, rule objects, builder JSON, or locally rendered finding text.
- **NFR-PI-002: MUST — Safety.** No Policy Intelligence interface exposes a configuration mutation or runtime-apply operation.
- **NFR-PI-003: MUST — Determinism.** Equal snapshots produce equal fingerprints, order, codes, and findings.
- **NFR-PI-004: MUST — Bounded operation.** A scan runs on a cancellation-aware background task, examines at most 500 active and 500 disabled rules, caps local findings/comparisons, and emits an explicit incomplete-analysis finding when a budget is reached. Newer snapshots and shutdown cancel superseded work. Provider disclosure is limited to 20 selected findings; provider response and arrays have explicit limits.
- **NFR-PI-005: SHOULD — Accessibility.** Native WPF selection, keyboard multi-select, plain text, and visible status/cancel controls remain usable without mouse-only gestures.

## Deep modules and seams

- `PolicyRuntimeOrder` is the Canonical Runtime Order module. Its small interface creates leverage across builder, analyzer, and views while keeping tie behavior local.
- `PolicyIntelligence.Analyze(snapshot)` is the local fact interface. Match parsing, containment, disabled dry-run validation, fingerprinting, and finding materialization remain inside its implementation.
- `PolicyIntelligence.ToDisclosure(report, selectedCodes)` is the privacy seam. Its closed output is the only policy object that may cross an AI provider adapter.
- `IAiPolicyExplainer.ExplainPolicyAsync` is a real seam with two adapters: OpenAI and Ollama. It accepts no `AppConfig`, `ProxyRule`, local finding text, or mutation callback.

## Won't have in v1

- Automatic fixes, reordering, enabling, saving, applying, or self-healing.
- Live connection attribution, packet inspection, traffic claims, DNS lookups, or local port probing.
- Model-generated policy facts, severity changes, service-domain knowledge, or external browsing.
- Sending the entire policy, redacted sing-box JSON, all findings by default, or persistent disclosure consent.
- Partial-overlap claims that cannot be proven from supported static matchers.

## Traceability

| Objective | Requirements | Evidence |
| --- | --- | --- |
| Add mature AI lifecycle value | FR-PI-001, 003, 005, 008 | Policy Intelligence page and analyzer tests |
| Preserve fail-closed behavior | FR-PI-001, 004, 008, 009 | Builder/workspace/provider tests |
| Preserve local privacy | FR-PI-006, 007 | Exact preview plus request-body canary tests |
| Make semantics maintainable | FR-PI-002, 003 | Canonical order module and containment tests |

The OpenAI Codex for Open Source application form, release tag, and release publication remain outside this implementation.
