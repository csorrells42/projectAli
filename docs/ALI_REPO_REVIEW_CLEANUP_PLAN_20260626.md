# Ali Repo Review Cleanup Plan - 2026-06-26

This review covers the current `phase-1c-voice-hardening` working tree after the programming assistant, Visual Studio companion, PDF, streaming response, and runtime advisor upgrades. It is a cleanup map, not a deletion list. Anything marked as a candidate should be verified with tests and owner review before removal.

## Highest-Value Findings

1. `src/Ali.Infrastructure/Coding/LocalCodingToolService.cs` is the largest production file at about 7,998 lines. It now owns command routing, planning, crash recovery, Windows troubleshooting, PDF tools, dotnet runners, Git actions, patch previews, reports, and package planning. The behavior is useful, but the blast radius is too large for fast future changes.
2. `src/Ali.App.Wpf/ViewModels/MainWindowViewModel.cs` is about 4,473 lines. It owns chat, runtime settings, voice, attachments, correction reports, coding permissions, PDF permissions, streaming UI, and settings-window behavior. This is the second-highest stability target.
3. `src/Ali.Core/Coding/CodingToolRequestParser.cs` is about 2,494 lines. The parser has grown into a command catalog embedded in code. A declarative command registry would reduce drift between Ali's self-knowledge, the companion UI, docs, tests, and VSIX commands.
4. `src/Ali.App.VisualStudioExtension/AliCompanionToolWindowControl.cs` is about 1,305 lines. It should be split into UI composition, bridge client, command staging, selection context, and status rendering.
5. `src/Ali.App.WebHelper/Program.cs` is about 1,260 lines. The helper is now valuable enough to deserve endpoint modules, page assets, bridge state, and runtime/Ollama coordination helpers.

## Candidate Abandoned Or Low-Reference Code

- `src/Ali.Infrastructure/Voice/MciWaveAudioRecorder.cs` and `src/Ali.Infrastructure/Voice/MciWaveSpeechPlayer.cs` look like older MCI fallback paths now that NAudio/Piper are the main local voice route. Verify no settings path or emergency fallback depends on them before deleting.
- `src/Ali.Infrastructure/Runtime/DevelopmentLocalModelRuntime.cs` is intentionally still needed as the deterministic bootstrap stub. Do not delete it just because the real local runtime is working.
- `src/Ali.Core/Coding/CodingWorkspacePolicy.cs` and related permission helpers are still important. Low reference count is expected because permission decisions centralize there.
- Test-only fake runtimes, stores, and retrievers should be left alone unless a matching production path is removed.

## Giant-Method And Module Refactor Plan

1. Extract `LocalCodingToolService` into command families:
   - workspace inspection and file read/search
   - planning, roadmaps, execution packets, and crash recovery
   - dotnet, package, and Git runners
   - patch preview and file edit guards
   - PDF/report generation
   - Windows troubleshooting and installer diagnostics
2. Extract `MainWindowViewModel` into smaller controllers/view models:
   - runtime settings and local model activation
   - streaming chat and conversation state
   - voice input/output orchestration
   - permissions and workspace folder settings
   - attachments, correction reports, and source visibility
3. Replace parser growth with a shared command catalog that can feed:
   - parser patterns
   - companion UI buttons
   - Ali ability/self-knowledge answers
   - user manual snippets
   - VSIX command staging
4. Split the VSIX tool window into a thin WPF control plus service classes for bridge calls, document context, and UI state.
5. Split the WebHelper page and bridge code so the HTML/JavaScript asset is not buried inside `Program.cs`.

## Async And Determinism Risks

- WPF `async void` event handlers are mostly normal for UI events, but `MainWindow_OnClosing`, keyboard handlers, local-library scan, and correction flows deserve explicit exception/cancellation audit.
- Several UI operations still use `CancellationToken.None`, including runtime checks, installed-model refresh, source/correction exports, and some library scans. A standard UI operation cancellation scope would make slow or stuck operations easier to stop.
- `SystemHardwareInfoReader` uses short, bounded process probes for `nvidia-smi` and CIM fallback. It is acceptable as a user-clicked advisor, but an async version would avoid any future UI freeze if a vendor tool stalls.
- NAudio stop paths use bounded waits. Keep them, but add driver-hang logging before deeper voice work.
- The runtime advisor estimates memory from model metadata and detected hardware. It is a recommendation tool, not a benchmark; final settings still need a live check and user approval.

## Unfinished Or Explicitly Future Work

- Voice command execution is intentionally blocked in this phase. Typed approval gates remain the safe path.
- Wake word, barge-in, and fully certified voice repair remain separate work.
- PDF OCR, redaction, form editing, annotation editing, encrypted PDFs, image-only PDFs, and layout-preserving arbitrary binary edits remain future work.
- Visual Studio has a real companion tool window now, but actions still route through Ali's local bridge and approval gates. Direct autonomous in-IDE edits/builds remain future work.
- Screenshot-to-bug understanding should be a later vision-model phase with strict evidence capture and owner review.
- Git pull/push should remain explicitly gated and should not be broadened until install and recovery paths are stable.

## Biggest Bang For The Next Cleanup Cycle

1. Build the shared ability/command catalog first. It reduces command drift, improves Ali's self-knowledge, updates docs/UI from one source, and gives the VS companion a cleaner command feed.
2. Split `LocalCodingToolService` by command family. This gives the largest reliability win for future programming upgrades.
3. Split `MainWindowViewModel` around runtime, voice, conversation, and permissions. This reduces UI regression risk and makes streaming/voice easier to reason about.
4. Add operation cancellation and progress records for long-running UI tasks. This directly supports crash recovery and slow-computer diagnosis.
5. Only then remove confirmed dead voice fallbacks or helper code. Deletion should follow green tests, not lead the cleanup.

## Runtime Advisor Addition

The Runtime tab now has a recommendation path that uses the selected model profile and detected hardware instead of assuming a historical Qwen model. On Chris's current development machine the selected coding model is `ali-deepseek-coder-v2:16b-low`, and NVIDIA VRAM should be read from `nvidia-smi` when available because CIM can under-report some GPUs. The report presents Low, Medium, and Aggressive strategies with context, output, sampling, streaming, vision, estimated RAM/VRAM pressure, and tradeoffs.
