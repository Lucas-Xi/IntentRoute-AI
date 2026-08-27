# Roadmap

The roadmap is evidence-driven. Items move forward when implementation, tests, and maintenance capacity agree.

## v0.1 — honest preview

- [x] sing-box TUN configuration generation and validation
- [x] exact per-process Proxy, Direct, and Block actions
- [x] host, IP/CIDR, port, and protocol constraints
- [x] managed sing-box lifecycle and redacted runtime logs
- [x] DPAPI password storage and credential-free exports
- [x] Windows CI, release archives, and checksums
- [x] dual-stack Windows TUN configuration
- [x] failed-start rollback, orphan recovery, and stale generated-config cleanup

## v0.2 — AI-assisted intent authoring

- [x] IntentRoute AI product/repository branding
- [x] OpenAI Responses API strict structured output with `store=false`
- [x] Literal `127.0.0.1`/`::1` local Ollama provider and installed-model discovery
- [x] Shared untrusted-output validator and enabled-clone dry-run
- [x] Human preview plus disabled, atomic rule acceptance
- [x] Non-destructive, interruption-safe migration from the v0.1 data directory
- [x] Mocked provider, validation, persistence, and migration tests

## v0.3 — Policy Intelligence maturity work

- [x] Canonical Runtime Order shared by generated routes and read-only views
- [x] Explicit TCP + UDP compilation for `Both`, excluding implicit ICMP broadening
- [x] Local deterministic duplicate, conflict, shadowing, broad-scope, disabled-invalid, inactive-duplicate, priority-tie, and ProxyAll findings
- [x] Dedicated WPF Policy Intelligence page with local evidence and rule navigation
- [x] User-selected, exact-preview Policy Disclosure with request-level confirmation
- [x] Strict OpenAI and literal-loopback Ollama policy explanation that cannot mutate configuration
- [x] Policy fingerprint invalidation for responses that return after configuration changes
- [x] Privacy canaries, matcher containment, provider payload, and canonical-order tests
- [x] Static Route Decision Simulator with conservative three-state evaluation, local trace, stale-result rejection, and Recovery Protection

## Candidate next work

- [x] Guided sing-box discovery with version reporting
- [x] Authenticated local proxy editing in the UI
- [x] Automated CI integration tests against a pinned real sing-box release
- [ ] Accessible keyboard navigation and high-DPI test coverage
- [ ] Localized UI resources instead of hard-coded strings
- [ ] Editable AI draft fields before acceptance, with revalidation after every edit
- [ ] Provider health diagnostics that remain credential-free
- [x] Conservative partial-overlap hints with an explicit non-proven classification
- [x] Corrupt-configuration recovery that preserves the source file, blocks accidental overwrite, and guides the user through restore
- [ ] Signed release artifacts and build provenance when sustainable signing infrastructure exists

## Explicitly not promised

Autonomous AI rule activation, proxy chains, provider subscriptions, remote node management, remote Ollama endpoints, per-connection traffic attribution, and cross-platform support are not scheduled. They require separate design and security review before implementation. Until then, non-empty proxy-chain definitions are rejected rather than persisted or silently ignored.
