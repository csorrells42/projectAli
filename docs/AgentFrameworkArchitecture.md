# Project Ali Agent Framework architecture

## One harness and one Ali planning loop

Ali has one user-facing identity, one production Agent Framework Harness agent, and one model-controlled planning/execution loop. The desktop conversation enters `ConversationOrchestrator`, crosses the thin `AliToolCoordinator` boundary, and runs through `AliAgentHarnessRunner`. A fresh Harness session is created for each visible user turn while the same session is retained across that turn's tool calls and approval responses.

The production runner does not construct or register private specialist agents, sequential workflows, programming group chat, a Magentic manager, or external coding agents. It also does not inspect, advertise, or resume checkpoints created by those retired nested graphs. Every callable action remains in Ali's one effective tool inventory and returns to the same planner.

The compatibility connector still performs bounded transport repair, completion review, and a temporary semantic classification call used to decide whether a proposed direct answer needs critic review. Those calls do not own tools, retain a private work graph, or start another Agent Framework agent. The state-backed planning-client migration will replace that temporary classifier and the remaining compatibility repair chains; until then, one planning loop does not mean one model request per turn.

## Reusable expertise is guidance, not another brain

Reviewed Agent Skills preserve the useful expertise that previously lived in nested agents and workflows:

- `software-engineering-delivery` guides substantial implementation, inspection, build, test, debugging, review, and delivery work.
- `evidence-research` guides current, comparative, source-sensitive, and local-document research.
- `office-artifact-delivery` guides documents, PDFs, charts, spreadsheets, presentations, and polished business artifacts.
- `engineering-shop-floor-explanation` guides evidence-bound interpretation of engineering documents for practical shop-floor use.

Skills are loaded progressively from Ali's shipped `skills` directory. A skill can provide instructions and reviewed procedure, but it cannot execute by itself, maintain an independent transcript, claim success, or bypass capability settings, tool permissions, evidence requirements, or activity reporting. Ali's one planner selects every concrete tool call and produces the only final answer.

The current reviewed skills are instruction-only and ship no executable scripts.

## Capability and permission boundary

Before each planning pass, the canonical capability boundary intersects the live runtime declarations, enabled capability groups, provider readiness, active user, saved permissions, MCP state, and semantic tool directory. Disabled or retired tools are absent from the callable registry rather than hidden only by prompt wording.

Agent Framework continues to own the outer session, registered-tool invocation, approval suspension and response, file access, file memory, Agent Skills, and lifecycle middleware. Ali's permission policy remains authoritative for file changes, process execution, network or metered operations, private reads, destructive actions, and other consequential tools.

Activity reports the visible lifecycle, selected tool, approval state, result, and elapsed work without exposing hidden reasoning. Tool results and retrieved content remain untrusted data rather than instructions.

## State and recovery boundary

Conversation-scoped Agent Framework file memory remains available beneath Ali's `Data\AgentWorkspaces` area for private notes and drafts. It is separate from personal Mem0 memory, the indexed document library, and user-visible artifacts.

Legacy nested-workflow checkpoint files may remain on disk from earlier builds. The single-loop runtime does not open, modify, delete, offer, or resume them. They stay inert until the later journal/recovery cutover establishes the one authoritative state-backed recovery format. Ali never silently restarts old work.

## External coding-agent boundary

Ali is the sole coding executor. Production composition does not construct external coding-agent ownership, register external executor functions, or subscribe to external executor progress events. Retired compatibility providers and packaging assets are not shipped.

Ali performs software work through her native programming, Roslyn, language-provider, build, test, run, debugger, architecture, source-control, and delivery tools under the ordinary capability and permission boundary.

## Live conversation debugging bridge

The optional **Settings > Agents > Live Ali debugging bridge** binds Codex and other local test clients to the active desktop conversation without screen automation. `POST /v1/turns` enters text through the same typed-input method used by the Send button. `GET /v1/session` returns the current transcript, parsed render blocks, evidence state, visible Agent Activity, busy/status state, and any permission-wait metadata.

The bridge listens only on `127.0.0.1`, requires a generated bearer token, and is off by default. When enabled, authenticated permission decisions can resolve only Ali's exact currently visible request ID. The request still travels through the normal permission, standing-grant, activity, and audit path; stale or mismatched IDs fail closed. The bridge returns visible state only and never exports hidden model reasoning.

Local debugging uses `tools\TalkToAli.ps1 Status` to inspect the visible session or `tools\TalkToAli.ps1 Send "message"` to submit through the live Send pipeline. With trusted-controller decisions enabled, `ApproveOnce`, `ApproveArguments`, `ApproveTool`, and `Deny` resolve the exact pending approval shown by `Status`. The helper discovers Ali's current local data root and token without printing the token.
