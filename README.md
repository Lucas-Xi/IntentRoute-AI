# IntentRoute AI

[![CI](https://github.com/Lucas-Xi/IntentRoute-AI/actions/workflows/ci.yml/badge.svg)](https://github.com/Lucas-Xi/IntentRoute-AI/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Lucas-Xi/IntentRoute-AI?include_prereleases)](https://github.com/Lucas-Xi/IntentRoute-AI/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

IntentRoute AI is an open-source Windows control plane that turns plain-language network intent into locally validated routing-rule drafts. It can use the OpenAI Responses API or an already-running local Ollama model, then hands accepted rules to the same deterministic sing-box TUN configuration pipeline used by the manual editor.

> **Project status: v0.2.0 preview.** IntentRoute AI is useful for testing and early adoption, but it has not yet demonstrated broad production usage. AI output can be incomplete or wrong. Every generated rule is locally validated, added disabled, and must be explicitly enabled by the user.

## Why this project exists

Many Windows applications do not expose useful proxy controls, while hand-authoring process/domain/IP routing rules is error-prone. IntentRoute AI provides two complementary paths:

- A conventional, inspectable rule editor for deterministic manual configuration.
- An optional AI authoring assistant that translates natural language into a bounded, reviewable rule draft.

The application does not capture packets itself. It generates a validated [sing-box](https://github.com/SagerNet/sing-box) v1.13+ TUN configuration, starts and supervises the external sing-box process, and keeps the default route direct unless a rule says otherwise.

## AI workflow

1. Select **OpenAI** or **Ollama (local)**.
2. Enter an intent such as: “Route Chrome and Cursor traffic for GitHub and OpenAI through the proxy; keep everything else direct.”
3. The provider returns a strict structured draft containing process, host/IP, port, protocol, action, rationale, confidence, and warnings.
4. IntentRoute AI treats the result as untrusted input and validates field limits, executable names, domains, CIDRs, ports, protocols, actions, duplicates, and proxy availability.
5. A temporary enabled candidate is passed through `SingBoxConfigBuilder` so disabled-rule filtering cannot make validation a no-op. This preview dry-run is deterministic in-process config construction; it intentionally does not execute an external program.
6. The user reviews the preview and may add the whole draft as **disabled rules**.
7. Enabling remains a separate user action. That state-changing path writes a candidate file and executes `sing-box check -c` before the managed runtime is replaced.

AI never directly enables rules, invokes commands, selects files, installs models, downloads sing-box, or applies an unreviewed configuration.

## Provider setup

### OpenAI

IntentRoute AI reads the user's key at request time from `OPENAI_API_KEY`. The key is not accepted in the app UI and is never written to the application configuration, profiles, logs, exports, or diagnostics.

PowerShell example for the current Windows user:

```powershell
[Environment]::SetEnvironmentVariable('OPENAI_API_KEY', 'your-api-key', 'User')
```

Restart IntentRoute AI after changing the environment variable. The OpenAI request uses the Responses API, strict JSON Schema output, no tools, a bounded timeout/output size, and `store=false`.

### Local Ollama

Install [Ollama](https://ollama.com/), start its local service, and install a model separately. For example:

```powershell
ollama pull qwen3:8b
```

IntentRoute AI queries only literal HTTP `127.0.0.1` (the default) or `::1`. v0.2.0 rejects hostnames, other loopback addresses, credentialed endpoints, HTTPS, LAN, and public Ollama endpoints; disables proxy use and redirects for these requests; and never pulls a model or launches Ollama automatically. The UI lists models already installed through `GET /api/tags`.

## AI data boundary

| Data | OpenAI | Local Ollama |
|---|---:|---:|
| User-entered intent | Sent | Sent to loopback only |
| Static rule schema/instructions | Sent | Sent to loopback only |
| Proxy username/password | Never | Never |
| Proxy server address | Never | Never |
| Existing rules/configuration | Never | Never |
| Runtime logs | Never | Never |
| Full process list or paths | Never | Never |
| API key | Authorization header only | Not applicable locally |

OpenAI API data handling is governed by the user's OpenAI account and current API policies. `store=false` is an application-level request setting, not a promise that no provider-side security or abuse-monitoring processing exists. Ollama mode keeps the application request on loopback, but the privacy and behavior of the installed model/runtime remain the user's responsibility.

## Current routing capabilities

- Process-aware Proxy / Direct / Block rules.
- Optional exact-domain and `*.suffix` filters.
- IPv4/IPv6 address and CIDR filters.
- Single ports and ascending port ranges.
- TCP, UDP, or Both.
- Explicit priority ordering.
- IPv4 and IPv6 TUN addresses with strict routing.
- Atomic candidate configuration, `sing-box check`, startup-settle verification, and rollback.
- Exclusive runtime ownership plus PID/start-time orphan recovery.
- Passwords protected at rest with Windows DPAPI `CurrentUser`.
- Password-free profile exports and bounded/redacted runtime logs.

IntentRoute AI does **not** provide a proxy node, VPN account, packet driver, bundled AI model, OpenAI API key, or sing-box binary.

## Install a preview build

1. Download `IntentRoute-AI-v0.2.0-win-x64.zip` and its `.sha256` file from [Releases](https://github.com/Lucas-Xi/IntentRoute-AI/releases).
2. Verify the checksum.
3. Download the official Windows x64 sing-box v1.13+ archive separately.
4. Put `sing-box.exe` beside `IntentRouteAI.exe`, add it to `PATH`, or set `INTENTROUTE_SING_BOX` to its full path. The legacy `PROXYMANAGER_SING_BOX` variable remains supported for upgrades.
5. Ensure an existing local SOCKS5 service is listening on `127.0.0.1:10808`, or configure another local port.
6. Run `IntentRouteAI.exe` as administrator. TUN creation requires elevation.

The self-contained release targets Windows x64 and does not require a separate .NET runtime.

## Configuration and upgrade migration

Current data is stored under `%APPDATA%\IntentRouteAI`. On first v0.2.0 launch, if the new directory has no current configuration, the application copies only `config.json` and `*.profile.json` from `%APPDATA%\ProxyManager`. Copying holds a per-directory exclusive migration lock and uses an in-progress marker plus atomic per-file moves, so an interrupted migration retries only missing known files on the next launch and never overwrites a completed copy. It deliberately does not copy generated sing-box configs, runtime leases, locks, or candidates, and it never deletes the legacy directory automatically.

Proxy passwords are protected at rest with DPAPI `CurrentUser`. The generated `%APPDATA%\IntentRouteAI\sing-box.generated.json` necessarily contains any configured credential in plaintext while sing-box is running. The application removes it on stop, clean exit, and unexpected child exit; the next launch performs bounded orphan recovery and stale-artifact cleanup. Cleanup remains best effort under disk, ACL, administrator, or abrupt-crash interference.

## Build and test

Requirements for source builds:

- Windows 10/11 x64
- .NET 8.0.424 SDK (pinned by `global.json`)
- PowerShell 7 recommended

```powershell
./scripts/test.ps1
./scripts/check-vulnerabilities.ps1
./scripts/build.ps1
```

Provider tests use mocked HTTP handlers. They do not require an OpenAI key, a paid API call, a running Ollama service, or a downloaded local model.

## Architecture and security

- [Architecture](docs/ARCHITECTURE.md)
- [Threat model](docs/THREAT_MODEL.md)
- [Security policy](SECURITY.md)
- [AI v0.2.0 approved design](docs/plans/2026-08-25-intentroute-ai-design.md)
- [Codex for Open Source readiness](docs/CODEX_FOR_OSS_READINESS.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

Please report vulnerabilities privately through [GitHub Security Advisories](https://github.com/Lucas-Xi/IntentRoute-AI/security/advisories/new). Do not include real API keys, proxy credentials, generated configurations, or unredacted logs in an issue.

## Known limitations

- Preview quality; compatibility varies by Windows, firewall, endpoint-security, and sing-box versions.
- AI suggestions are not authoritative and may omit service domains or misunderstand intent.
- No autonomous activation, traffic self-healing, live connection attribution, arbitrary executable wildcards, or remote Ollama endpoints.
- No proxy node distribution or connectivity guarantee.
- `sing-box check` validates configuration syntax/schema, not adapter creation or upstream reachability.

## 中文快速说明

IntentRoute AI 是一个 Windows 开源 AI 分流控制工具。你可以用中文描述“哪个程序的哪些域名应该代理、直连或阻止”，再由 OpenAI 或本机 Ollama 生成结构化草案。软件会在本地执行严格校验，草案写入后默认禁用，必须由你再次确认启用。

OpenAI 模式只从 `OPENAI_API_KEY` 环境变量读取用户自己的密钥；Ollama 模式只允许字面量 `127.0.0.1` 或 `::1`（默认连接 `127.0.0.1:11434`）。两种模式都不会发送代理密码、现有规则、运行日志或完整进程列表。没有配置 AI 时，所有手工分流功能仍可正常使用。

## License

IntentRoute AI is licensed under the [MIT License](LICENSE). sing-box is a separate GPL-licensed program and is not included in this repository or its release archives; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
