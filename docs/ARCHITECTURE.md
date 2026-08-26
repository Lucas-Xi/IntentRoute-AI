# Architecture

## Supported path

`ProxyManager.Standalone` is a .NET 8 WPF control plane. It does not capture packets itself.

1. The UI edits an `AppConfig` model.
2. `AppConfigStore` persists settings atomically and protects passwords with DPAPI.
3. `SingBoxConfigBuilder` validates supported semantics and produces full and redacted JSON representations.
4. `SingBoxRuntime` discovers a separately installed sing-box executable.
5. A candidate configuration is written atomically and checked with `sing-box check -c`.
6. Only a valid candidate replaces the managed sing-box process.
7. sing-box creates the Windows TUN route and applies process/destination route rules.

## Trust boundaries

| Boundary | Responsibility |
| --- | --- |
| WPF input to config builder | Validate process patterns, hosts, IP/CIDR, ports, protocols, proxy references, and server endpoints |
| Config builder to file system | Never log full JSON; write atomically to the per-user application directory |
| File system to sing-box | Execute an explicit discovered path without a command shell; validate before run |
| sing-box output to UI | Redact common password, secret, token, and credential patterns |
| Profile export | Remove passwords rather than exporting DPAPI ciphertext |

## Failure behavior

Invalid replacement configuration does not intentionally terminate a healthy managed process. Process output is bounded in memory. On normal shutdown, ProxyManager terminates only the child process it started and removes its generated configuration file.

## Dependency boundary

sing-box is a separate program selected and installed by the user. It is not linked, vendored, downloaded, or redistributed by ProxyManager. Upstream behavior and licensing remain the responsibility of the sing-box project.
