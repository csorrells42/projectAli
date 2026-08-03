# CP12 deterministic-rule disclosure

Date: 2026-08-03
Branch: `codex/checkpoint-12-engineer-certification`

## Scope boundary

CP12 adds deterministic evaluation-fixture, evidence-retention, and scoring mechanics under `src/Modules/EngineeringCertification`. These rules run only when the engineer certification runner is explicitly invoked. They do not inspect ordinary user requests, choose a runtime for a user request, select tools for the model, decompose user work, or change Ali's authoritative Agent Framework loop.

The authoritative-loop integration boundary accepts typed tool, primitive, recovery, and token receipts. Certification never derives those facts from generated answer prose.

## Disclosed rules

1. Suite construction
   - Version `engineering-certification-v1` contains exactly 100 tasks: ten fixture families with ten stable variants each.
   - Every candidate receives the same ordered task definitions and immutable fixture contents.
   - Suite validation rejects fewer than 100 or more than 200 tasks, duplicate task ids, unsafe fixture paths, missing fixtures/tools, and invalid time/token budgets.
   - A SHA-256 digest covers the version, ordered tasks, fixture bytes, required typed tool ids, resource budgets, and failure-injection flags. Resume requires the exact digest.

2. Candidate discovery
   - Enabled configured OpenAI-compatible runtime endpoints are queried through their bounded `/models` inventory.
   - Candidate identity is SHA-256 over configured runtime id, exact endpoint, and exact returned model id.
   - Inventory parsing accepts documented provider-shaped arrays and the `data`, `models`, or `all_models_loaded` collections with typed id/name fields.
   - Results are deduplicated by exact binding digest and ordered by runtime id then model id only for repeatability.
   - No production rule contains GPT-OSS, Gemma, Qwen, Devstral, DeepSeek, or any other candidate model family/name.
   - A resumed run freezes its original candidate bindings. Inventory drift requires a new run id instead of silently changing a comparison.

3. Mechanical score
   - Applicable score components have equal weight and normalize to 100 percent.
   - Components are: Release build success; exact typed primitive id; zero compiler-identified hallucinated API diagnostics; zero newly introduced Roslyn errors/warnings; unit-test success; presence of every required typed tool id; typed recovery after an injected first-tool failure; measured completion time within budget; and typed token usage within budget.
   - Recovery is applicable only to the 20 tasks whose stable variant is 1 or 6.
   - Missing token evidence fails the token component; it is not estimated.
   - Hallucinated API evidence is limited to compiler errors `CS0103`, `CS0117`, `CS1061`, `CS0234`, and `CS0246`. Other Roslyn errors/warnings still affect the introduced-diagnostics component.
   - Comparison rows sort by mean score descending, then exact model id for stable presentation. This order is report presentation only and cannot route user requests.

4. Isolation, cancellation, recovery, and bounds
   - Fixtures are materialized from the versioned suite into a new candidate/task/attempt workspace below the exact run root. Canonical source is never copied into or used as a benchmark workspace.
   - Absolute paths, `..` traversal, boundary escapes, and reparse-point run directories are refused.
   - Compact readable-plus-hash directory segments keep spawned Windows test hosts below common process-path limits without weakening identity.
   - Caller cancellation is propagated. Each task also has a five-minute budget; each independent `dotnet` command has a two-minute limit.
   - Infrastructure exceptions may retry only within the configured one-to-three attempt bound. Ordinary low model scores are not retried or reinterpreted.
   - Bounds are 32 configured runtimes, 64 discovered candidates, 100-200 tasks, three attempts, 65,536 characters per agent/baseline/verifier raw transcript section, 131,072 characters per process stream, 524,288 bytes per stored evidence JSON file, and 16,384 typed tokens per current task.
   - Durable evidence is written after each completed task. Resume skips only evidence whose suite digest, candidate binding, and task id match exactly.

5. Reports
   - The runner retains a suite snapshot, frozen candidate inventory, isolated workspace marker, per-task JSON and raw transcripts, per-candidate JSON/Markdown reports, and JSON/Markdown comparisons.
   - Every comparison states that the mechanical result is not a user-request routing rule and does not guarantee general intelligence, safety, or fitness for unrelated work.

## Effects

These rules can affect only certification task admission, candidate enumeration for a certification run, fixture workspace creation, certification retries/resume, evidence retention, and certification scores/reports. They cannot change ordinary Ali answers, runtime selection, model prompts outside a certification task, permissions, tools, Identity, Viewport, or canonical project source.
