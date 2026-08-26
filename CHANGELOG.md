# Changelog

All notable changes are documented here. The project follows semantic versioning while the public API and behavior are still in preview.

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
