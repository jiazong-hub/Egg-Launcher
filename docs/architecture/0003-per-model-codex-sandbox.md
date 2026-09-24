# Architecture decision 0003: per-model Codex sandbox settings

Status: adopted in the local v0.9.2 version; named Codex permission profiles remain beta.

## Decision

Store optional Codex command-sandbox settings on each `ModelProfile`, separately from llama.cpp runtime arguments. Profile schema version 7 adds `SandboxSettings`:

- Network access can inherit Codex's existing setting, be disabled, or allow full outbound access.
- Additional absolute paths can be granted read-only or read/write access.
- A null value means the profile has no sandbox override. Loading or saving an untouched legacy profile must not change Codex sandbox settings.

Changing llama.cpp defaults does not change sandbox permissions. Restoring llama.cpp defaults also leaves sandbox settings alone.

## Config transaction

When a selected model has saved sandbox settings, Local mode temporarily selects the named Codex permission profile `egg_launcher_active`. The profile extends the user's existing default permission profile, or the normal `:workspace` profile when no explicit baseline exists. It adds only the model's path grants and selected network override.

The transaction snapshots and owns only these sandbox fields while Local mode is active: `default_permissions`, `sandbox_mode`, `[sandbox_workspace_write]`, and `permissions.egg_launcher_active`. Legacy sandbox fields are removed while the named permission profile is active because Codex does not support combining the two formats. Existing writable roots and an explicitly configured legacy network value are carried into the generated profile. Switching to a model with no saved sandbox settings restores the pre-Local sandbox values; returning to OpenAI restores them as well.

`approval_policy` and `approvals_reviewer` remain outside the ownership set. The launcher does not configure or infer approval behavior.

## Safety boundaries

- A pre-existing `permissions.egg_launcher_active` definition is a conflict; it is never overwritten.
- Codex full-disk access (`:danger-full-access`) cannot be used as a parent for a named profile. The transaction stops rather than silently narrowing or broadening it.
- Legacy temporary-directory exclusion flags cannot be represented by the named-profile format. If enabled, the transaction stops rather than dropping them.
- Full network access is a single profile setting. No per-domain allowlist is generated. Codex proxy behavior and administrator policy still apply.
- Codex named permission profiles are a beta configuration feature; the target ChatGPT Desktop/Codex version must support them.

## Manual acceptance

Before a public release, verify saved and legacy profiles, model A-to-B switching, returning to OpenAI, full network on/off, read-only and writable paths, and recovery after an interrupted transaction. Confirm the approval mode remains unchanged throughout. The local v0.9.2 tag does not assert that this matrix has passed.
