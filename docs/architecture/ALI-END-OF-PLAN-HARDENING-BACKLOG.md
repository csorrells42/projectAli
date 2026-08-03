# Ali End-of-Plan Hardening Backlog

Status: **First priority after CP13 is complete**

These items are not substitutes for the required CP7-CP13 gates and are not known CP7 defects. They are broader assurance programs that could materially improve Ali after the agreed architecture is complete. They must not interrupt CP7-CP13. Once CP13 is finished, this document becomes the first ordered backlog for the next hardening decision; no item may silently disappear.

## 1. Physical power-loss durability matrix

- Priority: High before Ali performs unattended, high-value mutations.
- Trigger: Ali is expected to recover automatically after a machine reset, storage interruption, or power loss during a durable operation.
- Work: Exercise real process termination and controlled machine/VM power loss at every journal, displacement, publication, cleanup, and completion boundary.
- Completion evidence: Every seam recovers to an authenticated old or new state, never double-applies an effect, never reports unconfirmed success, and leaves no ambiguous protected transaction.

## 2. Windows filesystem race and namespace fuzzing

- Priority: Medium for the current single-owner workstation; high before wider deployment.
- Trigger: Ali operates in roots concurrently modified by other programs or users.
- Work: Repeatedly interpose rename, delete, hard-link, junction, reparse-point, 8.3-alias, child-creation, and identity-replacement races across file, source-publication, memory, Git, and DevOps adapters.
- Completion evidence: Instrumented campaigns cover every declared mutation seam and demonstrate fail-closed behavior or exact recovery with no out-of-root mutation.

## 3. Same-user hostile-process threat model

- Priority: Medium while Ali is owner-operated; high if untrusted software shares the Windows account.
- Trigger: The design must defend against another process running with the same user authority, rather than ordinary crashes and accidental interference.
- Work: Inventory which protected artifacts, handles, process launches, local secrets, and recovery decisions remain inside the same-user trust boundary, then choose explicit mitigations.
- Completion evidence: A reviewed threat model maps each attack path to an OS-enforced control, a detected/refused condition, or a clearly accepted residual risk.

## 4. Hermetic execution sandbox for untrusted projects and toolchains

- Priority: High before Ali builds or runs code that is not trusted by the owner.
- Trigger: Ali accepts arbitrary repositories, build scripts, analyzers, generators, package hooks, or executables from third parties.
- Work: Isolate compiler/tool child processes, filesystem and registry access, network access, credentials, environment variables, and descendant processes using an enforceable Windows containment boundary.
- Completion evidence: Escape-oriented tests prove an untrusted build cannot reach data, credentials, network destinations, or processes outside its declared sandbox policy.

## 5. Long-duration performance and recovery soak

- Priority: High for CP13 deployment confidence.
- Trigger: The integrated CP7-CP12 system is stable enough for representative end-to-end workloads.
- Work: Run large repositories, repeated tool loops, model reconnects, optional-service failures, cancellation, restart, memory pressure, and long idle/active cycles while collecting latency, queue depth, handle count, memory, CPU, receipts, and recovery outcomes.
- Completion evidence: Defined latency/resource budgets hold for the agreed duration, no backlog grows without bound, no handle or memory leak trends upward, and every injected interruption recovers truthfully.

## 6. Zero-trust local toolchain redesign

- Priority: Low for a trusted owner-managed workstation; reconsider if Ali becomes a service or shared product.
- Trigger: Authorized local compilers, SDK resolvers, MSBuild imports, analyzers, generators, helpers, or child processes can no longer be treated as trusted code.
- Work: Replace the current trusted-toolchain boundary with authenticated components, least-privilege execution, policy-controlled inputs/outputs, and independently verified effects.
- Completion evidence: The architecture no longer relies on project or toolchain code honoring Ali's selected-root and effect boundaries, and adversarial conformance tests prove the replacement controls.

## CP13 handoff requirement

The CP13 completion report must link this document and explicitly hand it forward as the first post-CP13 backlog. The follow-on hardening decision must record, for each numbered item:

1. whether it was completed now, scheduled as the next hardening checkpoint, or judged unnecessary for the current deployment;
2. the evidence supporting that decision; and
3. any remaining operational limitation stated in plain language.
