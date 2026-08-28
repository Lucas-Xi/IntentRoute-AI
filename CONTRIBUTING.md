# Contributing to IntentRoute AI

Thank you for helping make Windows application routing more transparent and reliable.

## Before opening work

- Search existing issues and discussions.
- For a bug, include Windows version, sing-box version, IntentRoute AI version, AI provider/model when relevant, expected behavior, actual behavior, and minimal reproduction steps.
- Redact proxy credentials, tokens, personal paths, IP addresses that identify people, and private hostnames.
- For a larger feature or security-boundary change, open an issue before investing in an implementation.

## Local development

Requirements: Windows 10/11, the .NET 8.0.424 SDK pinned by `global.json`, and optionally sing-box v1.13+ for runtime schema validation.

```powershell
./scripts/test.ps1
./scripts/build.ps1
./scripts/validate-sing-box.ps1 -SingBoxPath C:\path\to\sing-box.exe
./scripts/test-pinned-sing-box.ps1 -SingBoxPath C:\path\to\sing-box.exe
```

CI also runs `test-pinned-sing-box.ps1` without a path. That explicit test command downloads the official v1.13.19 Windows archive into the runner temp directory, verifies the pinned SHA-256, validates representative production-builder output, and deletes the test dependency. It does not change the product rule that sing-box must be installed separately and is never bundled or automatically downloaded by IntentRoute AI.

## Pull requests

1. Keep changes focused and explain the user-visible behavior.
2. Add or update tests for routing, validation, persistence, and secret redaction.
3. Do not add synthetic connection logs or claim traffic interception without a real data-plane test.
4. Do not bundle sing-box or another network binary.
5. Do not log full configurations, passwords, or credentials.
6. Run the local gates and complete the pull request checklist.
7. Provider tests must use mocked HTTP handlers; never add a real API key, paid API call, model download, or network dependency to CI.
8. Treat AI output as untrusted data. Do not weaken strict schema parsing, local semantic validation, preview, or the separate enable action.
9. Treat Route Decision Simulation as static what-if only. Never label a simulated result as observed traffic, and keep hypothetical queries, rule traces, and local identifiers out of provider requests.

Maintainers may ask for changes when a contribution expands administrator privileges, process execution, configuration file exposure, or network-routing scope.

## Commit style

Clear conventional-style subjects are encouraged, for example:

- `feat: add IPv6 CIDR validation`
- `fix: preserve running instance after invalid config`
- `docs: document TUN recovery steps`

## Adding or changing localized strings

The UI is bilingual (Chinese-neutral plus English) through hand-maintained resx files
and a hand-written accessor — there is no Visual Studio designer step, so CI builds
straight from the repository.

1. Add the key with its Chinese value to `ProxyManager.Standalone/Localization/Strings.resx`
   and the English value to `Strings.en.resx` in the same change. Escape XML entities
   (`&lt;` for `<`) — a raw `<` breaks the build.
2. Add a property to `Strings.cs`: `public static string KeyName => GetString(nameof(KeyName));`
3. Reference it from code behind via the `Strings` alias or from XAML via
   `{x:Static loc:Strings.KeyName}`.
4. Both files must contain exactly the same key set with non-empty values; the parity
   and non-empty tests fail otherwise. Duplicate `<data>` names are not caught by the
   toolchain — pick a unique key.
5. Tests that assert on a localized message must derive the expected text from
   `Strings` (or a culture-stable fragment such as a format suffix), never a
   hard-coded Chinese literal — CI runs under en-US while your machine may not.
6. By design, Policy Intelligence finding titles and the persisted default proxy
   name stay Chinese; do not localize them without a maintainer discussion.

By contributing, you agree that your contribution is licensed under the MIT License.
