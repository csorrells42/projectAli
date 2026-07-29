# Project Ali Agent Framework architecture

`programming-knowledge-stable-v1` is the rollback boundary for this upgrade.

## One personality, private specialists

Ali is the only user-facing identity and owns every final answer. Private Software Engineer, Researcher, and Office/Artifact agents may be invoked as bounded tools. They do not introduce additional assistant identities into the conversation and do not receive authority beyond their assigned tool sets.

The first implementation registers exactly three synchronous Agent Framework agents as tools: `consult_software_engineer`, `consult_researcher`, and `consult_office_artifact_specialist`. Each invocation uses a fresh private session. Specialists receive only domain-relevant, non-approval tools; Ali retains all mutating and approval-requiring operations so the existing permission window remains authoritative.

## Orchestration policy

- **Direct Ali:** greetings, casual conversation, stable knowledge, simple questions, and one-step tool actions.
- **Agent skill:** focused domain work where the model should adapt a trusted playbook.
- **Specialist as tool:** a substantial subtask needs a narrow instruction set and tool inventory; control returns to Ali.
- **Sequential workflow:** required stages have a meaningful order and should be observable and repeatable.
- **Programming group chat:** substantial software creation or repair benefits from a bounded maker/checker loop. It is never the default for explanations or small edits.
- **Magentic:** open-ended, multi-domain objectives whose useful plan cannot be expressed as one established workflow. It remains bounded by iteration, time, tool permissions, and user policy.
- **Concurrent/background agents:** disabled. This deployment uses one local inference path and will not spend latency pretending that queued model calls are parallel.

## Magentic activation

Magentic is eligible only when all of the following are true:

1. the request is open-ended rather than a known single operation;
2. it spans at least two specialist domains or requires dynamic replanning;
3. a direct specialist or established sequential workflow is insufficient; and
4. the configured policy is **Automatic for complex work**, or the user approves when policy is **Ask first**.

Magentic is never used for greetings, ordinary factual answers, a single file edit, a routine build/test, basic web search, or memory recall. High reasoning effort alone does not activate it.

## Shared safety and visibility

Framework middleware, Ali's permission policy, and activity events surround every agent and workflow. Activity exposes role, step, tool choice, result, elapsed time, approval, and failure state. Hidden reasoning is never quoted, spoken, stored in conversation history, or shown as activity.

Agent Skills are loaded only from Ali's shipped, reviewed skill directory. The provider exposes its standard resource and script operations, but the current reviewed skills are instruction-only and ship no executable scripts.

## Implemented workflows

- `run_research_artifact_workflow` is an official sequential workflow: Researcher then Office/Artifact specialist.
- `run_programming_group_chat` is an official round-robin group chat: Software Engineer and a workflow-only Programming Reviewer, capped at four participant turns.

Both workflows are hosted as Agent Framework agents and then exposed as model-callable functions. They use the lockstep in-process execution environment. Ali inspects their result, performs approval-requiring actions, and gives the only user-facing final response.

## Bounded Magentic and durable checkpoints

`run_magentic_orchestration` uses the official Agent Framework Magentic builder with the same three private specialists. It runs synchronously in the lockstep environment with a configurable maximum of 2-12 coordination rounds, one reset, two stalls, and no automatic plan-signoff pause. Tool permissions still surround every action; Magentic itself requires the ordinary approval window when the policy is **Ask first**.

The **Settings > Agents** tab provides three activation policies:

- **Off:** removes Magentic from Ali's model-callable inventory.
- **Ask first:** keeps it available but requires explicit activation approval.
- **Automatic for complex work:** permits model selection only under the eligibility boundary above.

Workflow state uses the Agent Framework JSON checkpoint manager and a file-backed checkpoint store under Ali's local data directory. Every private agent and manager has a stable executor identity so a compatible graph can be reconstructed after the application or computer restarts.

At startup and before each new turn, Ali inspects the latest checkpoint for every session. Only interrupted checkpoints that still contain queued work or an outstanding request and whose executor identities match the current workflow graph are offered. Completed sessions, malformed files, and checkpoints from an incompatible build are not presented as resumable work. The activity panel announces recoverable sessions without running them.

`list_recoverable_workflows` returns the exact local session identifiers and preserved objectives. `resume_workflow_checkpoint` calls Agent Framework's `ResumeAsync` with the saved `CheckpointInfo`; it runs only after the user explicitly asks to resume that exact session. Ali never auto-resumes work at startup, never guesses a session identifier, and never restarts the objective from the beginning. A failed resume leaves the checkpoint intact for inspection or another deliberate attempt.

The Settings tab reports the stored checkpoint count and can archive checkpoints recoverably into a timestamped sibling folder. Checkpointing and recovery do not introduce concurrent or background execution.

## Live conversation debugging bridge

The optional **Settings > Agents > Live Ali debugging bridge** binds Codex and other local test clients to the active desktop conversation without screen automation. `POST /v1/turns` enters text through the same typed-input method used by the Send button. `GET /v1/session` returns the current transcript, parsed render blocks, evidence state, visible Agent Activity, busy/status state, and any permission-wait metadata.

The bridge listens only on `127.0.0.1`, requires a generated bearer token, and is off by default. When the user enables the bridge, authenticated permission decisions are available by default but can be disabled independently. `POST /v1/approvals` can resolve only Ali's exact currently visible request ID as allow-once, allow-exact-arguments, allow-tool, or deny. The request still travels through Ali's normal permission, standing-grant, activity, and audit path; stale or mismatched IDs fail closed. The bridge returns visible/rendered state only and never exports hidden model reasoning.

Local debugging uses `tools\TalkToAli.ps1 Status` to clone the current visible session or `tools\TalkToAli.ps1 Send "message"` to submit through the live Send pipeline. With trusted-controller decisions enabled, `ApproveOnce`, `ApproveArguments`, `ApproveTool`, and `Deny` resolve the exact pending approval shown by `Status`. The helper discovers Ali's current local data root and token without printing the token.
