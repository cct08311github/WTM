# Wave-3 Design Addendum — WTM WorkFlow Engine (回退-to-node + Parallel/Inclusive Gateways)

> Status: FINAL. Supersedes the §5.7 / §12 deferred notes. Drives implementation of sub-issues #276 (WF-16) and #277 (WF-17).
> Grounded against: `ConcurrencySpikeTests.cs` (the ONLY proven concurrency primitive), `Models/{NodeInstance,ProcessInstance,WorkflowEventLog,Enums}.cs`, `GuardedTransition.cs`, `WorkflowEngine.cs`.

---

## 1. Backbone choice, grafts, and what the skeptics forced

### 1.1 Backbone — Design #2/#3 SUPERSEDE-NOT-DELETE (robustness 4, but the only one whose core primitive is *proven*)

Design #1 (Generation-as-epoch) scored 4 but its central claim — "the approve has already logically lost the instant the generation moved" — is **false under the repo's actual isolation**. The spike (`ConcurrencySpikeTests.cs:88-106`) proves exactly one thing: a **single-row, single-statement** `ExecuteUpdateAsync` with state+RowVer in the WHERE is atomic *with no surrounding transaction and no isolation-level dependency* (comment lines 5-9 explicitly reject SERIALIZABLE for SQLite/MySQL/Oracle/DaMeng portability). Design #1's epoch bump touches `ProcessInstance` while the in-flight approve touches `NodeInstance`, across a multi-statement envelope the engine **does not wrap in a transaction** — so under READ COMMITTED the approve can read `gOld`, win, advance, and *then* get span-surgered away (the CRITICAL lost-approval the skeptic exhibited).

The **supersede-not-delete** primitive (Designs #2 and #3) is the one move that is airtight on the proven primitive: discarding a span node is a **single-row terminal-state CAS on the same RowVer the approver uses**, so span-discard-vs-approve collapses into the *exact* `T_PROV_0_SQLite_ConcurrentCAS_ExactlyOneWinner` contest the spike already passes. `NodeInstance` is `BasePoco` and its own doc comment (`NodeInstance.cs:19-20`) already anticipates "superseded during 回退 re-entry … not soft-deleted" — we are honoring an existing invariant, not inventing one. We adopt this as the backbone.

### 1.2 Grafts onto the backbone

| Grafted idea | From | Why kept |
|---|---|---|
| **`Generation` (uint) epoch column** on NodeInstance/ApprovalTask/WorkflowTimer | #1 | The cleanest discriminator for "which rows belong to the live marking" after re-entry, and the multi-token marking query needs it. We keep the column but **never** rely on it for cross-transaction invalidation — see §3. |
| **`ReturnLoops` folded into the SAME CAS as the generation bump** | #1 (its single genuinely-airtight idea, all three skeptics agreed) | Loop accounting and epoch advance become one single-row statement → the spike's proven shape → Race D closed with zero isolation dependency. |
| **`Returning` instance sub-state as the return mutex** | #2 | Serializes concurrent returns on one instance row *before* any span surgery, so a losing return never commits destructive marks (closes #2's own "loser's committed collateral" break). |
| **Terminal `Superseded` node state (flips State, not just a marker)** | #3's skeptic fix | A superseded row leaves `State==Activated`-keyed predicates (esp. the no-RowVer advisory `IncrementNodeApprovedCount`) — so supersede MUST flip State to a terminal value, not merely stamp a column. |
| **Inclusive-join `ExpectedCount` pinned at fork time** + orphan fail-closed | #1/#2/#3 (consensus) | Avoids BPMN OR-join deadlock; fail-closed matches the engine's existing `FailClosedRouting` posture. |

### 1.3 Skeptic-found breaks that forced design changes (every one closed in §3–§4)

1. **Lost-approval / double-advance under READ COMMITTED (#1 CRITICAL).** Forced: the return runs inside **one explicit transaction the engine opens itself** (today it opens none — verified `WorkflowEngine.cs` AdvanceCore/ReturnToInitiator have no `BeginTransaction`; the only one is per-call inside `AppendAsync`), AND supersede is a same-RowVer CAS so even outside-txn approves resolve deterministically.
2. **`MAX(Seq)+1` collides without SERIALIZABLE (#2 HIGH, #3 confirmed).** Forced: **eliminate `MAX(Seq)+1` entirely.** Seq becomes an instance-row monotonic counter (`NextSeq`) bumped inside the same guarded instance CAS — single-row, portable, no isolation dependency. This is the biggest departure from the prior #269 design.
3. **Span-snapshot vs concurrent successor mint (#2/#3 HIGH "successor-token TOCTOU").** Forced: every `MintNodeInstanceAsync` is **gated on the instance still being in the pre-return generation** and is itself re-checked; a successor minted after the bump is stamped `gOld`, excluded from the live marking, and reaped. The mint becomes a *guarded* insert (unique index + generation gate), not a bare `Add`.
4. **Two-statement Join fire hangs/over-fires (#2/#3 HIGH).** Forced: Join fire is a **single-statement conditional CAS** (`WHERE ArrivedCount >= ExpectedCount AND State==Activated`), never a read-then-fire.
5. **Fired-timer FK aborts return; un-gen-guarded timer side-effects (#1 HIGH).** Forced: supersede-not-delete removes the FK hazard entirely (target row always exists); timer-fire action paths gain the generation gate so a fired `gOld` timer no-ops its node action.
6. **Instance wedged in `Returning` on crash (#2 MEDIUM).** Forced: `Returning` carries a `ReturningLeaseUtc`; a reaper (Wave-5 hosted service) reclaims an expired lease via CAS.
7. **Lost Return audit event between bump-commit and append (#3 MEDIUM).** Forced: the Return event is appended **inside the same transaction** as the bump (now possible because we own the transaction and Seq no longer needs its own Serializable wrapper).
8. **Boundary-straddling Join gen mismatch orphans tokens (#1 HIGH).** Forced: §3.2 scope rule — return targets must **dominate** the trigger node, so a discarded span is a clean sub-DAG and no Join straddles the generation boundary.

---

## 2. 回退-to-node (WF-16) mechanism — discard-and-recount

### 2.1 Public surface

```csharp
Task<WorkflowActionResult> ReturnToPrevAsync(Guid taskId, string actorITCode, string? reason, CancellationToken ct);
Task<WorkflowActionResult> ReturnToNodeAsync(Guid taskId, string targetNodeKey, string actorITCode, string? reason, CancellationToken ct);
```
Both funnel into one private `ExecuteReturnToNodeAsync`. `ReturnToPrev` resolves `targetNodeKey` = nearest completed upstream Approval node that **dominates** the trigger node on the pinned graph; `ReturnToNode` validates the explicit target is an upstream dominator (publish-time + runtime check, §3.2 risk).

### 2.2 The transaction envelope (the fix that makes the whole pipeline atomic)

```csharp
// The engine OWNS the transaction. Today nothing here opens one — this is new.
await using var tx = await _db.Database.BeginTransactionAsync(ct);   // provider default isolation; correctness does NOT depend on level
//   ... STEP 0..6, every mutation a single-row CAS ...
await tx.CommitAsync(ct);
// retry the whole envelope ≤3 on DbUpdateConcurrencyException (spec §7.3)
```
`WorkflowEventLogWriter.AppendAsync` detects the ambient transaction (`CurrentTransaction != null`, the existing `ownsTransaction=false` path) and **participates** — it no longer opens a Serializable txn and no longer computes `MAX(Seq)+1` (see Race B, §3.2).

### 2.3 Exact ordered steps — FK-safe, each a `GuardedTransition` CAS

**STEP 0 — VALIDATE + PIN (read-only, `AsNoTracking`).** Load trigger task, its node, the instance. Guard `node.State==Activated`, `actor==assignee`, `task.State==Pending`, `RejectPolicy ∈ {ReturnToPrev, ReturnToNode}`. Resolve `targetNodeKey`; compute `spanNodeKeys` = forward graph reachability from target (exclusive) to trigger (inclusive) over the pinned `DefinitionVersion.GraphJson` (pure, unit-tested function — §3.2 risk). Capture `gOld = instance.Generation`, `instanceRowVer`.

**STEP 1 — ENTER RETURNING (mutex + loop cap + epoch bump + Seq pre-allocation, ONE CAS — the linearization point).**
```
GuardedTransition.BeginReturnAsync:
  UPDATE ProcessInstance
  SET    State = Returning,
         Generation = Generation + 1,           -- gNew = gOld+1
         ReturnLoops = ReturnLoops + 1,
         ReturningLeaseUtc = @now + @leaseTtl,
         RowVer = RowVer + 1
  WHERE  ID == @id
    AND  State == Running
    AND  RowVer == @instanceRowVer
    AND  Generation == @gOld
    AND  ReturnLoops < @maxReturnLoops
```
`rows==1` → this caller owns the return epoch; `gOld` is now logically dead. `rows==0` → re-read to disambiguate: `ReturnLoops>=cap` → **fail-closed** (STEP 6-FC below); else a concurrent return already won → return `AlreadyHandled`. **This single statement closes Race D and is the only contended row in the whole pipeline.**

**STEP 2 — CANCEL TIMERS (before any node change; FK-safe regardless because nothing is deleted).**
```
GuardedTransition.CancelTimersForReturnAsync (per-row CAS, looped over span timers):
  UPDATE WorkflowTimer SET Status=Cancelled, RowVer=RowVer+1
  WHERE  ID==@id AND Status==Armed AND RowVer==@v
```
A poller that already Fired the timer → our CAS `rows==0` (no-op); the fired action targets a node we are about to supersede and is gen-gated (Race C, §3).

**STEP 3 — DISCARD APPROVAL-TASK SPAN (soft, `ApprovalTask` carries lifecycle states).**
```
GuardedTransition.DiscardTasksForReturnAsync (per-row CAS):
  UPDATE ApprovalTask SET State=Cancelled, RowVer=RowVer+1
  WHERE  ID==@id AND NodeInstanceId IN @spanNodeIds
    AND  State IN (NotYetActive, Pending, Suspended, AddedPending) AND RowVer==@v
```
加签 (`IsRuntimeInjected`) tasks included. Already-decided tasks left intact for audit. The **trigger task** is CAS-claimed `Pending→Rejected` (records the return decision).

**STEP 4 — SUPERSEDE NODE SPAN (the critical CAS; Race A).** For each live span node:
```
GuardedTransition.SupersedeNodeAsync (per-row CAS, SAME RowVer an approver uses):
  UPDATE NodeInstance SET State=Superseded, SupersededAtGen=@gOld, RowVer=RowVer+1
  WHERE  ID==@id AND State IN (Activated, Pending) AND RowVer==@v
```
**Flips State to terminal `Superseded`** (NOT a bare marker — closes the #3 skeptic break where `State==Activated` predicates re-fire on a superseded row). If an approver already completed the node, our CAS gets `rows==0`; that completed-but-in-span node is gen-stale (its `Generation==gOld < instance.Generation`) so the live-marking query excludes it; we additionally CAS `CompletedApproved→Superseded` for timeline determinism. The row is **never deleted**, so a late approver's CAS always finds a row and resolves to a clean `AlreadyHandled`.

**STEP 5 — RE-MATERIALIZE TARGET (guarded mint; closes successor-TOCTOU).**
```
GuardedTransition.MintNodeInstanceGuardedAsync:
  -- insert is idempotent via UNIQUE (TenantCode, InstanceId, NodeKey, Generation)
  -- and gated: re-read instance, proceed ONLY IF State==Returning AND Generation==@gNew
  INSERT NodeInstance(... NodeKey=@target, Generation=@gNew, State=Pending, RowVer=0 ...)
```
`ReturnResetMode.Reset` (default) → `ApprovedCount=0`. `Resume` (opt-in) → copy surviving decided approvals **from the immediately-prior generation only**, re-validating each approver still resolves under the current rule (§5 risk; compliance-sensitive, CHANGELOG opt-in).

**STEP 6 — APPEND + UNLOCK + RE-ROUTE (all in-txn).**
`AppendAsync(EventAction.Return, BeforeState=gOld, AfterState=gNew)` — participates in the ambient txn, Seq via the instance counter (§3 Race B). Then:
```
GuardedTransition.AdvanceProcessInstanceAsync:
  UPDATE ProcessInstance SET State=Running, ReturningLeaseUtc=NULL, RowVer=RowVer+1
  WHERE  ID==@id AND State==Returning AND RowVer==@v
```
Commit. `AdvanceCoreAsync` then drives the `gNew` marking (which only sees `gNew`, non-superseded nodes).

**STEP 6-FC — FAIL-CLOSED (loop cap exhausted).** `AppendAsync(FailClosed, reason="MaxReturnLoops exceeded")` + `GuardedTransition` CAS `Running→Terminated`, in-txn.

### 2.4 Crash recovery
If the process dies mid-envelope, the transaction rolls back atomically — no partial return. The *only* persisted intermediate is `State==Returning`; if a crash lands after commit of STEP 1 in a degenerate re-entrant retry, the **lease reaper** (Wave-5) reclaims it (§3 Race D addendum).

---

## 3. The four race resolutions — exact guard that makes each airtight

### Race A — span-discard vs in-flight approve/reject (T-WAVE3-1) — CRITICAL, CLOSED

**Guard:** `SupersedeNodeAsync` is `WHERE State IN (Activated,Pending) AND RowVer==@v → SET State=Superseded, RowVer+1`. The approver's `CompleteNodeInstanceAsync` is `WHERE State==Activated AND RowVer==@v`. **Same physical row, same RowVer expectation** → the DB serializes them at statement level (proven by `T_PROV_0_SQLite_ConcurrentCAS_ExactlyOneWinner`). Exactly one wins; loser `rows==0 → AlreadyHandled`.

Why the #1-skeptic's lost-approval cannot occur here: there is **no separate epoch read on a different row** that an approve can win against. The contest is entirely on the NodeInstance row both actors target. And because we **flip State to `Superseded`** (not leave `Activated`), the no-RowVer advisory `IncrementNodeApprovedCountAsync` (`WHERE State==Activated`) auto-no-ops the instant supersede commits — no orphaned count crosses a threshold on a dead node. Re-materialized target is a **new row (new ID, `Generation=gNew`)**, so a stale approver physically cannot reach it.

### Race B — Seq monotonicity after re-entry (T-WAVE3-2) — CLOSED by eliminating MAX(Seq)+1

**The #269 `MAX(Seq)+1` pattern is removed.** Both skeptics showed it collides without SERIALIZABLE, which the portability layer forbids. Replacement:

- New column **`ProcessInstance.NextSeq (int, default 1)`**.
- `AppendAsync` allocates Seq via a **single-row guarded CAS** that returns the pre-increment value:
```
UPDATE ProcessInstance SET NextSeq = NextSeq + 1, RowVer = RowVer + 1
WHERE  ID==@id AND RowVer==@v        -- the event takes the OLD NextSeq
```
The Seq counter rides the instance row's own CAS — **single-row, single-statement, no isolation dependency, exactly the spike shape.** Two concurrent appends contend on the instance RowVer; one wins and takes Seq=k, the other retries and takes Seq=k+1. Contiguous, monotonic, gap-free, append-only. Re-entry never resets it (discarded-span events keep their Seq; Return takes the next). A nullable **`WorkflowEventLog.Generation`** column tags the epoch for audit grouping only — it never enters Seq math.

> Note: this couples Seq allocation to the instance RowVer, so within-instance event throughput is serialized on that row. That is **intentional** — Seq *is* the per-instance ordering guarantee. Cross-instance throughput is unaffected. (Benchmark item in §6.)

### Race C — timer cancellation ordering (T-WAVE3-3) — CLOSED

**Two-part:** (1) STEP 2 cancels timers before STEP 4 supersedes their FK'd nodes; (2) **supersede-not-delete removes the FK constraint hazard entirely** — `WorkflowTimer.NodeInstanceId` always points at a still-existing (now `Superseded`) row, so the `OnDelete(Restrict)` FK is never exercised and a Fired-but-uncancelled timer can never abort the return (this is the exact #1-skeptic HIGH break, structurally eliminated).
Cancel CAS: `WHERE Status==Armed AND RowVer==@v → Cancelled`. Wave-5 fire CAS: `WHERE Status==Armed AND RowVer==@v → Fired`. One wins. A fire that wins on a `gOld` timer then runs its action through `CompleteNodeInstanceAsync`, which **gains `AND Generation==@curGen`** — `gOld != gNew` → `rows==0`, idempotent no-op. Remind/Escalate side-effects also gate on `Generation==@curGen` so no spurious notification fires for a superseded node (the #1-skeptic's second HIGH defect).

### Race D — MaxReturnLoops accounting (T-WAVE3-4) — CLOSED

**The increment is folded into the STEP-1 `BeginReturnAsync` CAS** (`SET ReturnLoops=ReturnLoops+1, Generation=Generation+1, State=Returning` guarded by `ReturnLoops < MaxReturnLoops AND Generation==@gOld AND RowVer==@v`). You cannot advance the epoch or take the Returning lock without counting the loop, in one single-row statement. Two concurrent returns both read `gOld`/`rowVer`; first wins, second's `RowVer`/`Generation` predicate fails → `rows==0 → AlreadyHandled`, never touching the counter. Cap reached → predicate `ReturnLoops < cap` fails → `rows==0` → fail-closed terminal. No double-count, no double-discard, no lost-update, no isolation dependency.

**Crash addendum (the #2-skeptic's wedged-`Returning` break):** `ReturningLeaseUtc` is set in STEP 1 and cleared in STEP 6. A Wave-5 reaper reclaims an expired lease:
```
UPDATE ProcessInstance SET State=Running, ReturningLeaseUtc=NULL, RowVer=RowVer+1
WHERE  ID==@id AND State==Returning AND ReturningLeaseUtc < @now AND RowVer==@v
```
Single-row CAS; the original (dead) owner's later commit fails its own RowVer guard.

---

## 4. Parallel/Inclusive gateways + Join + Ack (WF-17)

### 4.1 Token / marking model

A **token = a `NodeInstance` with `State ∈ {Pending, Activated}` AND `Generation == instance.Generation` AND `State != Superseded`.** The rows ARE the marking (no side table). `AdvanceCoreAsync`'s single-token `FirstOrDefault` (`WorkflowEngine.cs:290`) becomes a **deterministic `OrderBy(ID)` drain loop over the live current-gen marking**. For single-token graphs the set has one element → byte-identical behavior → **Red-Line "never silently change default behaviour" satisfied** (existing graphs have `Generation==0`, no node ever superseded). **Every active-token query MUST filter `Generation == instance.Generation`** — a missing filter would re-activate stale-epoch tokens (the #1-skeptic's leaked-orphan break; enforced by conformance test T-RET-5).

### 4.2 Fork

`NodeKind.Condition` (exclusive, existing first-match) is unchanged. New split semantics:
- **Parallel (AND):** mint ALL outgoing branch tokens.
- **Inclusive (OR):** mint each branch whose `TransitionDef.Condition` evaluates true via the existing `WhitelistRoutingEvaluator`; fail-closed if zero match and no default.

All N minted in ONE in-txn `SaveChanges`, each stamped `Generation`, `ForkGroupId (Guid)`, `JoinNodeKey`. Mints are idempotent under retry/re-entry via `UNIQUE (TenantCode, InstanceId, NodeKey, Generation)`. **At fork time the engine stamps the Join's `ExpectedArrivals` = count of branches actually activated** (pins OR-join arity → no BPMN inclusive-join deadlock).

### 4.3 Join — completion is a SINGLE-STATEMENT conditional CAS

First arrival mints the Join `Pending` idempotently (unique index → one INSERT wins, losers read it). Each arriving token: completes itself (`Activated→Superseded`, merging into the Join) **and** increments arrival:
```
GuardedTransition.IncrementJoinArrivedAsync:
  UPDATE NodeInstance SET JoinArrivedCount = JoinArrivedCount + 1, RowVer = RowVer + 1
  WHERE  ID==@joinId AND State==Activated AND Generation==@g AND RowVer==@v
```
Then the **fire is one statement** — read and fire are NOT separate (closes the #2/#3 two-statement hang/over-fire break):
```
GuardedTransition.FireJoinIfSatisfiedAsync:
  UPDATE NodeInstance SET State=CompletedApproved, RowVer=RowVer+1
  WHERE  ID==@joinId AND State==Activated AND Generation==@g
    AND  JoinArrivedCount >= JoinExpectedArrivals AND RowVer==@v
```
`rows==1` → this token fires the Join and mints its single successor; `rows==0` → not yet satisfied or another token fired it. Exactly-once by construction.

### 4.4 Orphan-token fail-closed (Join never hangs)

A forked token reaching a terminal **non-arriving** state (`Superseded` via return, `CompletedRejected`, `FailClosedRouting`) decrements expectation **in the same CAS transaction** that terminates it:
```
GuardedTransition.DecrementJoinExpectedAsync:
  UPDATE NodeInstance SET JoinExpectedArrivals = JoinExpectedArrivals - 1, RowVer = RowVer + 1
  WHERE  ID==@joinId AND State==Activated AND Generation==@g
    AND  JoinExpectedArrivals > JoinArrivedCount AND RowVer==@v   -- underflow-protected
```
After decrement, attempt `FireJoinIfSatisfiedAsync` so the Join fires the instant it becomes satisfiable. The decrement is **centralized in the `GuardedTransition` terminal-transition helpers** so every termination path (return/reject/timeout) hits it (the #2-skeptic's "miss one path → hang" break).

**Authoritative backstop (closes counter-drift, #3-skeptic):** `CanFire` is re-derived from a query on each Join evaluation — `live current-gen non-terminal cohort tokens still able to reach the Join` (graph reachability over the marking). If that set is empty AND `JoinArrivedCount < JoinExpectedArrivals` → Join is **unsatisfiable** → fail-closed: `Activated→CompletedRejected` CAS + `EventAction.FailClosed` + RejectPolicy routing. The query is source of truth; the counters are an optimization. The Wave-5 reconciliation sweep is a third backstop. **Because superseded nodes flip to terminal `Superseded` (not `Activated`), the "still-live cohort" query has a clean signal** (the #3-skeptic's ambiguous-`Activated` break is gone).

**Scope rule (closes boundary-straddling Join, #1-skeptic HIGH):** return targets must **dominate** the trigger node — every path to the trigger passes through the target — so a discarded span is a clean sub-DAG and no fork/Join straddles the generation boundary. A return into the middle of an open parallel region is **out of scope for Wave-3** (publish-time validation rejects non-dominating targets; runtime fail-closes; CHANGELOG documents the limit).

### 4.5 Ack (blocking) vs Cc (non-blocking)

- **`NodeKind.Ack`** (already in enum): `AckHandler.OnEnterAsync` mints `ApprovalTask` rows (assignee=acknowledger) reusing the task machinery; `CanCompleteAsync==false` until required acks claimed (`Pending→Approved` via existing `ClaimApprovalTaskAsync`); `AckMode ∈ {All, Any, Quorum}` mirrors `ApproveMode`. **Blocks the token.** Generation-stamped → a return discards un-acked Ack tokens with the span.
- **`NodeKind.Cc`** (already in enum): mints `CcRecord` rows, `CanCompleteAsync==true` immediately. **Never holds a token.**

Explicit handler tests assert Ack holds and Cc never does (the #2-skeptic's Ack/Cc divergence break).

### 4.6 Invariant
Every fork-multi-mint, join-mint, arrival-increment, expected-decrement, join-fire, supersede, timer-cancel, and Seq allocation is a `GuardedTransition` single-row CAS (or unique-index idempotent INSERT for first mint). No raw `ExecuteUpdateAsync` outside `GuardedTransition`. The WTM single-CAS-primitive audit surface holds.

---

## 5. New entities / fields / migration impact (consumer-owned, mirror Etl)

All schema is **consumer-owned** and mapped in `ApplyWorkFlowModels` for all 7 `DBTypeEnum` providers, mirroring how `Etl` owns its tables. No core WTM types change.

**Enum additions** (`Models/Enums.cs`):
- `NodeState`: `+ Superseded` (terminal: span-discarded or merged-into-Join).
- `InstanceState`: `+ Returning` (return mutex sub-state, lease-guarded).
- `NodeKind`: `+ ParallelGateway, InclusiveGateway` (`Join`, `Ack`, `Cc`, `Condition` already exist).
- `EventAction`: already has `Return`, `FailClosed`, `TimeoutFire` — sufficient.
- `WorkflowActionCode` (result union): `+ AlreadyHandled, MaxReturnLoopsExceeded, JoinUnsatisfiable, Returned`.

**`ProcessInstance`** (`PersistPoco`): `+ Generation (uint, default 0)`, `+ ReturnLoops (uint, default 0)`, `+ NextSeq (int, default 1)`, `+ ReturningLeaseUtc (DateTime?, null)`.
**`NodeInstance`** (`BasePoco`): `+ Generation (uint, default 0)`, `+ SupersededAtGen (uint?, null)`, `+ ForkGroupId (Guid?, null)`, `+ JoinNodeKey (string?, null)`, `+ JoinExpectedArrivals (int, default 0)`, `+ JoinArrivedCount (int, default 0)`.
**`ApprovalTask`** (`PersistPoco`): `+ Generation (uint, default 0)`.
**`WorkflowTimer`**: `+ Generation (uint, default 0)`. (Status enum already sufficient.)
**`WorkflowEventLog`** (`BasePoco`): `+ Generation (int?, null)` — audit grouping only; **`Seq` math no longer uses `MAX`**.

**Indexes:** `UNIQUE (TenantCode, InstanceId, NodeKey, Generation)` on `NodeInstance` (idempotent fork/Join/re-materialize mint). **Non-filtered** (avoid partial-index portability gaps on MySQL/Oracle — §migration risk); superseded rows occupy slots, acceptable.

**New `GuardedTransition` methods** (all single-row CAS): `BeginReturnAsync`, `CancelTimersForReturnAsync`, `DiscardTasksForReturnAsync`, `SupersedeNodeAsync`, `MintNodeInstanceGuardedAsync`, `AllocateSeqAsync`, `IncrementJoinArrivedAsync`, `DecrementJoinExpectedAsync`, `FireJoinIfSatisfiedAsync`, `ReclaimReturningLeaseAsync`. Existing `Activate/Complete/IncrementApproved/ClaimTask` predicates **gain `AND Generation==@g`**.

**Migration (critical — backfill or strand live instances):** one EF migration adds columns with `HasDefaultValue` so **all existing rows backfill `Generation=0, ReturnLoops=0, NextSeq = (current MAX(Seq) per instance)+1, SupersededAtGen=NULL`** atomically. `NextSeq` backfill MUST seed from each instance's existing max event Seq or the first post-upgrade append collides. Risk surfaced by all three designs; covered by T-MIG-1 (§6).

**Schema/CHANGELOG discipline:** new behavior is opt-in (multi-token only triggers on gateway nodes; `ReturnResetMode.Resume` opt-in). `CHANGELOG.md` entry with migration notes + the dominator-only return-target limitation. `Resume` flagged compliance-sensitive.

---

## 6. Concurrency test matrix (MUST pass — real SQLite shared-memory, NOT EF InMemory)

Per repo lesson, release CI runs **`WalkingTec.Mvvm.WorkFlow.Test` AND `Mvc.Tests`**, not just Core.Test. EF InMemory cannot model RowVer WHERE serialization or unique indexes — use the `efcore-sqlite-shared-memory-test` pattern (keep-alive connection, per-thread `DbContext`), exactly as `ConcurrencySpikeTests.cs`.

**Return races (T-RET-\*):**
- **T-RET-1** (Race A, the headline): node Activated; Thread-A `SupersedeNodeAsync`, Thread-B `CompleteNodeInstanceAsync(Approve)`, same RowVer, ×20 rounds → exactly one wins; assert the loser's approval is NOT silently lost (either it completed and is gen-stale-excluded, or it no-op'd `AlreadyHandled`); assert advisory `ApprovedCount` never crosses threshold on a `Superseded` node.
- **T-RET-2** (Race D): two concurrent `ReturnToNodeAsync` same instance → exactly one bumps `Generation`+`ReturnLoops`; loser `AlreadyHandled`; loser commits **no** destructive supersede marks (assert span rows untouched by the loser).
- **T-RET-3** (Race D cap): drive `ReturnLoops` to `MaxReturnLoops`; next return fail-closes `Running→Terminated` + `FailClosed` event; assert deterministic, no infinite loop.
- **T-RET-4** (Race C): poller fires a span timer `Armed→Fired` concurrently with the return; assert no FK abort (row superseded not deleted), fired `gOld` timer action no-ops via `Generation==@curGen`, no spurious Remind.
- **T-RET-5** (successor TOCTOU + stale-epoch leak): approve wins on a sibling `gOld` node and mints its successor concurrently with the bump; assert the `gOld` successor is excluded from the `gNew` marking and reaped; assert no stale-epoch token ever activates.
- **T-RET-6** (Seq, Race B): 8 concurrent appends on one instance (mix of return + sibling advance) → Seq contiguous 1..n, no dup, no gap; re-entry continues monotonically.
- **T-RET-7** (crash recovery): simulate crash after STEP 1 commit; reaper reclaims expired `Returning` lease; assert instance returns to `Running` and is re-drivable; assert original owner's stale commit fails its RowVer guard.
- **T-RET-8** (audit atomicity): assert the `Return` event commits in the same txn as the bump — no generation jump without a `Return` row.

**Join / gateway races (T-JOIN-\*):**
- **T-JOIN-1**: 3-branch AND-fork, all arrive concurrently → Join fires exactly once (`FireJoinIfSatisfiedAsync` single-statement), exactly one successor minted; ×20 rounds.
- **T-JOIN-2** (inclusive arity): OR-fork activates 2 of 3 → `ExpectedArrivals==2`; Join fires on 2 arrivals, never waits for the 3rd.
- **T-JOIN-3** (orphan decrement): one branch `CompletedRejected` (no instance-terminate) concurrently with siblings arriving → `DecrementJoinExpectedAsync` keeps it satisfiable; Join fires; no hang.
- **T-JOIN-4** (unsatisfiable fail-closed): all branches die → reachability backstop fires Join `CompletedRejected` + `FailClosed`; never hangs, never silent-advances.
- **T-JOIN-5** (decrement-vs-fire): last arrival's increment races the last arm's decrement → exactly one fire, no over-fire/under-fire (single-statement fire makes this deterministic).
- **T-JOIN-6** (return into fork region): return whose dominating target is upstream of a fork → whole region superseded at `gOld`, re-materialized at `gNew`; assert no `gOld` Join orphans `gNew` tokens.
- **T-ACK-1**: Ack node blocks token until acks claimed (`AckMode=All/Any/Quorum`); Cc node never holds a token.

**Provider + migration:**
- **T-PROV-W3-\***: re-run the conformance suite for every new compound-WHERE CAS on all 6 relational providers (esp. DaMeng/达梦, Oracle FOR-UPDATE fallback §7.5) — the multi-column `AND Generation==@g` could translate differently than the single-RowVer spike; **ship-blocker if any provider phantom-no-ops the compound guard.**
- **T-MIG-1**: upgrade a pre-Wave-3 DB with in-flight instances; assert `Generation=0`/`NextSeq=MAX+1` backfill lets the first post-upgrade CAS and the first append succeed (a partial backfill strands every running instance).

---

## 7. Two file-scoped sub-issue scopes

### #276 — WF-16 回退-to-node (discard-and-recount via supersede + epoch + instance-Seq)

**Goal:** `ReturnToPrev`/`ReturnToNode` with span supersession, generation/loop accounting in one CAS, instance-counter Seq, crash-recovery lease.

**Files (create/modify):**
- `src/WalkingTec.Mvvm.WorkFlow/Models/Enums.cs` — `+ NodeState.Superseded`, `+ InstanceState.Returning`, `+ WorkflowActionCode` members.
- `src/.../Models/ProcessInstance.cs` — `+ Generation, ReturnLoops, NextSeq, ReturningLeaseUtc`.
- `src/.../Models/NodeInstance.cs` — `+ Generation, SupersededAtGen`.
- `src/.../Models/ApprovalTask.cs` / `WorkflowTimer.cs` — `+ Generation`.
- `src/.../Models/WorkflowEventLog.cs` — `+ Generation (audit-only)`.
- `src/.../Engine/GuardedTransition.cs` — `+ BeginReturnAsync, CancelTimersForReturnAsync, DiscardTasksForReturnAsync, SupersedeNodeAsync, MintNodeInstanceGuardedAsync, AllocateSeqAsync, ReclaimReturningLeaseAsync`; add `AND Generation==@g` to existing predicates.
- `src/.../Engine/WorkflowEngine.cs` — `ExecuteReturnToNodeAsync` (STEP 0–6 + 6-FC), engine-owned transaction envelope, `ReturnToPrevAsync`/`ReturnToNodeAsync` entry points; replace `AppendAsync` Seq source.
- `src/.../Engine/WorkflowEventLogWriter.cs` — drop `MAX(Seq)+1`; consume `AllocateSeqAsync`; honor ambient txn (`ownsTransaction=false`).
- `src/.../Definition/{WorkflowGraphValidator.cs, WorkflowGraphSchema.cs}` — dominator-set computation, publish-time return-target validation, span reachability function.
- `src/.../ServiceCollectionExtensions.cs` (`ApplyWorkFlowModels`) — new columns + unique index, 7-provider mapping; EF migration with backfill defaults.
- `test/WalkingTec.Mvvm.WorkFlow.Test/ReturnToNodeTests.cs` + `ReturnConcurrencyTests.cs` — T-RET-1…8, T-MIG-1, T-PROV-W3-\*.

### #277 — WF-17 parallel/inclusive gateways + Join + Ack (multi-token marking)

**Goal:** multi-token `AdvanceCoreAsync`, AND/OR fork, single-statement Join CAS, orphan fail-closed, blocking Ack.

**Files (create/modify):**
- `src/.../Models/Enums.cs` — `+ NodeKind.{ParallelGateway, InclusiveGateway}`, `+ AckMode` (or reuse `ApproveMode`).
- `src/.../Models/NodeInstance.cs` — `+ ForkGroupId, JoinNodeKey, JoinExpectedArrivals, JoinArrivedCount`.
- `src/.../Engine/GuardedTransition.cs` — `+ IncrementJoinArrivedAsync, DecrementJoinExpectedAsync, FireJoinIfSatisfiedAsync`; centralize expected-decrement in terminal-transition helpers.
- `src/.../Engine/WorkflowEngine.cs` — relax active-node `FirstOrDefault` → current-gen `OrderBy(ID)` drain loop; fork multi-mint; Join evaluation + reachability backstop; fail-closed routing.
- `src/.../Engine/{ParallelGatewayHandler.cs, InclusiveGatewayHandler.cs, JoinHandler.cs, AckHandler.cs}` (new); `CcHandler.cs` (verify non-blocking).
- `src/.../Definition/{WorkflowGraphValidator.cs, WorkflowGraphSchema.cs}` — fork↔Join pairing, `JoinNodeKey`/arity metadata, publish-time validation (every fork has a reachable paired Join).
- `src/.../ServiceCollectionExtensions.cs` — Join/fork column mapping + reuse the `NodeInstance` unique index.
- `test/WalkingTec.Mvvm.WorkFlow.Test/{ParallelGatewayTests.cs, JoinTests.cs, AckCcTests.cs}` + `JoinConcurrencyTests.cs` — T-JOIN-1…6, T-ACK-1.

**Sequencing:** land **#276 first** (the epoch/`Generation` column + supersede primitive + multi-token-ready marking query are prerequisites for #277's drain loop and Join generation-gating). Both touch `ServiceCollectionExtensions.cs`, `GuardedTransition.cs`, `WorkflowEngine.cs`, `Enums.cs` → **serialize the two PRs or merge dotnet10 between them** to avoid the single-runner ETL-style conflict noted in MEMORY.

---

**Net:** backbone = supersede-not-delete (the only spike-proven move); grafted = generation epoch + ReturnLoops-in-CAS + Returning-mutex + pinned-Join-arity; every skeptic break closed by (1) an engine-owned transaction the code currently lacks, (2) replacing `MAX(Seq)+1` with an instance-row counter CAS, (3) flipping supersede to a terminal `State` so no `Activated`-keyed predicate re-fires, (4) single-statement Join fire, (5) generation-gated mint + lease reaper. No race relies on an isolation level the portability layer forbids.