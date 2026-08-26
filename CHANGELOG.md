# Changelog

All notable changes are documented here. The project follows semantic versioning while the public API and behavior are still in preview.

## [0.2.0] - 2026-08-26

### Added

- Rebranded the product and release artifacts as IntentRoute AI.
- Added a provider-neutral AI rule assistant with OpenAI Responses API and loopback-only local Ollama support.
- Added strict structured-output parsing, bounded responses, provider error categories, and mocked HTTP test seams.
- Added deterministic AI draft validation for executable names, domains, CIDRs, ports, protocols, actions, duplicates, limits, and enabled proxy availability.
- Added an all-or-nothing preview and explicit acceptance flow that persists generated rules disabled without restarting sing-box.
- Added non-destructive migration of known configuration/profile files from `%APPDATA%\ProxyManager` to `%APPDATA%\IntentRouteAI`.

### Security

- OpenAI requests use `store=false`, no tools, a strict schema, and an API key read only from `OPENAI_API_KEY` at request time.
- Ollama requests reject non-loopback and credentialed endpoints and disable system proxy use and redirects.
- Neither provider receives proxy credentials, proxy endpoints, existing rules, logs, filesystem paths, or the full process inventory.
- AI rules are temporarily enabled only in a cloned dry-run so the existing builder validates their actual semantics before disabled persistence.

### Tests

- Expanded the suite from 15 to 34 tests, including provider contracts, secret redaction, malformed/unexpected output, loopback enforcement, validation, migration, and disabled-rule persistence.

## [0.1.1] - 2026-08-25

### Security

- Added dual-stack IPv4/IPv6 TUN addresses so Windows strict routing covers both address families.
- Added a per-config-directory runtime lock and a PID/start-time lease for best-effort orphan recovery.
- Remove generated configs on stop and unexpected child exit, and clean stale candidates on the next launch.

### Fixed

- Detect a sing-box process that exits during its startup-settle window instead of reporting a false successful apply.
- Restore and restart the previous checked configuration when a checked replacement fails during startup.

### Tests and release

- Added lifecycle coverage for stale cleanup, concurrent ownership, orphan recovery, stop cleanup, dual-stack config, and failed-replacement rollback.
- Require release tags to match the project version and publish preview tags as GitHub prereleases.

## [0.1.0] - 2026-08-25

### Added

- WPF rule editor for exact process routing on Windows.
- sing-box v1.13+ TUN configuration builder and managed process runtime.
- Proxy, Direct, and Block rule actions with destination constraints.
- Pre-start `sing-box check` validation and redacted runtime logs.
- DPAPI-protected local password storage and redacted profile export.
- Unit tests for routing configuration, invalid inputs, and secret handling.
- CI, release automation, checksums, security policy, threat model, and contributor documentation.

### Changed

- Replaced the earlier system-proxy-only behavior with an explicit sing-box TUN data plane.
- Removed synthetic connection logs and unsupported UI controls.
