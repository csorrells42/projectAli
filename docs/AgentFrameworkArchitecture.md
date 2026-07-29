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

Agent Skills are loaded only from Ali's shipped, reviewed skill directory. Skill scripts are not enabled in the initial framework checkpoint.
