# Changelog

All notable changes are documented here. The project follows semantic versioning while the public API and behavior are still in preview.

## [Unreleased]

### Fixed

- Include the WPF native runtime libraries in the self-contained single-file publish so the packaged executable can create its main window instead of terminating during native window subclass initialization.
- Render rule rows with their application, condition, routing mode, enabled state, and creation time instead of the model type name.
- Reject malformed UTF-8 configuration bytes and profile imports instead of accepting replacement characters that could later overwrite the original file.
- Show a yellow stale-runtime state when a replacement fails but the previously applied sing-box process remains healthy.

### Added

- Added a Settings readiness panel that reports the resolved sing-box path, probes `sing-box version`, and requires a recognized v1.13+ release before configuration checking or process launch.
- Added an explicit file picker for a separately installed sing-box executable without bundling, downloading, or installing it.
- Added authenticated local proxy editing for SOCKS5, HTTP, and HTTPS listeners with DPAPI-protected passwords.
- Added a bounded local TCP port check that sends no proxy credentials and does not claim protocol, authentication, or internet reachability success.
- Added guided recovery actions for an unreadable configuration: open the data directory, import a valid configuration, or explicitly reset.

### Changed

- Restrict supported proxy endpoints to literal loopback IP addresses; hostnames, LAN addresses, public addresses, and IPv4-mapped IPv6 forms are rejected.
- Drive the runtime status indicator from real probing, checking, starting, running, failed, and stopped states instead of a fixed green indicator.
- Show a clear dialog when another IntentRoute AI instance already owns the sing-box runtime lock.

### Security

- Preserve an unreadable `config.json` byte-for-byte, create a timestamped recovery copy when possible, block every configuration save path, and skip sing-box application until the user explicitly recovers or resets.
- Treat malformed or undecryptable `dpapi:` proxy passwords as an unusable configuration instead of silently replacing them with empty credentials.
- Fail closed when sing-box version output is missing, unreadable, timed out, or older than v1.13; no candidate configuration is written and no child process is started.
- Never execute a sing-box candidate found through environment variables, the application directory, or `PATH` until the user explicitly approves the exact file with the Settings file picker.
- Treat saved, migrated, and imported sing-box paths as unapproved on every elevated launch; only a file reselected in the current session may run `version`, `check`, or `run`.
- Validate rule imports against the complete candidate configuration before atomically replacing the current config; unsupported semantics no longer persist before runtime rejection.
- Treat null entries inside rules, proxy servers, or proxy chains as an unusable configuration instead of allowing a startup `NullReferenceException` outside the recovery path.
- Cancel and drain first-load/model-provider work before disposing providers during window shutdown, preventing a managed crash when the published app is closed immediately after launch.

### Tests

- Added coverage for corrupt JSON preservation, DPAPI failure, save blocking, explicit reset, loopback-only endpoint validation, local TCP checks, sing-box version compatibility, and prevention of check/start on unsupported versions.
- Added invalid UTF-8 preservation and unapproved discovery tests, plus a build-time package-content gate that rejects bundled sing-box and generated runtime files.
- Added null-collection-entry recovery coverage for rules, proxy servers, and proxy chains.
- Added a Windows CI and release smoke gate that starts the published single-file WPF executable, verifies its main window title, and requires a clean normal close.

## [0.2.0] - 2026-08-26

### Added

- Rebranded the product and release artifacts as IntentRoute AI.
- Added a provider-neutral AI rule assistant with OpenAI Responses API and literal `127.0.0.1`/`::1` local Ollama support.
- Added strict structured-output parsing, bounded responses, provider error categories, and mocked HTTP test seams.
- Added deterministic AI draft validation for executable names, domains, CIDRs, ports, protocols, actions, duplicates, limits, and enabled proxy availability.
- Added an all-or-nothing preview and explicit acceptance flow that persists generated rules disabled without restarting sing-box.
- Added non-destructive migration of known configuration/profile files from `%APPDATA%\ProxyManager` to `%APPDATA%\IntentRouteAI`.
- Added an exclusive migration lock and interrupted-migration marker so concurrent or partial legacy copies safely retry without overwriting files already copied.

### Security

- OpenAI requests use `store=false`, no tools, a strict schema, and an API key read only from `OPENAI_API_KEY` at request time.
- Ollama requests accept only literal `127.0.0.1` or `::1`, reject credentialed endpoints, and disable system proxy use and redirects.
- Neither provider receives proxy credentials, proxy endpoints, existing rules, logs, filesystem paths, or the full process inventory.
- AI rules are temporarily enabled only in a cloned dry-run so the existing builder validates their actual semantics before disabled persistence.
- Unsupported protocol/action values are rejected at the strict parser boundary, and all projects compile with warnings treated as errors under the pinned .NET 8.0.424 SDK.

### Tests

- Expanded the suite beyond its original 15 cases with provider contracts, secret redaction, malformed/unexpected output, exact loopback enforcement, parser enum rejection, network-filter validation, interrupted/concurrent migration recovery, and disabled-rule persistence.

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
