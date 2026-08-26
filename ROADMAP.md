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

## Candidate next work

- [ ] Guided sing-box discovery with version reporting
- [ ] Authenticated local proxy editing in the UI
- [ ] Automated CI integration tests against a pinned real sing-box release
- [ ] Accessible keyboard navigation and high-DPI test coverage
- [ ] Localized UI resources instead of hard-coded strings
- [ ] Signed release artifacts when sustainable signing infrastructure exists

## Explicitly not promised

Proxy chains, provider subscriptions, remote node management, per-connection traffic attribution, and cross-platform support are not scheduled. They require separate design and security review before implementation.
