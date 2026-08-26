# Changelog

All notable changes are documented here. The project follows semantic versioning while the public API and behavior are still in preview.

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
