# Architecture

## Supported path

`ProxyManager.Standalone` is a .NET 8 WPF control plane. It does not capture packets itself.

1. The UI edits an `AppConfig` model.
2. `AppConfigStore` persists settings atomically and protects passwords with DPAPI.
3. `SingBoxConfigBuilder` validates supported semantics and produces full and redacted JSON representations.
4. `SingBoxRuntime` discovers a separately installed sing-box executable.
5. A candidate configuration is written atomically and checked with `sing-box check -c`. This validates schema and configuration, not live network reachability.
6. A checked candidate is promoted and the managed process is replaced. If the replacement exits during its startup-settle window, ProxyManager atomically restores and restarts the previous checked configuration when one exists.
7. sing-box creates dual-stack IPv4/IPv6 Windows TUN routes and applies process/destination route rules.

## Trust boundaries

| Boundary | Responsibility |
| --- | --- |
| WPF input to config builder | Validate process patterns, hosts, IP/CIDR, ports, protocols, proxy references, and server endpoints |
| Config builder to file system | Never log full JSON; write atomically to the per-user application directory |
| File system to sing-box | Execute an explicit discovered path without a command shell; validate before run |
| sing-box output to UI | Redact common password, secret, token, and credential patterns |
| Profile export | Remove passwords rather than exporting DPAPI ciphertext |
| Runtime ownership | Hold an exclusive per-config-directory lock and record the child PID, start time, and executable path without credentials |

## Failure behavior

Invalid replacement configuration does not intentionally terminate a healthy managed process. A checked replacement that immediately fails to run triggers a best-effort rollback to the prior generated configuration and process. Process output is bounded in memory.

On stop, normal shutdown, or an unexpected managed-child exit, ProxyManager removes the generated configuration. On the next launch after an application or OS crash, it verifies a recorded orphan by PID and start time, checks the executable path when Windows permits it, terminates that process tree, and removes stale configs/candidates. Recovery and deletion remain best effort under administrator interference, filesystem failure, or corrupted state.

## Dependency boundary

sing-box is a separate program selected and installed by the user. It is not linked, vendored, downloaded, or redistributed by ProxyManager. Upstream behavior and licensing remain the responsibility of the sing-box project.
