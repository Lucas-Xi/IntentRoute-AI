# IntentRoute AI

IntentRoute AI turns user-authored routing intent into a safely persisted configuration for a separately installed sing-box runtime.

## Language

**Configuration Workspace**:
The trusted owner of the active routing configuration and any proposed replacement while it is being accepted or recovered.
_Avoid_: Config service, settings manager

**Active Configuration**:
The complete routing configuration that has passed local acceptance and is visible to the application.
_Avoid_: Live config, current JSON

**Configuration Candidate**:
A proposed complete routing configuration that is not active until every acceptance condition succeeds.
_Avoid_: Temporary config, draft file

**Configuration Snapshot**:
A detached view of the Active Configuration that callers may inspect without changing the Configuration Workspace.
_Avoid_: Mutable config, config reference

**Recovery Protection**:
The state in which an unreadable Active Configuration is preserved and all replacement actions remain blocked until a recovery copy is available and the user explicitly recovers or resets.
_Avoid_: Fallback mode, auto-reset

**Runtime Approval**:
Current-session authority to execute one exact, user-selected sing-box path. Persisted or imported paths are hints and never carry this authority.
_Avoid_: Trusted config path, remembered approval

**Stale Runtime**:
A preserved sing-box process that still runs an older accepted configuration after the Active Configuration changes but cannot yet be applied.
_Avoid_: Running, healthy current config

**Runtime Identity**:
The executable path and version attached to the currently managed sing-box process. Candidate probe results do not become Runtime Identity until that candidate process actually starts; rollback restores the previous executable identity as well as its generated configuration.
_Avoid_: Selected path, candidate identity

**Canonical Runtime Order**:
The single ordering of enabled rules used by the sing-box builder, rule views, process-candidate display, and policy analysis: priority ascending, creation timestamp ascending, then persisted source order.
_Avoid_: UI order, apparent order

**Policy Intelligence**:
The read-only local module that analyzes a Configuration Snapshot using Canonical Runtime Order and conservative sing-box matching relations. It never observes live traffic, probes a proxy, mutates configuration, or applies a runtime.
_Avoid_: AI scanner, traffic analyzer

**Policy Finding**:
A locally proven or mechanically classified property of the current policy, with a stable display code, severity, affected local rules, evidence, and a human-review recommendation.
_Avoid_: AI opinion, connection event

**Policy Disclosure**:
The closed, allowlisted projection of selected Policy Findings that a user may explicitly send to an AI provider. It contains aggregate counts, finding codes, categories, severities, relations, and affected-rule counts, but no rule identifiers or values.
_Avoid_: Redacted config, policy export

**AI Policy Explanation**:
Untrusted, strictly parsed plain text attached only to Policy Finding codes from the exact Policy Disclosure. It cannot create facts, mutate rules, change severity, or cross the Configuration Workspace seam.
_Avoid_: AI fix, AI policy result
