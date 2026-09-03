# Codex for Open Source 申请草稿（本地文件，勿提交）

> 用途：为 https://openai.com/form/codex-for-oss/ 预填内容。
> 依据：`docs/CODEX_FOR_OSS_READINESS.md`（仓库内公开的诚实边界）+ 2026-08-27 仓库实况。
> 原则：所有采用数据以申请当日 GitHub 公开数据为准，不估算、不夸大。

## 一、程序要点

- 入口：https://openai.com/form/codex-for-oss/ （程序页：https://developers.openai.com/community/codex-for-oss ）
- 待遇：6 个月 ChatGPT Pro with Codex、API credits（用于 PR review / 维护者自动化 / 发布流程等核心 OSS 工作）、视情况获得 Codex Security。
- 评审考量：仓库使用度、生态重要性、积极维护证据、申请人在项目中的角色、项目容量。

## 二、表单字段与拟填内容（英文可直接粘贴）

**Name** — Vincent Xi（GitHub: Lucas-Xi）

**Email** — （填你的常用邮箱，建议与 GitHub 账号一致）

**GitHub repository URL** — https://github.com/Lucas-Xi/IntentRoute-AI
（已公开 PUBLIC ✓，默认分支 main ✓）

**Your role** — Primary maintainer
（依据：仓库唯一维护者；全部提交由本人完成；`.github/CODEOWNERS` 登记本人；拥有全部写入/合并权限。）

**Why does the repository qualify?** （诚实框架，二选一或合并）

> IntentRoute AI is a Windows control plane for sing-box that turns plain-language routing
> intent into locally validated per-process routing rules, with optional OpenAI/Ollama
> drafting behind a deterministic validation and human-confirmation boundary. I am the
> primary and sole maintainer: I authored all commits, own merge rights, and maintain
> CI (locked-dependency Windows pipeline, real sing-box v1.13.19 validation gate,
> vulnerability checks, packaged-WPF smoke tests), tagged releases with SHA-256 checksums,
> a documented threat model, and strict privacy canary tests for every AI-provider
> boundary. The repository is young (first public commit August 2026) and does not yet
> have broad adoption; I am applying on the strength of active daily maintenance,
> complete release/security infrastructure, and a documented public-interest case
> (inspectable per-process routing for Windows users who need it), not on usage numbers.

**How do you plan to use credits/Codex?** （对应仓库真实缺口）

> - Codex-assisted review of security-sensitive runtime code (process launching, generated
>   sing-box configuration, runtime approval and rollback paths) before every release.
> - Expand automated coverage for the remaining roadmap items: AppService/runtime-error
>   localization and mixed-DPI visual validation (window-layer localization shipped
>   through v0.6.0).
> - Repeatable security and threat-model audits of provider-boundary code (Policy
>   Disclosure, redaction, DPAPI handling) as a second reviewer.
> - Issue triage: drafting reproduction steps, summarizing duplicate reports, and
>   preparing scoped fix proposals for human approval.
> - Release workflow verification: cross-checking changelog, roadmap, and generated
>   configuration semantics across tagged releases.

## 三、提交申请前必须完成的证据清单

- [x] 仓库公开、描述与 topics 齐全（dotnet/wpf/sing-box/tun/proxy/ai/…）
- [x] MIT LICENSE、CONTRIBUTING、CODE_OF_CONDUCT、SECURITY、SUPPORT、issue/PR 模板（社区健康度 100%）
- [x] CI 全绿（windows-latest：锁定还原 → 测试 → 真实 sing-box 校验 → 漏洞扫描 → 发布 → WPF 冒烟 + UIA 键盘断言）
- [x] v0.1.0–v0.9.0 十个预发布归档 + SHA-256 校验和（v0.9.0：包内嵌未签名构建溯源清单 provenance.json——版本/提交/构建者/SDK/22 项依赖含哈希，包校验强制要求）
- [x] 申请当日截图/记录 stars、forks、contributors、下载量（如实填写；当前为 0/新项目也照实）
  - 2026-08-28 快照：**58 stars / 0 forks / 0 watchers / 0 open issues**（无 issue/PR 待响应）；下载量 v0.1.0=4、v0.1.1=2、v0.2.0=5、v0.3.0–v0.9.0=0；贡献者 1 人（本人）；代码库零 TODO/FIXME；依赖无已知漏洞

## 四、诚实边界（来自 CODEX_FOR_OSS_READINESS.md，申请时不得违反）

- 项目 2026-08 才公开，尚未证明广泛采用——不得暗示用户量。
- sing-box 提供 TUN 数据平面，是独立项目；OpenAI/Ollama 仅提供可选推理，AI 不自主控制数据平面。
- 路由决策模拟是静态本地策略推理，不是连接遥测/DNS 证据。
- 目前维护者仅一人，除非仓库活动后续证明 otherwise。
- 采用/贡献者/发布影响等数据一律取自申请时的 GitHub 公开证据。

## 五、下一步

1. ~~v0.3.0 发布被本地 Mimosa 预提交门禁阻塞~~ 已解决（2026-08-27：canary 测试随机化、进程启动改为实例写法、smoke 诊断路径限定绝对路径；四提交 + tag + 推送全部通过，Release/CI 双绿）。Mimosa 提示本地扫描覆盖不完整，建议后续自行运行一次完整深度审计。
2. 抓取当日 GitHub 数据（stars/forks/contributors/下载量），更新本文件第三节勾选项。
3. 打开表单，按第二节内容填写提交。
