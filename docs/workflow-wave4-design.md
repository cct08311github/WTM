# Wave-4 Design Addendum — 加签 (WF-18) + 委托 (WF-19): Final Synthesized Spec

> Status: APPROVED FOR IMPLEMENTATION. Supersedes the three candidate designs. Every claim below was verified against `origin/feat/wf-16-return-to-node` (the merged Wave-3 substrate): `GuardedTransition.cs`, `AllApprovalHandler.cs`, `WorkflowEngine.cs`, `SequentialApprovalHandler.cs`, and the `Models/` schema. Line references are to that branch.

---

## 1. Backbone + Grafts + Skeptic-Forced Fixes

### Chosen backbone: node-local epoch folded into the completion CAS predicate (Design 3)

All three designs scored 4/10 from the skeptics because each leaned on a property the **verified engine does not have**: an atomic "count-and-complete" statement. Ground truth (verified):

- `GuardedTransition.CompleteNodeInstanceAsync` (GuardedTransition.cs:467) guards **`State==Activated AND RowVer==@v AND (Generation==@g)`**. `TotalRequired` is **NOT** in the predicate.
- `GuardedTransition.IncrementNodeApprovedCountAsync` (GuardedTransition.cs:541) is **RowVer-less** (`WHERE State==Activated` only — explicitly advisory).
- The All-mode completion path is **multi-await**: `ApproveTaskAsync` (WorkflowEngine.cs:617-633) does `IncrementNodeApprovedCount` → re-read `freshNodeAll` (AsNoTracking) → `ComputeThreshold(freshNodeAll.TotalRequired,…)` → `AdvanceAsync` → `AdvanceCoreAsync` re-reads `activeNode` (line 302) → `CanCompleteAsync` (line 195, yet another read of `TotalRequired`) → `CompleteNodeInstanceAsync(activeNode.ID, activeNode.RowVer)`.
- **There is no `BeginTransactionAsync` around `ApproveTaskAsync`.** Each `ExecuteUpdateAsync` is its own implicit transaction.

So the **threshold basis (`TotalRequired`) and the CAS guard (`RowVer`) are read from a snapshot that nothing fences against a concurrent 加签**. This is the real R1 break. EF's portable `ExecuteUpdateAsync` cannot do `COUNT(tasks) >= ceil(...)` + state-flip in one statement across all 7 providers, so **Design 2's "derive TotalRequired via live COUNT inside the CAS" is rejected** (physically impossible without a correlated subquery the team has not validated on Oracle/DaMeng, and the COUNT/RowVer reads stay split). **Design 1's "Threshold-as-CAS-field" is rejected as written** because it never binds `TotalRequired` to the *actual* completion read — it relies on an unforced ordering ("threshold is always read after the RowVer that 加签 advanced"), which the multi-await path does not guarantee.

Design 3 is the backbone because its fix is **minimal and surgical**: add one node-local epoch `ApproverSetEpoch` that every approver-set mutation bumps in the **same** `ExecuteUpdateAsync` that touches `TotalRequired`, and **add `AND ApproverSetEpoch==@e` to the completion CAS predicate** — exactly mirroring how Wave-3 already added the optional `Generation` guard to the same predicate (GuardedTransition.cs:54-55, 467). The threshold basis and the completion guard now travel together because the epoch is read alongside `TotalRequired` and asserted in the flip.

### Grafts

| Graft | From | Why |
|---|---|---|
| **委托 = task REASSIGNMENT, never mint** (mid-flight) | Design 1 (its single airtight idea, per all 3 verdicts) | `TotalRequired` is **structurally invariant** under delegation — no decrement, no atomic-counter race. The slot is reassigned 1-for-1 via a task-level CAS; vote-count integrity is structural, not computed. |
| **Resolution-time substitution + dedupe before Activation** | Design 2/3 | Transitive resolution + human-dedupe run single-threaded in the resolver **before** the node is Activated, so `TotalRequired` is written once with zero concurrency. The dangerous "decrement mid-会签" path is **designed out**. |
| **Gen-stamped drop-on-return is free** | All 3, verified | `DiscardTasksForReturnAsync` cancellable set (GuardedTransition.cs:194) **already includes `TaskState.AddedPending`**; injected tasks carry `Generation`, so 回退 span-discard drops them with **zero new code**. |
| **Window-as-CAS-predicate + frozen-stamp default** | Design 1/2/3 (converged) | AtAction folds the time-bound into the claim CAS predicate (no separate read → no TOCTOU). AtAssignment freezes authority at mint (no re-check → no race). |

### Skeptic-forced fixes (the deltas that close the breaks the verdicts found)

1. **FIX-A (R1 keystone):** Every approver-set mutation bumps `ApproverSetEpoch` **and** `RowVer` in one statement; **both** `CanCompleteAsync` and the final completion CAS are re-plumbed to read and assert `ApproverSetEpoch`. This binds the threshold basis to the completion guard. (§4 R1)
2. **FIX-B (R1 "during-completion" window):** The skeptics' worst case — 加签 lands while `State==Activated` but after the threshold-met decision — is closed because `AddApproversToNodeAsync` and `CompleteNodeInstanceAsync` **contend on the same `(RowVer, ApproverSetEpoch)` predicate space**. Whoever loses re-reads. There is no longer a "decision made against snapshot X, CAS executes against snapshot Y" gap. (§4 R1)
3. **FIX-C (R2 non-atomic supersede+mint):** Mid-flight 委托 does **not** supersede-then-mint (Design 2's path, which the skeptic broke by observing the `-1` intermediate state with no transaction). It does a **single** task-level CAS that flips `AssigneeITCode` on the existing slot. One statement, one row, no intermediate count. (§4 R2)
4. **FIX-D (R3 AtAction):** The window bound is **denormalized onto the task** (`DelegationExpiresUtc`) at mint, so the claim CAS predicate `AND (DelegationExpiresUtc IS NULL OR @now <= DelegationExpiresUtc)` fences the window with no rule read inside `ExecuteUpdateAsync`. `@now` is **app-supplied and bound once** (never SQL `CURRENT_TIMESTAMP` — clock-skew + provider-portability). (§4 R3)
5. **FIX-E (Oracle/DaMeng portability — raised by Verdict 3):** For providers where `ExecuteUpdateAsync` with a `DateTime` comparison in WHERE does not translate, the claim degrades to `SELECT … FOR UPDATE` + in-txn re-check + flip **inside an explicit `BeginTransactionAsync`** so the check-then-act stays atomic under a row lock. This is gated by the existing `DbTypeEnum` conformance suite. (§4 R3, §6)
6. **FIX-F (Sequential Before-insert tear — raised by Verdict 3):** `SequentialApprovalHandler`'s pointer-advance `SetProperty` (SequentialApprovalHandler.cs:167) gains `AND ApproverSetEpoch==@e`, so a pointer-advance that raced a 加签 Before-insert loses and re-reads. (§2)
7. **FIX-G (delta over-count — raised by Verdict 1):** 加签 delta is computed from a deduped read **and** protected by a `UNIQUE` constraint over active states so a concurrent double-加签 of the same person cannot inflate `TotalRequired`. MySQL (no filtered index) falls back to epoch serialization + tombstone discriminator. (§2, §5)
8. **FIX-H (AdminFallback fail-open — raised by Verdict 3):** A delegation cycle resolving to an empty `AdminFallbackITCode` must **not** silently auto-approve. It routes through the existing `FailClose` policy (`ApplyNoApproverPolicyAsync` sets an impossible threshold → node blocks, never auto-approves). (§3)

---

## 2. 加签 (WF-18) Mechanism

### Surface

```
WorkflowActionResult AddApproverAsync(
    Guid sourceTaskId,            // the actor's own active task on the node (drives RBAC + AddDepth)
    AddPosition position,         // Before | After
    IReadOnlyList<string> newApproverITCodes,
    string reason,
    string actorITCode,
    CancellationToken ct)
```

Routes through **one** new `GuardedTransition` method; never calls `ExecuteUpdateAsync` directly (preserves the WTM single-CAS-helper invariant).

### Who-can-加签 (RBAC, enforced server-side before the CAS)

The actor must hold a current **Pending** assignee task on **this** node (looked up via `sourceTaskId WHERE AssigneeITCode==actorITCode AND State==Pending AND NodeInstanceId==node.ID`), **or** hold the workflow-admin privilege (`AdminFallbackITCode` / future `CanAddApprover` policy hook). Mirrors the WF-14 server-side-actor RBAC pattern. Anything else → `WorkflowActionCode.NotAuthorized`.

### MaxAddDepth (anti-runaway)

`ApprovalTask.AddDepth` (new column). Base resolver tasks = 0; an injected task = `sourceTask.AddDepth + 1`. `AddApproverAsync` rejects with `WorkflowActionCode.MaxAddDepthExceeded` **before** the CAS when `sourceTask.AddDepth + 1 > WorkFlowOptions.MaxAddDepth` (default 3, already present). O(1) — no chain walk.

### Pre/Post insertion semantics

- **会签 (All / ratio):** Both `Before` and `After` mint **Pending** tasks immediately; order is presentation-only (`SequenceOrder`). `TotalRequired += delta` makes them part of the threshold. The injected approver MUST act before completion.
- **串签 (Sequential):** `Before` inserts at `SequenceOrder == sourceTask.SequenceOrder` (shift the rest +1) as **`AddedPending`** (activates when the pointer reaches it); `After` inserts at `sourceTask.SequenceOrder + 1`. **FIX-F:** the `SequentialApprovalHandler` pointer-advance CAS now includes `AND ApproverSetEpoch==@e`, so an advance racing the insert loses and re-reads.
- **或签 (Any):** 加签 widens the candidate set; `TotalRequired` is advisory for Any (first action wins). The epoch still bumps so a near-simultaneous decide+加签 is linearized — the decide CAS loses on the stale epoch and re-reads, and the new approver is a legitimate alternative.

### `IsRuntimeInjected` and delegation flow-through

Each injected task is stamped `IsRuntimeInjected=true`, `AddedByITCode=actor`, `AddDepth=parent+1`, `Generation=node.Generation`, `State = (position==After && Sequential ? AddedPending : Pending)`, `RowVer=0`. **Cross-feature fix (Verdict 1's triplet risk):** injected-task creation runs through the **same delegation resolution step** as node-entry, so a standing rule on an injected approver D→C correctly mints/stamps for C.

### The exact CAS

New `GuardedTransition.AddApproversToNodeAsync` — one atomic single-row statement on `NodeInstance`, guarded exactly like the completion CAS:

```
UPDATE NodeInstance
   SET TotalRequired     = TotalRequired + @delta,
       ApproverSetEpoch  = ApproverSetEpoch + 1,
       RowVer            = RowVer + 1
 WHERE ID               == @nodeId
   AND State            == Activated          -- can't 加签 a completed/superseded node
   AND Generation       == @g                 -- Wave-3 epoch: stale-epoch 加签 no-ops after 回退
   AND ApproverSetEpoch == @expectedEpoch      -- no concurrent 加签/委托/转办 won first
   AND RowVer           == @expectedRowVer
```

- `@delta` = count of **newly-deduped** approvers (those not already holding an active task on the node — computed from a deduped read; see FIX-G).
- **rows==1 → winner:** in the **same `DbContext`**, INSERT the `@delta` new `ApprovalTask` rows, write `EventAction.AddApprover` via `WorkflowEventLogWriter` (Seq allocated through `AllocateSeqAsync`). The k INSERTs + the guarded UPDATE + the event-log append are wrapped in an explicit `BeginTransactionAsync` so a crash cannot leave `TotalRequired=N+k` with zero tasks (Verdict 3's deadlock risk).
- **rows==0 → loser:** node already completed / superseded / concurrently mutated → return `WorkflowActionResult.NodeAlreadyDecided` (mapped from `AlreadyHandled`). **Tasks are NEVER inserted** → zero orphan risk.

### FIX-G — delta correctness under concurrent 加签

Compute `@delta` from a deduped read of active assignees, and create rows **only** for not-yet-present approvers. A `UNIQUE` constraint `(NodeInstanceId, AssigneeITCode, Generation)` filtered to active states makes a racing duplicate insert a no-op (`DbUpdateException` caught → that approver is treated as already-present, delta self-corrects). On MySQL (no filtered unique index) rely on the `ApproverSetEpoch` serialization (a second concurrent 加签 loses the CAS and recomputes delta on retry) plus a non-filtered unique on `(NodeInstanceId, AssigneeITCode, Generation)` — and **never reuse `(Assignee, Gen)`** even for cancelled rows (a fresh `Generation` or the soft-delete tombstone discriminates).

### Drop-on-return (zero new code)

Injected tasks carry `Generation` and `IsRuntimeInjected=true`. On 回退-to-node, `DiscardTasksForReturnAsync` already cancels `AddedPending` and all span tasks by `Generation`; `SupersedeNodeAsync` flips the node to `Superseded`. Re-materialization mints fresh resolver tasks at `gNew` with `TotalRequired` recomputed — injected approvers are intentionally dropped (a one-time human action against the discarded attempt).

---

## 3. 委托 (WF-19) Mechanism

### DelegationRule (schema present from Sprint 1, verified)

`PrincipalITCode`, `DelegateeITCode`, `ScopeDefinitionCode` (null = all), `StartUtc`, `EndUtc`. It is a `PersistPoco`, so **`IsValid` is the inherited soft-delete flag** — "active rule" means `IsValid==true`. **Active ⇔** `IsValid==true AND now ∈ [StartUtc,EndUtc] AND (ScopeDefinitionCode==null OR == instance.DefinitionCode)`.

### Core decision (the safety keystone)

**Delegation NEVER changes `TotalRequired` on an in-flight 会签 node.** Two paths, both `TotalRequired`-invariant:

#### (A) Resolution-time substitution — node-entry, pre-Activated, single-threaded (no concurrency)

A new `DelegationResolvingDecorator : IApproverResolver` wraps the base resolver (decorator pattern — **no change to the published `IApproverResolver.ResolveAsync` signature**, verified at IApproverResolver.cs:111). For each base approver A it walks the chain and **replaces** A with the terminal delegatee (records `DelegatedFromITCode=A` for audit). It is a substitution, never an addition.

**Transitive chain (hop cap + cycle detect):**
```
ResolveTransitive(p):
  visited = HashSet<string>{ p };  cur = p;  hops = 0
  loop:
    rule = activeRuleFor(cur)                 // live read, no cache
    if rule == null: return cur               // chain ends
    next = rule.DelegateeITCode
    if visited.Contains(next):                // cycle A→B→A (or self A→A)
        log; return AdminFallback(cur)        // FIX-H: fail-safe, see below
    if ++hops > MaxDelegationHops (=3):
        log; return cur                       // stop at last resolved; never throw/loop
    visited.Add(next); cur = next
```
Explicit per-chain `visited` set = O(hops) true cycle detection (catches A→B→A even though codes differ each step). Self-delegation A→A is caught on the first revisit.

**FIX-H (no fail-open):** When a cycle/cap resolves to AdminFallback and `AdminFallbackITCode` is null/empty, the approver does **not** vanish into a silent auto-approve. It routes through the existing `FailClose` policy (`ApplyNoApproverPolicyAsync` sets an impossible threshold → node blocks for manual intervention). For a compliance-marketed engine, **fail-closed, never fail-open**.

**Human-dedupe vs `TotalRequired` — the exact atomic story:** The decorator accumulates into an **ordered distinct set** (`AddIfAbsent`). If the terminal delegatee is already a direct approver (D→C where C is also resolved independently), the set keeps **one** C and unions provenance. `TotalRequired = finalSet.Count` is written **once**, in the existing mint transaction (`AllApprovalHandler.OnEnterAsync` line 142), **before** the node is Activated. Because this is single-threaded and pre-Activation, there is **no CAS, no torn count, no decrement** — one human = one slot = one vote, by construction.

#### (B) Mid-flight delegation — task REASSIGNMENT, single-row CAS, `TotalRequired` invariant (FIX-C)

A standing rule added **after** the node is Activated does **NOT** retro-mint or retro-mutate the live node. Documented semantics: **standing delegation affects the next node activation, not nodes already activated.** To redirect a live task (explicit 转办/委托-now), one **single** task-level CAS reassigns the existing slot:

```
UPDATE ApprovalTask
   SET AssigneeITCode     = @C,
       DelegatedFromITCode = @D,
       DelegationRuleId    = @ruleId,
       DelegationExpiresUtc = @ruleEndUtc,
       ApproverSetEpoch     = @nodeEpochStamp,   -- audit stamp
       RowVer               = RowVer + 1
 WHERE ID         == @taskId
   AND State      == Pending          -- only reassign an actable slot
   AND RowVer     == @expectedRowVer
   AND Generation == @g                -- superseded-span reassign loses (回退 safety)
```
Then bump the node's `ApproverSetEpoch` (the eligible-actor set changed) via `AdvanceNodeApproverSetEpochAsync` so any in-flight completion re-reads. **`TotalRequired` is untouched** (1-for-1). 

- **rows==1:** C now owns D's exact slot. The same CAS writes `AssigneeITCode=C`, so the claim-time RBAC check (`AssigneeITCode==actor`, verified at WorkflowEngine.cs:566) unblocks C and blocks D — no permission deadlock, no soft-delete-then-recreate race (the trap in Design 2 / R2 Option-2). D's slot is `Pending`-flipped to C's ownership; D can never also vote.
- **rows==0:** D already acted (slot not Pending) or superseded → `AlreadyHandled`; delegation harmlessly does not apply to an already-cast vote.
- **Collision:** if C already holds an active task on this node, mid-flight reassignment is **REFUSED** (logged, `EventAction.Delegate` detail "delegatee already an approver; mid-flight merge not permitted"). Merging only ever happens pre-mint (path A), where it is concurrency-free. This upholds the no-mid-flight-`TotalRequired`-change invariant.

This is the wrapping of the "supersede + mint" idea into a **single atomic statement**, which is what closes the skeptic's R2 break (no observable `-1` intermediate, no transaction dependency for correctness of the count).

### DelegationWindowMode (default = AtAssignment, verified at WorkFlowOptions.cs:146)

- **AtAssignment (default, conservative red-line):** window evaluated once at mint/reassign, stamped into `DelegationExpiresUtc`. Task is then immutable w.r.t. the window — the delegatee may act after expiry because authority was **frozen at mint**. No re-check → no race. Changing the default is a CHANGELOG + startup-log event (silently altering approval authority is a compliance risk).
- **AtAction (opt-in):** window re-checked at claim, folded into the claim CAS predicate (§4 R3).

---

## 4. The Three Race Resolutions (exact guards)

### R1 — 加签-onto-in-flight-会签 threshold-recompute

**Root cause (verified):** `TryCompleteApprovedAsync` (AllApprovalHandler.cs:224) and `CanCompleteAsync` (line 195) compute `ComputeThreshold(node.TotalRequired,…)` and then `CompleteNodeInstanceAsync(node.RowVer)` — the threshold basis is **outside** the CAS predicate; the multi-await path (`Increment → re-read → Advance → re-read → Complete`) gives a window where 加签 changes `TotalRequired` after the threshold-met decision but before the flip.

**Resolution — `ApproverSetEpoch` co-incremented with `TotalRequired`, asserted in the completion CAS:**

1. **Schema:** add `NodeInstance.ApproverSetEpoch (uint, default 0)`.
2. **Mutation guard:** `AddApproversToNodeAsync` (and any `TotalRequired`-touching or eligible-actor-set-touching op) increments `ApproverSetEpoch` **and** `RowVer` in the same statement (§2).
3. **Completion guard (the surgical change):** `CompleteNodeInstanceAsync` gains an **optional** `uint? expectedApproverSetEpoch` param; when non-null the predicate appends `AND ApproverSetEpoch==@e`. **Optional/null preserves the byte-identical pre-Wave-4 predicate** for existing callers — the exact discipline Wave-3 used to add `Generation` (GuardedTransition.cs:467). Likewise `CanCompleteAsync`/`TryCompleteApprovedAsync` read `ApproverSetEpoch` alongside `TotalRequired` from the same `freshNode` snapshot and pass it to the CAS.

**Both orderings now safe (FIX-A + FIX-B):**
- 加签 commits first → `ApproverSetEpoch R→R+1`. The in-flight completion's epoch is stale → CAS rows==0 → `AlreadyHandled` → engine re-reads fresh `TotalRequired`+epoch, recomputes threshold (now N+k), sees `ApprovedCount < newThreshold` → returns `Advanced` (waits for D). **Correct.**
- Completion commits first → node leaves `Activated`. 加签's CAS fails `State==Activated` → rows==0 → `NodeAlreadyDecided`, tasks never inserted. **Correct — cannot inject into a decided node.**

**Direct answer to "can 加签 un-complete an already-met node?":** No. Once `State` flips to `CompletedApproved`, `AddApproversToNodeAsync` (`WHERE State==Activated`) matches zero rows → refused → INSERTs roll back. The threshold-met-but-uncommitted window is also closed because the deciding voter and the 加签 contend on the **same `(RowVer, ApproverSetEpoch)`** — the loser re-reads. The threshold is never evaluated against a torn `(count, total)` pair.

### R2 — 委托 transitive-chain + dedupe vs 会签 vote-count integrity

**Resolution — substitution before Activation + reassign-not-mint mid-flight (FIX-C):**

- **Pre-Activated (path A):** transitive resolution + dedupe on an in-memory `finalSet`; `TotalRequired = finalSet.Count` written once in the mint transaction. No concurrency → no torn count. Delegatee **replaces** principal (never added) → vote-count semantics intact. Cycle/cap via explicit `visited` set → `FailClose` fallback (FIX-H), never a silent loop or fail-open.
- **Mid-flight (path B):** a **single** task-level CAS flips `AssigneeITCode` on the existing slot; `TotalRequired` never moves. The same CAS writes `AssigneeITCode=C` (RBAC unblock for C, block for D). `rows==0` → vote already cast → no-op. Collision with an existing C-task → refused. **No lost approver** (slot reassigned atomically, never deleted), **no count corruption** (`TotalRequired` invariant in-flight), **no double vote** (one of {D,C} holds a claimable slot).
- **Why no transaction dependency:** because mid-flight delegation is one statement (not supersede-then-mint), there is no observable `-1` intermediate state — the skeptic's R2 break (Verdict 2) does not apply.

### R3 — DelegationWindowMode AtAction re-check vs claim TOCTOU (+ default safety)

**Resolution — window-as-CAS-predicate (FIX-D) + frozen-stamp default + provider fallback (FIX-E):**

- **Schema:** `ApprovalTask.DelegationExpiresUtc (DateTime?, nullable)` — denormalized snapshot of `DelegationRule.EndUtc` stamped at mint/reassign; `ApprovalTask.DelegationRuleId (Guid?)` for provenance; `ApprovalTask.WindowVerifiedUtc (DateTime?)` audit-only (never in a predicate).
- **AtAssignment (default):** task immutable w.r.t. window; no re-check → no race.
- **AtAction (opt-in), translating providers:** the claim routes through `ClaimDelegatedTaskAsync`, whose predicate folds the window in:
  ```
  UPDATE ApprovalTask SET State=Approved, ActedAtUtc=@now, RowVer=RowVer+1
   WHERE ID==@id AND State==Pending AND RowVer==@v AND Generation==@g
     AND (DelegationExpiresUtc IS NULL OR @now <= DelegationExpiresUtc)
  ```
  `@now` is **app-supplied, bound once** (never SQL `CURRENT_TIMESTAMP`). Time-check and state-flip are the **same** atomic UPDATE → no "re-read says active, then expire, then CAS succeeds" gap. `rows==0` is disambiguated by a follow-up no-side-effect read: `State==Pending AND @now > DelegationExpiresUtc` → `WorkflowActionCode.DelegationExpired` (deliberate reject; task stays Pending for manual reassignment, audit shows the real reason); `State!=Pending` → `AlreadyHandled`.
- **FIX-E (Oracle/DaMeng — Verdict 3 ship-blocker):** where `ExecuteUpdateAsync` with a `DateTime`-in-WHERE comparison does not translate, `ClaimDelegatedTaskAsync` degrades to `BeginTransactionAsync` + `SELECT … FOR UPDATE` of the row + in-txn `@now <= DelegationExpiresUtc` check + flip, all under the row lock — preserving check-then-act atomicity. Provider selection is gated by the existing `DbTypeEnum` conformance suite (`ConcurrencyConformanceTests`).
- **Mid-flight rule disable (`IsValid=false` without `EndUtc` change):** `DelegationExpiresUtc` captures the time window, **not** the disable flag. An explicit `RevokeDelegation` admin action CASes `DelegationExpiresUtc=@now` on all open delegated tasks for that rule (routing revocation through the same atomic predicate). Documented: flipping `IsValid` alone is a **future-rules** switch, not an in-flight revoke. **No deadlock:** a revoked/expired delegated task reverts to the principal (1-for-1 reassign, `TotalRequired` unchanged), who can still act — the node stays live. AtAction is **recommended to be paired with the Wave-5 timeout reaper**; a startup warning fires if `DelegationWindowMode=AtAction` is set without it.

---

## 5. New Entities / Fields / Migration (consumer-owned, mirrors Etl)

Migration model mirrors `WalkingTec.Mvvm.Etl`: the framework ships **POCO models + enum extensions only**; the **consumer** runs `dotnet ef migrations add Wave4Delegation` against their own `DataContext` (no embedded migration in the framework package). No new `DbSet` beyond what Wave-1..3 registered for these entities.

### NodeInstance (1 new column)
| Field | Type | Notes |
|---|---|---|
| `ApproverSetEpoch` | `uint`, default 0 | Node-local epoch; bumped by every approver-set mutation (加签 insert, 转办 reassign, AtAction revoke). Folded into the completion CAS predicate. Distinct from Wave-3 `Generation` (cross-node 回退 supersession). Read on the PK row — no index needed. |

### ApprovalTask (4 new columns; reuses existing `DelegatedFromITCode`, `AddedByITCode`, `IsRuntimeInjected`, `Generation`)
| Field | Type | Notes |
|---|---|---|
| `AddDepth` | `int`, default 0 | 加签 chain depth; enforced `<= MaxAddDepth`. O(1) (no chain walk). |
| `DelegationRuleId` | `Guid?` | FK-by-value to the rule that produced the assignment; provenance + AtAction. |
| `DelegationExpiresUtc` | `DateTime?` | `EndUtc` snapshot at mint/reassign; participates in the AtAction claim CAS predicate. Null = no window constraint. |
| `WindowVerifiedUtc` | `DateTime?` | Audit/forensics only — never in any predicate. |

### Indexes
- `UNIQUE IX_ApprovalTask_Node_Assignee_Gen (NodeInstanceId, AssigneeITCode, Generation)` filtered `WHERE IsValid AND State NOT IN (Cancelled, Delegated, Transferred)` — idempotent 加签 / reassign under race. **MySQL has no filtered index** → ship a non-filtered unique on the triple + the `ApproverSetEpoch` serialization + tombstone discriminator (never reuse `(Assignee, Gen)` for cancelled rows; bump `Generation` or use the soft-delete flag).
- `IX_DelegationRule_Lookup (TenantCode, PrincipalITCode, IsValid, StartUtc, EndUtc)` — chain-resolution lookups. Plus an app-level guard: **at most one overlapping active rule per `(Principal, Scope)`** for resolution determinism (Verdict 1's non-determinism caveat).

### Enums (all reused — verified, zero enum changes for these)
`TaskState.{AddedPending, Delegated, Transferred, Cancelled}` ✅ exist. `EventAction.{AddApprover, Delegate, Transfer}` ✅ exist. `WorkFlowOptions.{MaxAddDepth=3, MaxDelegationHops=3, DelegationWindowMode=AtAssignment, AdminFallbackITCode}` ✅ exist.

### New `WorkflowActionCode` values (closed-union discipline, precedent `MaxReturnLoopsExceeded`)
`MaxAddDepthExceeded`, `NodeAlreadyDecided`, `DelegationExpired`, `DelegationHopsExceeded`, `DelegationCycle`, `DelegateAlreadyParticipant`. Distinct codes so UI/audit see real reasons, never generic `AlreadyHandled`.

### New `GuardedTransition` methods (the only new CAS shapes)
| Method | Predicate / effect |
|---|---|
| `AddApproversToNodeAsync` | `WHERE State==Activated AND Generation==@g AND ApproverSetEpoch==@e AND RowVer==@v` SET `TotalRequired+=@delta, ApproverSetEpoch+=1, RowVer+=1` |
| `ReassignTaskAssigneeAsync` | `WHERE State==Pending AND RowVer==@v AND Generation==@g` SET `AssigneeITCode, DelegatedFromITCode, DelegationRuleId, DelegationExpiresUtc, RowVer+=1` |
| `AdvanceNodeApproverSetEpochAsync` | `WHERE State==Activated AND RowVer==@v` SET `ApproverSetEpoch+=1, RowVer+=1` (used after reassign / revoke so completion re-reads) |
| `ClaimDelegatedTaskAsync` | `ClaimApprovalTaskAsync` + `AND (DelegationExpiresUtc IS NULL OR @now <= DelegationExpiresUtc)`; FIX-E fallback on non-translating providers |
| `RevokeDelegatedTasksAsync` | AtAction disable sweep: per-row CAS sets `DelegationExpiresUtc=@now` (or reverts to principal) + bumps node epoch; idempotent, partial-success reported |

### Changed (additive, backward-compatible — preserve byte-identical predicate for null params)
- `CompleteNodeInstanceAsync` → add optional `uint? expectedApproverSetEpoch`.
- `AllApprovalHandler.{CanCompleteAsync, TryCompleteApprovedAsync, TryCompleteRejectedAsync}` → read + thread `ApproverSetEpoch`.
- `SequentialApprovalHandler` pointer-advance → add `AND ApproverSetEpoch==@e` (FIX-F).
- `DefaultApproverResolver.ResolveAsync` → wrapped by `DelegationResolvingDecorator` (no signature change).

---

## 6. Concurrency Test Matrix

Extend the existing SQLite `ConcurrencyConformanceTests` + the cross-provider `DbTypeEnum` suite (incl. DaMeng/Oracle for FIX-E). All tests interleave via the proven WF-0 barrier harness.

### T-ADD-* (WF-18)
| ID | Scenario | Assert |
|---|---|---|
| T-ADD-01 | 3-of-3 会签, A approved; 加签 D vs B's final approve, **加签 wins first** | node waits for D; `TotalRequired=4`; epoch+1; B's completion CAS rows==0 |
| T-ADD-02 | Same, **B's completion wins first** | node `CompletedApproved`; 加签 CAS rows==0; D task NOT created (no orphan) |
| T-ADD-03 | 加签 during the threshold-met-but-uncommitted window (FIX-B) | exactly one outcome; if 加签 lands, node waits for D; never completes short |
| T-ADD-04 | One `Approved` + one `AddedPending`, `TotalRequired=2` | node does **NOT** complete (AddedPending counts toward required, not toward ApprovedCount) |
| T-ADD-05 | Concurrent double-加签 of the same person (FIX-G) | `TotalRequired` increments by 1, not 2; unique constraint / epoch self-corrects |
| T-ADD-06 | 串签 Before-insert at pointer vs pointer-advance (FIX-F) | no torn `SequenceOrder`; advance loses on stale epoch, re-reads |
| T-ADD-07 | 加签 then 回退-to-node | injected task `Cancelled` by `DiscardTasksForReturnAsync`; re-mint at gNew without D |
| T-ADD-08 | `AddDepth == MaxAddDepth` then 加签 | `MaxAddDepthExceeded`; no CAS, no task |
| T-ADD-09 | 加签 by non-participant | `NotAuthorized` before CAS |
| T-ADD-10 | 加签 + standing rule on injected approver (triplet) | C gets the injected slot; `TotalRequired` correct |

### T-DEL-* (WF-19)
| ID | Scenario | Assert |
|---|---|---|
| T-DEL-01 | Chain A→B→C at node entry | task minted for C; `DelegatedFromITCode=A`; `TotalRequired` = deduped count |
| T-DEL-02 | Cycle A→B→A | `FailClose` (impossible threshold), node blocks; **never** auto-approve (FIX-H) |
| T-DEL-03 | Self-delegation A→A | caught on first revisit; FailClose fallback |
| T-DEL-04 | Hop cap exceeded (4-deep, Max=3) | stops at last resolved; logged; never throws/loops |
| T-DEL-05 | Mid-flight reassign D→C vs D's approve (FIX-C) | one outcome: C owns slot (rows==1) **or** D already acted (rows==0); `TotalRequired` invariant |
| T-DEL-06 | Mid-flight reassign where C already holds a slot | refused; D's task unchanged; `TotalRequired` invariant |
| T-DEL-07 | Dedupe: D→C where C is also a direct approver (entry) | one C slot; provenance unioned; `TotalRequired` counts C once |
| T-DEL-08 | AtAction claim at expiry boundary (FIX-D) | in-window claim succeeds; expired claim rows==0 → `DelegationExpired` (not `AlreadyHandled`) |
| T-DEL-09 | AtAction claim vs window expiry (TOCTOU) | no approval recorded after window; single bound `@now` |
| T-DEL-10 | AtAssignment after expiry | claim succeeds (authority frozen at mint) |
| T-DEL-11 | Admin `RevokeDelegation` vs concurrent claim on same task | per-row CAS; either claim won legitimately or revoke applied; idempotent; partial-success reported |
| T-DEL-12 | Reassign vs 回退 supersede (Generation guard) | reassign on superseded span rows==0 (`DiscardTasks` cancelled the task) |
| T-DEL-13 | AtAction on Oracle + DaMeng (FIX-E) | `SELECT FOR UPDATE` fallback enforces window atomically; conformance inclusive of boundary |

### T-MIX-* (cross-feature)
| ID | Scenario | Assert |
|---|---|---|
| T-MIX-01 | 加签 (epoch bump) interleaved with 回退 (Generation bump) on same node | both guards compose; correct winner; no wrong-epoch silent bug |
| T-MIX-02 | 委托 reassign + concurrent 加签 on same node | both bump `ApproverSetEpoch`; completion re-reads; `TotalRequired` consistent |
| T-MIX-03 | Concurrent 加签 + approve: Seq allocation (`AllocateSeqAsync`) | no dropped `AddApprover` audit entry; retry envelope covers Seq |

---

## 7. Sub-Issue Scopes (file-scoped, sequenced)

### #283 — WF-18 加签 (depends on Wave-3 merged; lands first)

Sequence so each PR is independently reviewable and green:

1. **#283.1 — Schema + epoch primitive (no behavior change).**
   `Models/NodeInstance.cs` (+`ApproverSetEpoch`); `Models/ApprovalTask.cs` (+`AddDepth`); `Models/Enums.cs`→`Engine/WorkflowActionResult.cs` (+`MaxAddDepthExceeded`, `NodeAlreadyDecided`). `Engine/GuardedTransition.cs`: add `AddApproversToNodeAsync` + `AdvanceNodeApproverSetEpochAsync`; add **optional** `expectedApproverSetEpoch` to `CompleteNodeInstanceAsync` (null = byte-identical predicate). Migration is consumer-owned (mirror Etl). Tests: GuardedTransition CAS unit tests.
2. **#283.2 — Completion path threads the epoch (FIX-A/B).**
   `Engine/AllApprovalHandler.cs` (`CanCompleteAsync`, `TryCompleteApprovedAsync`, `TryCompleteRejectedAsync` read + assert epoch); `Engine/WorkflowEngine.cs` (`ApproveTaskAsync` All-mode path passes epoch into `AdvanceAsync`/completion). Tests: T-ADD-01..04.
3. **#283.3 — `AddApproverAsync` orchestration + RBAC + MaxAddDepth + delta/unique (FIX-G).**
   `Engine/WorkflowEngine.cs` (new `AddApproverAsync`, server-side RBAC, explicit `BeginTransactionAsync` around guarded UPDATE + k INSERT + event log); `Engine/IWorkflowEngine.cs` (surface). Tests: T-ADD-05, T-ADD-08..10.
4. **#283.4 — Sequential Before/After + pointer-advance epoch guard (FIX-F).**
   `Engine/SequentialApprovalHandler.cs` (pointer-advance `AND ApproverSetEpoch==@e`; Before/After insertion). Tests: T-ADD-06.
5. **#283.5 — Drop-on-return verification + triplet hook.**
   No new discard code (reuse verified). Wire injected-task creation through the resolver delegation step (forward-compat for #284). Tests: T-ADD-07, T-MIX-01, T-MIX-03.

### #284 — WF-19 委托 (depends on #283 for `ApproverSetEpoch` + reassign-epoch primitive)

1. **#284.1 — Schema + codes.**
   `Models/ApprovalTask.cs` (+`DelegationRuleId`, `DelegationExpiresUtc`, `WindowVerifiedUtc`); `Engine/WorkflowActionResult.cs` (+`DelegationExpired`, `DelegationHopsExceeded`, `DelegationCycle`, `DelegateAlreadyParticipant`); `Models/DelegationRule.cs` index guidance + the one-overlapping-rule app guard. Migration consumer-owned.
2. **#284.2 — Resolution-time substitution (path A, FIX-H).**
   New `Engine/DelegationResolvingDecorator.cs` (transitive walk + visited-set cycle detect + hop cap + dedupe + FailClose fallback); DI wiring wrapping `DefaultApproverResolver` (no `IApproverResolver` signature change). Tests: T-DEL-01..04, T-DEL-07.
3. **#284.3 — Mid-flight reassignment (path B, FIX-C).**
   `Engine/GuardedTransition.cs` (`ReassignTaskAssigneeAsync`); `Engine/WorkflowEngine.cs` (转办/委托-now entry, RBAC, collision refusal, epoch bump). Tests: T-DEL-05, T-DEL-06, T-DEL-12, T-MIX-02.
4. **#284.4 — DelegationWindowMode AtAction (FIX-D) + provider fallback (FIX-E).**
   `Engine/GuardedTransition.cs` (`ClaimDelegatedTaskAsync` with folded predicate + `SELECT FOR UPDATE` fallback); `Engine/WorkflowEngine.cs` (claim routing); `DelegationExpiresUtc` stamping at mint/reassign. Tests: T-DEL-08..10, T-DEL-13.
5. **#284.5 — Revocation + startup guards.**
   `Engine/GuardedTransition.cs` (`RevokeDelegatedTasksAsync`); `WorkFlowOptions` startup-log when `DelegationWindowMode=AtAction` without the timeout reaper; CHANGELOG (default semantics, AtAction opt-in, "standing delegation affects next activation only"). Tests: T-DEL-11.

**Sequencing rule:** #283 fully merges before #284.3+ (mid-flight reassign reuses the `ApproverSetEpoch` + `AdvanceNodeApproverSetEpochAsync` primitive from #283.1). #284.2 (pure resolver decorator, pre-Activation) may proceed in parallel with #283 since it touches no shared CAS surface. Both epics land behind the verified Wave-3 substrate; all CAS changes keep the optional-param/null discipline so existing predicates stay byte-identical (prompt-cache + concurrency-regression safety).

---

Key files referenced (absolute):
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Engine/GuardedTransition.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Engine/AllApprovalHandler.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Engine/WorkflowEngine.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Engine/SequentialApprovalHandler.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Engine/DefaultApproverResolver.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Engine/IApproverResolver.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Engine/WorkflowActionResult.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/Models/{NodeInstance,ApprovalTask,DelegationRule,Enums}.cs`,
`/Users/openclaw/.openclaw/shared/projects/WTM/src/WalkingTec.Mvvm.WorkFlow/WorkFlowOptions.cs`
(all verified on branch `origin/feat/wf-16-return-to-node`).