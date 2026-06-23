# Ali V1 Non-Voice RC Status

Date: 2026-06-23
Branch: `phase-1c-voice-hardening`

## Commit(s)

- `5afb35a` Handle qwen reasoning streams safely

## Working Tree

- Clean at the end of runtime certification.

## Build And Tests

- `dotnet build .\Ali.sln`: PASS, 0 warnings/errors.
- Console harness/tests: PASS, 74/74.

## Security / Trust Store

- Approved Ali local dev signing cert thumbprint: `094F377BC08F776F367A46DB3E091FE6417AF92D`.
- Added/trusted only in `CurrentUser\Root`.
- Not added to `LocalMachine\Root`.
- Signed debug output launched and passed certification.
- Refreshed signed DevRun still hit Smart App Control on `Ali.Infrastructure.dll`; installer-managed signing/repair remains required.

## Runtime Certification

- Ollama standalone: PASS.
- Installed model used: `qwen3-vl:8b`.
- Removed model: `qwen3:14b`.
- Settings Check: PASS.
- Activate local runtime: PASS.
- Local prompt through activated runtime: PASS.
- Raw thinking stripped from user-visible chat: PASS.
- Runtime failure visibility/logging: PASS.

Certified low settings:

```text
Endpoint: http://127.0.0.1:11434/v1/
Model: qwen3-vl:8b
Quantization: Ollama package default / lowest Ali runtime settings
Context: 2048
Output: 128
Temperature: 0
Top-p: 0.1
Streaming: enabled
```

Observed prompt:

```text
User: Say exactly: Ali local runtime ready.
Ali: Ali local runtime ready.
```

Streaming/Stop:

- Streaming UI state was observed as `Streaming local response...`.
- Stop became enabled during the response.
- Stop returned the UI to a usable complete state.
- No duplicate continuation appeared after Stop.

Known qwen behavior:

- `qwen3-vl:8b` may emit hidden reasoning through `delta.reasoning` while `delta.content` is empty.
- Ali now hides reasoning from normal chat.
- If no visible assistant content arrives, Ali reports:
  `Unknown: local model runtime completed without visible assistant content. The model may have spent its output budget on hidden reasoning.`

## Runtime Failure Visibility

Bad endpoint tested:

```text
http://127.0.0.1:59999/v1/
```

Result:

- Settings displayed the refused connection message.
- Endpoint/model/elapsed/failure detail were visible.
- Activate remained disabled.
- Restored valid endpoint afterward and Check passed again.

## Lifecycle Certification

- Settings owned/single-instance: PASS.
- Repeated Settings clicks kept one Settings window: PASS.
- Main app shutdown after Settings activity left no Ali window/process: PASS.
- Ollama may remain running independently; this is not counted as an Ali zombie.

## Feature Certification

- Chat history/persistence/search: PASS via harness and prior manual report.
- Erase History scoped correctly: PASS via harness.
- Correction queue: PASS via harness.
- Explicit memory: PASS via harness.
- Local reminders while Ali is running: PASS via harness-level store/parser behavior.
- Owner visual review: NOT CERTIFIED.
- Voice/mic/Piper live certification: NOT CERTIFIED.

## Forbidden Scope Confirmation

- No command execution added.
- No cloud fallback added.
- No Phase 3 features added.
- No UI redesign.
- No live voice/mic/Piper testing performed in this pass.

## Files Changed In This Pass

- `src/Ali.Infrastructure/Runtime/OpenAiCompatibleLocalModelRuntime.cs`
- `src/Ali.Infrastructure/Runtime/OpenAiStreamParser.cs`
- `tests/Ali.Tests/Program.cs`
- `docs/USER_GUIDE.md`
- `docs/TECH_DEBT_AND_BRIDGES.md`
- `docs/ENGINEERING_NOTES.md`
- `outputs/ALI_V1_NON_VOICE_RC_STATUS_20260623.md`

## Recommended Next Owner Action

1. Fix installer/signing/repair so refreshed DevRun binaries pass Smart App Control without manual dev-cert work.
2. Owner visual review of cockpit, Settings, history/search, corrections, memory, and reminders.
3. Live voice/mic/Piper certification with Chris present.
