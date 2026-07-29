# Potential Upgrades

These are optional improvements to evaluate only after the current stable behavior demonstrates a concrete need. They are not committed runtime requirements.

## Committed post-framework priority queue

1. **Completion integrity:** Fix responses that promise to create or return something but terminate without a tool result or deliverable. Fix full authoritative inventories and other long structured answers being clipped or silently reduced (observed: 102 registered tools became a 25-row table). Reject unsupported incapability responses when the live registry contains the requested capability; the exact regression request is a C# WPF Tic-Tac-Toe app created on Desktop, built, verified, and run with normal approvals. Add output-limit, continuation, complete-table, promised-action, capability-inspection, and evidence-backed delivery tests.
2. **Mem0 reliability:** Immediately harden per-user durable memory after the Agent Framework upgrade. Verify approval-to-write, persistence, identity isolation, startup recovery, recall-before-search, correction, deletion, and transfer-folder behavior.
3. **Google-quality live search:** Immediately after Mem0, select and integrate a current supported direct Google search API/backend, expose its configuration and health in Settings, retain provider fallback, and repair the Software Engineering Radar workflow. Measure relevance, latency, quotas, and cost with Ali-specific queries before making it the default.

## Residual C# capability audit

Validate these against the live registry before implementation; they came from a model self-assessment and may understate existing Roslyn and Visual Studio features:

- Interactive C# REPL/scripting with retained state.
- Authenticated NuGet/private-feed support without exposing credentials to the model.
- Mixed managed/native debugging and unmanaged memory inspection.
- Reusable Docker, Azure Functions, and serverless delivery templates.
- Dedicated OWASP-oriented .NET security rules and evidence.
- IDE integration gaps involving non-code assets and project-system operations.
- Dynamic IL/JIT inspection only if a real user workflow justifies the risk and complexity.

## Deferred Python engineering big-guns review

- **Status:** Deferred by user request until after the Microsoft Agent Framework agents review.
- **Purpose:** Revisit the high-capability Python analysis, refactoring, testing, debugging, profiling, packaging, security, documentation, and runtime libraries that were deliberately left out of the first shared LSP/DAP provider pass.
- **Architecture:** Add capabilities through the shared language-provider and MCP contracts wherever practical; do not rebuild a separate Python-only coordinator or contaminate the stable editor-independent coding foundation.
- **Adoption rule:** Benchmark and test each candidate, prefer complementary tools over duplicated engines, keep heavyweight services lazy or on-demand, and preserve Ali's current coding capabilities and GPU budget.

## CPU-only result reranking

- **Status:** Deferred; current Nomic, Qdrant, Tree-sitter, and ripgrep retrieval is working.
- **Purpose:** Reorder a small, bounded candidate set when several local-document or code results are similarly relevant.
- **Scope:** Local Knowledge and code retrieval only. Keep personal-memory recall on its current low-latency path.
- **Resource policy:** CPU and system RAM only; do not consume additional GPU VRAM.
- **Candidate implementations:** FastEmbed `TextCrossEncoder`, FlashRank, or a Lemonade reranker explicitly configured with the CPU llama.cpp backend.
- **Safety requirements:** Optional setting, short timeout, bounded candidate count, fail-safe fallback to the original Qdrant/ripgrep ordering, and an Ali-specific benchmark before adoption.

## External Agent Skills registry

- **Status:** Investigate later; Google documentation recommends the open Agent Skills ecosystem, but it is not part of the current build.
- **Candidate:** [skills.sh](https://www.skills.sh/) and its open-source Vercel skills CLI.
- **Purpose:** Let Ali discover reusable, cross-agent procedures for coding, testing, research, office artifacts, and other specialist work without hard-coding every workflow.
- **Trust policy:** Prefer official or independently audited skills, preview contents and requested capabilities, require approval before installation, pin the source commit and content hash, and review updates rather than silently replacing instructions.
- **Permission boundary:** Skills may describe a procedure but never bypass Ali's existing tool registry, user identity, activity log, or Trusted Workstation approval policy.
