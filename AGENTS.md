# Repository agent guide

## Scope

The supported product is `ProxyManager.Standalone`, a Windows WPF control plane for a separately installed sing-box v1.13+ executable. Do not introduce or describe a custom packet driver unless one actually exists and is tested.

## Required gates

Run these before proposing a merge:

```powershell
./scripts/test.ps1
./scripts/build.ps1
```

When routing JSON changes, also run `scripts/validate-sing-box.ps1` against a supported sing-box executable.

## Non-negotiable boundaries

- Never commit credentials, generated runtime configurations, binaries, or local profiles.
- Never print the full sing-box configuration because it can contain passwords.
- Keep public diagnostics redacted.
- Do not reintroduce synthetic connection telemetry.
- Do not bundle or automatically download sing-box.
- Reject unsupported routing semantics instead of silently changing their meaning.
- Preserve the currently running sing-box process when a replacement configuration fails validation.
- Treat administrator execution, process launching, and generated config files as security-sensitive changes.

Update README, changelog, tests, and threat model when observable behavior or security boundaries change.
