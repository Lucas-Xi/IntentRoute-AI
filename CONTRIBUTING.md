# Contributing to IntentRoute AI

Thank you for helping make Windows application routing more transparent and reliable.

## Before opening work

- Search existing issues and discussions.
- For a bug, include Windows version, sing-box version, IntentRoute AI version, AI provider/model when relevant, expected behavior, actual behavior, and minimal reproduction steps.
- Redact proxy credentials, tokens, personal paths, IP addresses that identify people, and private hostnames.
- For a larger feature or security-boundary change, open an issue before investing in an implementation.

## Local development

Requirements: Windows 10/11, .NET 8 SDK, and optionally sing-box v1.13+ for schema validation.

```powershell
./scripts/test.ps1
./scripts/build.ps1
./scripts/validate-sing-box.ps1 -SingBoxPath C:\path\to\sing-box.exe
```

## Pull requests

1. Keep changes focused and explain the user-visible behavior.
2. Add or update tests for routing, validation, persistence, and secret redaction.
3. Do not add synthetic connection logs or claim traffic interception without a real data-plane test.
4. Do not bundle sing-box or another network binary.
5. Do not log full configurations, passwords, or credentials.
6. Run the local gates and complete the pull request checklist.
7. Provider tests must use mocked HTTP handlers; never add a real API key, paid API call, model download, or network dependency to CI.
8. Treat AI output as untrusted data. Do not weaken strict schema parsing, local semantic validation, preview, or the separate enable action.

Maintainers may ask for changes when a contribution expands administrator privileges, process execution, configuration file exposure, or network-routing scope.

## Commit style

Clear conventional-style subjects are encouraged, for example:

- `feat: add IPv6 CIDR validation`
- `fix: preserve running instance after invalid config`
- `docs: document TUN recovery steps`

By contributing, you agree that your contribution is licensed under the MIT License.
