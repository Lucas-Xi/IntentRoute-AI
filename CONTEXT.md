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
