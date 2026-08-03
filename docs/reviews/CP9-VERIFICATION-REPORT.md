# CP9 runtime and provider neutrality verification report

Date: 2026-08-03

Branch: `codex/checkpoint-9-runtime-experience`

Starting commit: `d6f91d1`

## Result

CP9 implements settings-driven local and remote OpenAI-compatible runtime plumbing for LM Studio,
Lemonade, Ollama, llama.cpp, and explicitly approved remote HTTPS endpoints. The implementation
keeps one model-driven Agent Framework loop and adds a fail-closed, functionally probed protocol
binding for autonomous engineering.

## Implemented surfaces

- Dynamic provider inventory with provider-reported context/output, tool, vision, tokenizer,
  rolling-window, and thinking-convention metadata.
- Explicit operator controls for provider, endpoint, model, budgets, streaming, vision, native
  tools, thinking convention, thinking toggle, and reasoning effort.
- Remote HTTPS opt-in restricted to the custom OpenAI-compatible engine, with redirects disabled
  and normal Windows certificate validation retained.
- Remote API-key storage separated from runtime settings using Windows current-user DPAPI, with an
  environment-variable alternative.
- Functional native-tool and exact JSON-schema compatibility probes with tri-state reasoning,
  streaming, and vision observations.
- Persisted capability profiles and exact protocol/capability/tokenizer/context/rolling-window/
  generation binding identities.
- Planning-time enforcement that rejects unprobed, inconsistent, or chat-only engineering
  protocols before another model request.
- Provider-owned model lifetime for LM Studio, Ollama, llama.cpp, and custom endpoints; the existing
  Lemonade-specific owned load/unload readiness lifecycle remains isolated to Lemonade.

## Release verification

Successful gates:

- Release build of `tests/Ali.Framework.Tests/Ali.Framework.Tests.csproj`: succeeded with
  0 warnings and 0 errors.
- CP9-focused Release test gate: 68 passed, 0 failed, 0 skipped.
- `git diff --check`: passed.

The repository-wide Release test run was also executed outside the restricted sandbox. It is not
green at this starting ancestry: the production capability catalog fails initialization because
five expected Roslyn tools are absent (`roslyn_apply_action`, `roslyn_inspect_target`,
`roslyn_list_actions`, `roslyn_preview_action`, and `roslyn_verify_changeset`). That single inherited
catalog mismatch causes a broad cascade across capability, orchestration, and MCP tests. Additional
machine-dependent toolchain/module tests also fail on this workstation. CP9 does not modify the
Roslyn provider/catalog implementation and did not attempt to repair work owned by another
checkpoint lane.

## Security/adversarial checks

- Remote HTTP, URL credentials, unapproved public endpoints, and named local engines pointed at a
  remote host are refused.
- A remote request without a protected/environment API key sends no network request.
- Authorized remote requests use a Bearer header; the canary key is absent from saved settings,
  capability serialization, binding identities, and health text.
- A native-tool setting fails health when the exact typed call is not proven.
- A validated structured-decision profile is accepted only after the exact schema/nonce response.
- Chat-only and mismatched bound profiles are refused for autonomous engineering.
- 8,192-token and 65,536-token contexts produce distinct exact capability identities without a
  model-name rule or global context clamp.

## Deterministic mechanics

See [CP9-DETERMINISTIC-RULE-DISCLOSURE.md](CP9-DETERMINISTIC-RULE-DISCLOSURE.md). No deterministic
English interpretation, routing, task decomposition, relevance selection, tool choice,
continuation decision, or answer construction was added.

## Known limitations and deferred gates

- No live LM Studio, Lemonade, Ollama, llama.cpp, remote provider, GPU, multimodal model, or large
  context generation was available during this pass. Provider behavior is covered by bounded HTTP
  contract tests, not claimed as live-runtime proof.
- Capability probing records manual vision as unknown unless an enabled typed image health probe
  succeeds; CP10 owns the expanded advisory/manual vision experience.
- The shared canonical desktop shortcut was neither modified nor launched. Live integrated desktop
  verification remains deferred to the root CP13 merge as instructed.
- Identity and Viewport source foundations were not modified.
