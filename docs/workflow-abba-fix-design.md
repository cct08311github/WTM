# ABBA Deadlock Fix Design — #290: Return-txn lock-order resolution — Final Synthesized Spec

> Status: APPROVED FOR IMPLEMENTATION. Synthesized 2026-06-13 from three adversarially-generated proposals (A: global lock-order unify via instance-pin; B: split return STEP-1 into its own committed transaction; C: accept-the-cycle + provider-deadlock victim-retry) + skeptic adjudication, then re-verified line-by-line against `origin/dotnet10` @7c8007397 (v10.12.0 + batch-1 merged).
> Every load-bearing claim in this document was checked against the shipped code: `Engine/WorkflowEngine.cs` (ExecuteReturnToNodeAsync :2435/:2491, DelegateTaskAsync :3172/:3260, AddApproverAsync :2928/:3025, RevokeDelegationAsync :3409), `Engine/GuardedTransition.cs` (BeginReturnAsync :115, AllocateSeqAsync :277, AdvanceProcessInstanceAsync :75, ReclaimReturningLeaseByRowVerAsync :1146, ReassignTaskAssigneeAsync :873, AddApproversToNodeAsync :782, AdvanceNodeApproverSetEpochAsync :821, EscalateTaskAssigneeAsync :1191), `Engine/WorkflowEventLogWriter.cs` (AppendAsync :78 / AllocateSeqWithRetryAsync :156), `Engine/WorkflowTimerExecutor.cs` (ProcessTimerAsync fire txn :320, GATE-0 :222-287, HandleEscalateAsync :1138, ReclaimExpiredLeasesAsync :1072).
> This change is a correctness fix to the most safety-critical concurrency surface in the engine. It is published-surface additive: no removed members, no behavior change on SQLite (the unit-test substrate), no isolation-level dependency, all existing T-RET-*/T-DEL-*/T-ADD-*/T-MIX-* suites stay green.
> Maintainer-only document (deadlock-cycle constructions + lock-order race analysis — not for public mirror). Excluded from the GitHub mirror via `.sync/github-excludes.txt`, same as the Wave-3/4/5/6 addenda.

---

## 0. Synthesis verdicts (contested points)

| Point | Verdict | Why |
|---|---|---|
| Which proposal survives the skeptic gauntlet | **B (split return STEP-1 into its own committed transaction)** as primary | B is the only proposal that cannot be broken on correctness AND eliminates the *root cause* — the return txn is the **sole** multi-row txn that takes `ProcessInstance` **first**; every other takes it **last** (via the Seq counter). Splitting STEP-1 out makes the return acquire `ProcessInstance` last in txB, unifying all txns on instance-last. |
| Proposal A (global instance-pin first) | **Rejected as primary** | A's pin genuinely serializes #290's Delegate-vs-Return pair, but A's own recommended edit (pin the instance inside the reaper escalate branch *after* `FireTimerAsync`) **widens** the pre-existing `(WorkflowTimer, ProcessInstance)` ABBA between reaper-escalate and the return's STEP-2 timer-cancel — it lengthens the window in which escalate holds the Timer and wants the Instance. A also touches 4 production methods (return + reaper + delegate + addApprover) for the same #290 elimination B achieves by touching **one** method. |
| Proposal C (accept cycle + victim-retry) | **Demoted to defense-in-depth backstop, NOT the #290 fix** | C does not fix the ordering bug. Its provider-deadlock classifier carries a **placeholder DaMeng code** — and DaMeng is a primary-market ship-blocker — so on the most important provider C delivers zero improvement, undetectable on SQLite. C also regresses legitimate operations: the longer 6-step return txn is the systematically-chosen deadlock victim against the shorter 3-statement delegate, so a human 回退 can hit `DeadlockRetryExhausted` under sustained delegation pressure. C is adopted **only** as a thin envelope around the residual Delegate-vs-AddApprover `(Node, Task)` cycle that B scopes out — explicitly framed as a backstop, with the DaMeng-degrades-to-status-quo limitation documented. |
| Canonical lock order (Q1) | **Keep the wave-5 §0 declared order `WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance(Seq)`; make the return txn genuinely conform via the STEP-1 split** | The declared order is correct and already honored by Delegate, AddApprover, and the reaper-escalate path (verified). The return txn is the only violator. Fixing the violator (B) is strictly less risk than re-basing every other txn onto a new instance-first order (A). |
| Residual Delegate-vs-AddApprover `(Node, Task)` cycle | **Out of scope for #290; tracked as a separate follow-up issue (WF-290.2)**; mitigated meanwhile by the C-backstop envelope | This pre-existing cycle (Delegate = Task→Node, AddApprover = Node→Task) is independent of the return txn. B neither creates nor cures it. The correct long-term fix is to make AddApprover acquire Task-before-Node so all human txns share one total order — additive, low-risk, but its own change. |
| Verification reality (Q3) | **Structural lock-order + semantic preservation provable on SQLite NOW; actual concurrent deadlock-free proof is #270-gated** | SQLite serializes the whole DB to one writer, so ABBA is *structurally impossible* there — it can never reproduce the deadlock. B's correctness rests on a code-inspection lock-order argument (instance-last-everywhere) that is independent of the live run. The deadlock-free proof on SqlServer/PgSql/MySql/Oracle/DaMeng is added now as skip-clean `[TestCategory("ProviderConformance")]` stubs (#270 pattern). |
| Blast radius (Q4) | **Minimal: one production method (`ExecuteReturnToNodeAsync`) restructured; no CAS predicate, no isolation level, no Seq logic changed** | The only new hazard is a non-atomic boundary between committed-txA and txB. That boundary lands precisely on the **already-existing** Returning-lease crash-recovery path (`ReclaimExpiredLeasesAsync` → `ReclaimReturningLeaseByRowVerAsync`), which already covers a host crash mid-return. B introduces no new recovery machinery. |

---

## 1. The bug, re-derived from shipped code

### 1.1 Inventory of multi-row transactions (the only possible ABBA parties)

`git grep BeginTransactionAsync` over `src/WalkingTec.Mvvm.WorkFlow/` yields exactly four runtime transactions that hold more than one of `{ProcessInstance, NodeInstance, ApprovalTask, WorkflowTimer, WorkflowEventLog}` across a wait. `ProcessDefinitionPublisher` only touches definition tables; `WorkflowEventLogWriter.AppendAsync` opens a txn only when none is ambient (single-statement Seq+INSERT, self-releasing). Verified held-lock orders:

| Txn | Entrypoint / txn-open | Held-lock acquisition order | Instance position |
|---|---|---|---|
| **ExecuteReturnToNodeAsync** (回退) | `WorkflowEngine.cs:2435` / txn `:2491` | **ProcessInstance** (STEP-1 BeginReturnAsync `:2518`) → WorkflowTimer (STEP-2 `:2546`) → ApprovalTask (STEP-3 `:2553` + trigger claim `:2570/:2619`) → NodeInstance (STEP-4 SupersedeNode `:2652`, STEP-5 Mint `:2694`) → **ProcessInstance again** (STEP-6 `:2711` + event-log AllocateSeq `:2738`) | **FIRST** (and held to commit) |
| **DelegateTaskAsync** (委托/转办) | `WorkflowEngine.cs:3172` / txn `:3260` | ApprovalTask (5a Reassign `:3316`) → NodeInstance (5b epoch `:3351`) → **ProcessInstance** (5c AllocateSeq `:3364`) | **LAST** |
| **AddApproverAsync** (加签) | `WorkflowEngine.cs:2928` / txn `:3025` | NodeInstance (6a AddApprovers `:3043`) → ApprovalTask (6b shift+INSERT `:3094/:3136`) → **ProcessInstance** (6c AllocateSeq `:3141`) | **LAST** |
| **Reaper fire — escalate** (WF-20) | `WorkflowTimerExecutor.cs:208` / txn `:320` | WorkflowTimer (FireTimer `:324`) → ApprovalTask (Escalate `:1327`) → NodeInstance (epoch `:1351`) → **ProcessInstance** (AllocateSeq `:1356`) | **LAST** |

Single-statement-autocommit paths that hold nothing across a wait (verified NOT ABBA parties): `RevokeDelegationAsync` (`:3409`, no txn — per-row autocommit), `SweepExpiredAtActionDelegationsAsync`, `ReclaimExpiredLeasesAsync` (`:1072`, per-instance single-row CAS), and all human Approve/Reject/Withdraw paths (each GuardedTransition CAS autocommits).

**The universal "last lock":** every non-return multi-row txn ends by calling `WorkflowEventLogWriter.AppendAsync` → `AllocateSeqWithRetryAsync` → `AllocateSeqAsync`, which issues an `ExecuteUpdateAsync` on the `ProcessInstance` row (`GuardedTransition.cs:298-303`, `SET NextSeq+1, RowVer+1`) inside the ambient txn, taking and holding the instance write-lock until commit. This is the second lock #290 names.

### 1.2 The #290 cycle (Delegate-vs-Return)

Two rows, one instance: ROW-A = the `ProcessInstance`, ROW-B = an `ApprovalTask` on a node inside the return span.

```
T1 = Delegate         T2 = Return (same instance, concurrent)
─────────────────     ──────────────────────────────────────
holds ROW-B           holds ROW-A
  ReassignTask          BeginReturnAsync (STEP-1, held whole txn)
  WorkflowEngine.cs:3316  WorkflowEngine.cs:2518
waits ROW-A           waits ROW-B
  AllocateSeq (5c)      DiscardTasksForReturn (STEP-3) / trigger claim
  WorkflowEngine.cs:3364  WorkflowEngine.cs:2553 / :2570/:2619
```

`T1 holds Task, waits Instance` × `T2 holds Instance, waits Task` = classic 2-row ABBA. On SqlServer/PgSql/MySql/Oracle/DaMeng the engine picks a victim and aborts it; the loser's exception propagates raw through `catch { await tx.RollbackAsync; throw; }` (`WorkflowEngine.cs:2756-2760` / `:3392-3396`) to the caller. On SQLite it cannot manifest (single-writer).

### 1.3 The same root cause spawns three more cycles

- **AddApprover-vs-Return** — AddApprover holds NodeInstance (`:3043`) and a new ApprovalTask (`:3136`), waits Instance Seq (`:3141`); Return holds Instance, waits ApprovalTask (STEP-3) and NodeInstance (STEP-4 SupersedeNode). Two independent 2-row cycles on `(Instance, Task)` and `(Instance, Node)`. Same root: return = instance-first.
- **Reaper-escalate-vs-Return** — the `FIX-A2` comment at `WorkflowTimerExecutor.cs:309-313` already names this: escalate holds WorkflowTimer (`:324`) + Task (`:1327`) + Node (`:1351`), waits Instance-Seq (`:1356`); the return's STEP-2 (`:2546`) takes WorkflowTimer *while holding the Instance from STEP-1*. So escalate-holds-Timer-waits-Instance × return-holds-Instance-waits-Timer = a `(WorkflowTimer, ProcessInstance)` ABBA. **GATE-0 does NOT fence this** — `WorkflowTimerExecutor.cs:266` skips Returning instances, but it is a *pre-txn snapshot* read at `:244`, taken **before** the reaper opens its txn at `:320`. A concurrent Return can flip Running→Returning *after* the reaper passed GATE-0 and entered its txn holding the Timer. (Today this is mitigated only by deadlock-victim + Armed-retry, which self-heals the reaper side but not the human-return side.)
- **AddApprover-vs-Delegate `(Node, Task)`** — independent of the return entirely: Delegate = Task→Node (`:3316`→`:3351`); AddApprover = Node→Task (`:3043`→`:3136`). If both target the same node concurrently this is `holds-Task-waits-Node × holds-Node-waits-Task`. In practice rows usually differ (AddApprover INSERTs *new* rows / shifts *other* tasks' SequenceOrder; Delegate reassigns the actor's *existing* task), but the SequenceOrder-shift UPDATE can touch a Delegate-held task and the NodeInstance row is unconditionally shared. **A "make-return-instance-last" fix does not address this cycle.**

### 1.4 The documentation lie (Q1)

`docs/workflow-wave5-design.md` §0 (line 21) declares the canonical order `WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance(Seq)` and asserts the return txn is "already consistent with it … (STEP-2 timers → STEP-3 tasks → STEP-4 node)". That enumeration **begins at STEP-2 and silently drops STEP-1's ProcessInstance acquisition** (and STEP-6 + the event-log re-acquisition). The shipped return txn is **instance-first**, in direct violation of the order it claims to honor. Delegate, AddApprover, and reaper-escalate genuinely honor instance-last; only the return is the outlier. (Note: `workflow-wave4-design.md` does not declare a global §0 lock-order rule — it documents per-txn deadlock-avoidance only; the canonical rule lives solely in wave-5 §0.)

---

## 2. The fix — split STEP-1 into its own committed transaction (Proposal B)

### 2.1 Mechanism

Today `ExecuteReturnToNodeAsync` opens one transaction at `WorkflowEngine.cs:2491` that brackets STEP-1 through STEP-6. STEP-1 (`BeginReturnAsync`, `GuardedTransition.cs:115-142`) is itself a single atomic CAS: `WHERE State==Running AND RowVer==@v AND Generation==@g AND ReturnLoops < @max` → `SET State=Returning, Generation+1, ReturnLoops+1, ReturningLeaseUtc=@lease, RowVer+1`. It is the linearization point and the instance-first acquisition.

Replace the single `tx` with **two sequential engine-owned transactions**:

**txA — STEP-1 only.** Open the txn at the current `:2491` position. Run the in-txn re-read (`:2497`), the `State != Running` guard (`:2501-2506`), the `MaxReturnLoops` early-exit (`:2509-2516`), the `BeginReturnAsync` CAS (`:2518-2524`) and its `beginRows == 0` disambiguation (`:2526-2534`), then the post-CAS Generation re-read (`:2537-2541`). **Commit txA here.** After commit, `State=Returning`, `Generation=gNew`, `ReturnLoops+1`, and `ReturningLeaseUtc` are all durable, and the `ProcessInstance` write-lock is released.

**txB — STEP-2 through STEP-6.** Open a fresh `await using var txB = await Db.Database.BeginTransactionAsync(ct)`. Run STEP-2 CancelTimers (`:2546`), STEP-3 Discard (`:2553`) + STEP-3b trigger claim (`:2570/:2619`), STEP-4 Supersede (`:2652`), STEP-5 Mint (`:2694`), STEP-6 AdvanceProcessInstance Returning→Running (`:2711`), event-log AppendAsync (`:2738`), `CommitAsync` (`:2749`). txB re-acquires `ProcessInstance` **only** at STEP-6 + the AllocateSeq inside AppendAsync — i.e. **instance LAST**.

**Resulting txB held-lock order:** `WorkflowTimer (STEP-2) → ApprovalTask (STEP-3) → NodeInstance (STEP-4/5) → ProcessInstance (STEP-6 + Seq)` — **byte-for-byte the wave-5 §0 canonical order.** txA touches only `ProcessInstance` (single row), so it can never be an ABBA party (a single-row CAS cannot hold two locks across a wait — the proven `T_PROV_0` shape).

### 2.2 Widened txB catch — compensating roll-forward

The new non-atomic boundary means txB can fail (DB error mid-STEP-2..6) after txA durably committed the instance into `Returning`. Today the catch is `catch { await tx.RollbackAsync(CancellationToken.None); throw; }` (`:2756-2760`). Widen it:

```
catch
{
    await txB.RollbackAsync(CancellationToken.None);

    // txA already durably committed State=Returning + lease. A failed txB leaves the
    // instance in Returning with no in-flight engine owner. Roll it forward to Running
    // promptly (idempotent), instead of waiting for lease expiry.
    try
    {
        var freshInst = await Db.Set<ProcessInstance>().AsNoTracking()
            .SingleAsync(p => p.ID == instance.ID, CancellationToken.None);
        if (freshInst.State == InstanceState.Returning)
        {
            // ReclaimReturningLeaseByRowVerAsync: portable Returning→Running CAS (no DateTime
            // in WHERE). rows==0 is benign — the Wave-5 reaper already reclaimed it.
            await GuardedTransition.ReclaimReturningLeaseByRowVerAsync(
                Db, freshInst.ID, freshInst.RowVer, CancellationToken.None);
        }
    }
    catch (Exception compEx)
    {
        // Compensation best-effort. If it fails, the Wave-5 reaper
        // (ReclaimExpiredLeasesAsync :1072) flips it back to Running at lease expiry.
        _logger.LogError(compEx,
            "ExecuteReturnToNodeAsync: compensating Returning→Running roll-forward failed for " +
            "instance {InstanceId}; deferring to Returning-lease reaper.", instance.ID);
    }
    throw;
}
```

`ReclaimReturningLeaseByRowVerAsync` (`GuardedTransition.cs:1146-1163`) is the exact existing primitive: `WHERE State==Returning AND RowVer==@v → State=Running, ReturningLeaseUtc=NULL, RowVer+1`. Reusing it keeps the compensation on the proven portable single-row CAS shape.

### 2.3 What is preserved (invariant-by-invariant)

- **GuardedTransition guard-in-WHERE for every state change** — no CAS predicate is touched. STEP-1's `BeginReturnAsync` is unchanged; STEP-2..6 are unchanged.
- **Seq contiguous/monotonic per instance** — txA writes **no** event log (the only `AppendAsync` is at STEP-6 in txB, verified `:2738` is the sole call), so an aborted txA consumes **zero** Seq → no gap. txB allocates exactly one Seq via the unchanged `AllocateSeqWithRetryAsync` (which always re-reads RowVer, `WorkflowEventLogWriter.cs:164-173`).
- **Returning lease + Generation epoch** — txA commits `State=Returning + Generation=gNew + ReturningLeaseUtc + ReturnLoops+1` as one atomic CAS. A crash/abort after txA leaves a durable, self-consistent `Returning` row that the Wave-5 reaper reclaims. `gNew` is durable before txB runs, so any gen-gated CAS from a stale actor (`gOld`) auto-no-ops.
- **MaxReturnLoops cannot double-count** — the cap lives in the `BeginReturnAsync` WHERE (`ReturnLoops < @max`, `:129`); the increment is exactly-once per winning CAS. A failed txB leaves `ReturnLoops` incremented — **correct fail-closed behavior** (a return that flipped the epoch genuinely consumed a loop; decrementing on rollback would reopen Race D).
- **No isolation-level dependency** — neither txA nor txB sets an isolation level; both use the provider default. (The Wave-3 #276 redesign deliberately removed the Serializable MAX(Seq)+1 in favor of the NextSeq counter for cross-provider portability; this fix does not reintroduce it.)
- **SQLite shared-in-memory remains the unit-test substrate** — behavior on SQLite is observationally identical (the split changes lock-hold *windows*, which SQLite serializes anyway).
- **Published-surface additive** — no new public members required by the primary fix; the only signature delta is internal to `ExecuteReturnToNodeAsync`.

---

## 3. Regression-safety analysis vs every other multi-row txn

The fix must not create a new cycle. After the split, txB's order is `WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance(Seq)`. Pairwise:

| Pair | Shared rows | Order agreement | Verdict |
|---|---|---|---|
| **txB vs Delegate** | ApprovalTask, NodeInstance, ProcessInstance(Seq) | txB: Task(STEP-3)→Node(STEP-4)→Instance; Delegate: Task(`:3316`)→Node(`:3351`)→Instance(`:3364`). **Same order.** | No ABBA. |
| **txB vs Reaper-escalate** | WorkflowTimer, ApprovalTask, NodeInstance, ProcessInstance(Seq) | Both `Timer→Task→Node→Instance`. **Identical canonical order.** This is the cycle B *eliminates*: the return no longer holds the instance while taking the Timer. | **#290's reaper-escalate residual CLOSED.** |
| **txB vs AddApprover** | NodeInstance, ApprovalTask, ProcessInstance(Seq) | txB: Task(STEP-3)→Node(STEP-4); AddApprover: Node(`:3043`)→Task(`:3136`). **Opposite `(Node,Task)` order.** BUT: in practice rows differ (AddApprover guards `nodeInst.State==Activated`; STEP-4 supersedes the node, so a concurrent AddApprover on a span node is fenced by the CAS). This is the **same pre-existing Delegate-vs-AddApprover `(Node,Task)` disagreement** (§1.3), not created by B. | Pre-existing, scoped out (§4). |
| **txA vs anything** | ProcessInstance only (single row) | txA is a single-row CAS; cannot hold two locks across a wait. | Never an ABBA party. |
| **txB vs txB (two concurrent returns, same instance)** | all | Each return's txA serializes on the instance `BeginReturnAsync` CAS first (one wins `Returning`, the other gets `beginRows==0 → AlreadyHandled`). Only the winner runs txB. Race A/D semantics (SupersedeNode RowVer contest, MaxReturnLoops cap) unchanged. | No regression. |

### 3.1 The non-atomic boundary — harmful-interleave hunt

Between txA-commit and txB-STEP-3, a concurrent Delegate or AddApprover could win on a span task/node (still Pending/Activated). Verified harmless:

- **Concurrent Delegate wins** — reassigns a span/trigger task's assignee. STEP-3 `DiscardTasksForReturnAsync` (`GuardedTransition.cs:206`) cancels by **State** (`WHERE State IN cancellable`), **not** by assignee — so the freshly-reassigned task is still Cancelled. STEP-3b's trigger claim already tolerates `rows==0` (`:2581-2605`, logs + proceeds because BeginReturn already won). The Delegate's epoch guard re-reads `nodeInst.Generation` inside its own txn (`:3320`); if STEP-1 already bumped the instance Generation but the node hasn't been superseded yet, the Delegate reassigns and is then cancelled by STEP-3 — identical to the pre-split atomic outcome.
- **Concurrent AddApprover wins** — INSERTs `AddedPending` tasks on a span node. STEP-3 cancels them too (`AddedPending` ∈ cancellable, `GuardedTransition.cs:201`); STEP-4 supersedes the node.
- **Critical equivalence:** the **old atomic version admitted the same post-STEP-3 outcome.** Under the single big txn, a concurrent Delegate either blocked on the instance lock (the deadlock) or, on the winning side, committed first and was then cancelled by STEP-3 in exactly the same way. B removes the lock contention, not any fence. The real fences (STEP-3 cancel + STEP-4 supersede + STEP-6 epoch + the `gNew` Generation bump committed in txA) are all preserved.

### 3.2 The widened crash window

If txB fails mid-way (e.g. STEP-4 partial), the instance is `Returning` with some span nodes superseded and the target possibly minted. This is **identical** to the pre-existing STEP-6-FC fail-closed state (`:2718-2735`) and to a host crash mid-txn — both already recovered by the Returning-lease reaper. B's widened catch (§2.2) makes recovery prompt instead of lease-expiry-delayed. `MintNodeInstanceGuardedAsync` (`GuardedTransition.cs:323`) is idempotent on a re-driven return (UNIQUE-index + check-before-insert). No data loss; no zombie (reaper guarantees liveness).

---

## 4. Residual cycle (out of scope for #290) + the C-backstop

### 4.1 Follow-up issue WF-290.2 — unify AddApprover to Task-before-Node

**Status: IMPLEMENTED in #310 (WF-290.2).** The Delegate-vs-AddApprover `(Node, Task)` cycle is now structurally eliminated. AddApprover's 6a/6b ordering was swapped: the ApprovalTask shift+INSERT now precedes `AddApproversToNodeAsync`, so all human txns share one total order `… → ApprovalTask → NodeInstance → ProcessInstance(Seq)`.

**§4.1 Correctness analysis (#310):** The reorder preserves all semantics:
- `existingITCodes` dedup re-read is outside the txn — unchanged.
- `insertionOrder` uses `freshNode.TotalRequired` (pre-bump value, still correct after reorder since `AddApproversToNodeAsync` hasn't fired yet).
- If `AddApproversToNodeAsync` returns 0 (concurrent actor modified node epoch), the whole txn rolls back atomically — no orphaned task INSERTs.
- `TotalRequired` ends at (pre-bump + delta) — identical outcome.
- `nodeInst.State == Activated` guard fires before any writes.

### 4.2 C-backstop — provider-deadlock victim-retry for Delegate/AddApprover ONLY

WF-290.2 (#310) has now landed, eliminating the root cycle. The C-backstop is retained as pure defense-in-depth:

- **`WorkflowDeadlockClassifier.IsDeadlockVictim(Exception)`** (new file `Engine/WorkflowDeadlockClassifier.cs`) — dependency-free match on reflected `Number`/`SqlState` + exception type-name, mirroring the dependency-free UNIQUE-detection style at `GuardedTransition.cs:356-361`. Codes: SqlServer 1205; PgSql `40P01` (+ `40001`); MySql 1213 (`ER_LOCK_DEADLOCK`); Oracle `ORA-00060`. **DaMeng:** the deadlock code MUST be confirmed against a live DaMeng instance under #270 before the classifier claims DaMeng coverage — until then the DaMeng branch is documented as **degrades-to-status-quo** (a missed code rethrows raw, exactly as today). This is the explicit honest limitation the skeptic flagged.
- **`RunWithDeadlockRetryAsync(...)`** (internal, next to `AllocateSeqWithRetryAsync` shape) — bounded jittered-backoff retry of the **whole** Delegate/AddApprover txn body (not the return). Wrap at the public entrypoints (`DelegateTaskAsync` `:3172`, `AddApproverAsync` `:2928`); keep the inner `catch (DbUpdateException) → DelegateAlreadyParticipant` (`:3377`) and `MintNodeInstance` UNIQUE catch as **non-deadlock** semantic outcomes (the classifier matches deadlock SQLSTATE/numbers only).
- **New options** (`WorkFlowOptions.cs`, additive, opt-in tunable, defaults preserve behavior on SQLite): `DeadlockRetryAttempts` (default 3), `DeadlockRetryBaseDelay` (default 20 ms). **New closed code** `WorkflowActionCode.DeadlockRetryExhausted` (append after `DelegateAlreadyParticipant`, kept out of `IsSuccess`/`IsAlreadyHandled`).
- **Idempotency safety — requires `ChangeTracker.Clear()` between attempts (#290 FIX-1)** — the original idempotency argument in the design reasoned only about the DB side: a deadlock-victim abort guarantees a full DB-side rollback, so `NextSeq+1` / `Generation` / lease are all undone at the provider level. This reasoning is **necessary but not sufficient**. EF Core moves Added entities to Unchanged **only on a SUCCESSFUL `SaveChanges`**. When `SaveChanges` throws during a deadlock victim abort, the prior attempt's tracked entities (the `k` new `ApprovalTask` rows added by `AddRange(newTasks)` + the `WorkflowEventLog` row) remain in state `Added` on the engine's shared `Db` context. The retry body creates a **fresh** set of `k` tasks AND re-inserts the **stale Added** ones — producing 2k duplicate rows, colliding with `IX_Wf_ApprovalTask_Node_Assignee_Gen`, and double-bumping `TotalRequired`. DB-side rollback does NOT revert in-memory EF entity state. The fix: call `Db.ChangeTracker.Clear()` at the **top of each retry iteration** in `RunWithDeadlockRetryAsync`, before invoking the body. The body re-reads/re-builds all entities from the DB inside each attempt (via `AsNoTracking` queries and fresh `Add` calls), so clearing here discards only stale tracked state with no functional loss. Without this `Clear()`, the retry-unit is NOT atomic-txn for EF purposes even though the DB transaction rolled back. This gap is invisible on SQLite (the classifier never fires on SQLite — it can never be a deadlock victim) and was caught by T-ABBA-RETRY-04.
- **NOT applied to the return txn** (it is fixed structurally by B) and **NOT applied to the reaper** (it already self-heals via Armed re-fire; the `FIX-A2` comment intent — "do not add serializable isolation" — is honored).

### 4.3 FIX-4: hardening evaluation of MEDIUM and LOW findings (#290 review)

**FIX-4a (MEDIUM — deferred with rationale): `ReturnLoops` increments on transient txB failure.**
`BeginReturnAsync` increments `ReturnLoops` atomically in txA. If txB fails transiently (network, deadlock victim, timeout) and the compensating roll-forward restores the instance to `Running`, `ReturnLoops` is permanently +1 even though no logical return occurred. Over many transient failures a user may exhaust `MaxReturnLoops` without having made any actual returns. The correct fix is to move the `ReturnLoops` increment to txB (so it only increments on a committed full return), which requires restructuring the BeginReturnAsync CAS predicate — a non-trivial change with its own regression surface. **Deferred to a follow-up issue.** The current behavior is conservative and safe: ReturnLoops never under-counts actual returns; it can only over-count, which at worst temporarily restricts return access and resolves on admin reset.

**FIX-4b (LOW — fixed in #290 hardening): STEP-6-FC bypasses compensating roll-forward.**
When the STEP-6 `Returning→Running` CAS returns 0 rows (a concurrent reaper or actor already changed the instance state), the code rolled back txB and returned `AlreadyHandled` WITHOUT calling `ReclaimReturningLeaseByRowVerAsync` — unlike the exception catch-block which proactively attempts the compensation. This meant the STEP-6-FC path left the instance relying entirely on the Wave-5 lease reaper, which fires only at lease expiry (default 30 min). **Fixed in this PR**: the STEP-6-FC branch now attempts the same `ReclaimReturningLeaseByRowVerAsync` compensation as the catch block before returning `AlreadyHandled`. If the instance is no longer in `Returning` (e.g., the reaper already won), the CAS is a no-op (rows==0, benign). This brings STEP-6-FC in line with the catch block and eliminates a 30-minute stuck-Returning window in a rare but possible scenario.

---

## 5. Test matrix — verifiable on SQLite NOW vs gated on #270

### 5.1 Verifiable on SQLite now (added to existing suites)

| Test ID | What it proves | Where |
|---|---|---|
| **T-RET-1..8 (existing)** | Returning/Generation/ReturnLoops/lease/supersede/mint semantics survive the txA/txB re-shaping. **Primary regression gate.** Must stay green unchanged. | `ReturnToNodeTests.cs`, `ReturnConcurrencyTests.cs` |
| **T-ABBA-RET-01 split-durability** | Inject an exception before STEP-6 in txB; assert the instance is left `Returning` with `ReturningLeaseUtc` set and `ReturnLoops` incremented (proves lease/epoch survive non-atomic STEP-1). | new `AbbaFixTests.cs` |
| **T-ABBA-RET-02 reaper-recovery** | Run `ReclaimExpiredLeasesAsync`/`ReclaimReturningLeaseByRowVerAsync` against the stuck `Returning` row with an expired lease; assert it flips to `Running` (crash-recovery covers the new window). | `AbbaFixTests.cs` |
| **T-ABBA-RET-03 compensating-rollforward** | Force a txB failure; assert the widened catch flips `Returning→Running` promptly (no lease-expiry wait). | `AbbaFixTests.cs` |
| **T-ABBA-RET-04 interleave-equivalence** | Commit a Delegate between a manually-staged committed-STEP-1 (txA) and STEP-3; run STEP-3..6; assert the span task ends `Cancelled` and the return completes (no harmful interleave). | `AbbaFixTests.cs` |
| **T-ABBA-RET-05 Seq-contiguity** | Across a return + concurrent Delegate sequence, assert `WorkflowEventLog.Seq` is gap-free/monotonic per instance (proves txA consumes no Seq). | `AbbaFixTests.cs` |
| **T-ABBA-RET-06 lock-order-structure** | Structural assertion (DbCommand interceptor recording statement order, or analyzer) that txB never writes `ProcessInstance` before its first `ApprovalTask`/`NodeInstance` write. | `AbbaFixTests.cs` |
| **T-ABBA-CLS-01..05 classifier** | `IsDeadlockVictim` returns true for synthetic SqlServer 1205 / PgSql 40P01,40001 / MySql 1213 / Oracle ORA-00060; false for `DbUpdateException(UNIQUE)`, generic `InvalidOperationException`, `OperationCanceledException`. Pure function, no DB. | `AbbaFixTests.cs` |
| **T-ABBA-RETRY-01..03 envelope** | Body throws synthetic deadlock K times then succeeds → retries K then succeeds; non-deadlock → immediate rethrow; always-deadlock → `DeadlockRetryExhausted` after exactly `DeadlockRetryAttempts`. Fake body, no provider. | `AbbaFixTests.cs` |
| **T-DEL-* / T-ADD-* / T-MIX-* (existing)** | The retry envelope on SQLite is a transparent single-pass pass-through (classifier returns false → no deadlock on SQLite); all existing Delegate/AddApprover suites behave byte-identically. | `DelegationTests.cs`, `ConcurrencyConformanceTests.cs` |

### 5.2 Gated on #270 live providers (skip-clean stubs added now)

SQLite single-writer **can never exhibit ABBA**, so the actual concurrent deadlock-free proof requires real providers. Add to `ConcurrencyConformanceTests_LiveDb` (`ConcurrencyConformanceTests.cs:~2069`) following the established pattern (`[TestMethod] [TestCategory("ProviderConformance")]` + `RequireConnectionString(envVar)` → `Assert.Inconclusive` when unset / `Assert.Fail` when set-but-unimplemented):

| Stub | Env var | Proves |
|---|---|---|
| `T_ABBA_290_DelegateVsReturn_DeadlockFree_SqlServer` | `WTM_TEST_SQLSERVER_CS` | Concurrent Delegate + Return on the same instance/task → both terminate, ZERO `SqlException 1205`, one winner + one clean `AlreadyHandled`/blocked-then-succeeded. |
| `…_PgSql` | `WTM_TEST_PGSQL_CS` | (same; PG `40P01`) |
| `…_MySql` | `WTM_TEST_MYSQL_CS` | (same; MySql 1213) |
| `…_Oracle` | `WTM_TEST_ORACLE_CS` | (same; `ORA-00060`) |
| `…_DaMeng` | `WTM_TEST_DAMENG_CS` | (same; **also confirms the DaMeng deadlock code for the C-backstop classifier** — the part that cannot be faked on SQLite) |
| `T_ABBA_290_AddApproverVsReturn_DeadlockFree_<5 providers>` | (same set) | Same proof for the AddApprover-vs-Return pair. |
| `T_ABBA_2902_AddApproverVsDelegate_DeadlockFree_<5 providers>` | (same set) | The residual `(Node,Task)` cycle — proves the C-backstop retry resolves it (until WF-290.2 unifies the order). |

**Honest statement:** full deadlock-free proof on server providers is impossible to demonstrate before #270 wires live containers. B's correctness pre-#270 rests entirely on the **structural lock-order argument** (instance-last-everywhere, verifiable by code inspection — §3) plus the existing SQLite semantic suites. The #270 stubs make the live proof a tracked, ready-to-implement obligation, not a hidden gap.

---

## 6. Sub-issue scope

| Sub-issue | Title | Scope |
|---|---|---|
| **#290 (this)** | Return-txn ABBA fix — split STEP-1 into its own committed transaction | `WorkflowEngine.cs:ExecuteReturnToNodeAsync` (split txA/txB + widened compensating catch); SQLite tests T-ABBA-RET-01..06; #270-gated `T_ABBA_290_*` stubs (10); doc corrections (wave-5 §0 + this doc). Touches **one** production method. |
| **#290 backstop (folded in)** | C-backstop deadlock-victim retry for Delegate/AddApprover | `Engine/WorkflowDeadlockClassifier.cs` (new); `RunWithDeadlockRetryAsync` envelope around `DelegateTaskAsync`/`AddApproverAsync`; `WorkFlowOptions` `DeadlockRetryAttempts`/`DeadlockRetryBaseDelay`; `WorkflowActionCode.DeadlockRetryExhausted`; tests T-ABBA-CLS-* / T-ABBA-RETRY-*; #270-gated `T_ABBA_2902_*` stubs. DaMeng code confirmed under #270. |
| **WF-290.2 (#310) ✓ DONE** | Unify AddApprover to Task-before-Node — total human-txn lock order | Swapped `AddApproverAsync` 6a/6b ordering (Task shift+INSERT before `AddApproversToNodeAsync`); §4.1 correctness analysis + T-ABBA-2902-LO-01/SEM-01..04/CONC-01 tests. The residual `(Node,Task)` ABBA cycle is now structurally eliminated. C-backstop retained as defense-in-depth. |
| **#270 (existing)** | Live-provider conformance harness | Wires the SqlServer/PgSql/MySql/Oracle/DaMeng containers that turn every `T_ABBA_*` stub above from skip-clean into an executing deadlock-free proof. |

---

## 7. CHANGELOG entry (for the version that lands #290)

```
### Fixes
- fix(workflow): #290 resolve a pre-existing ABBA deadlock between 回退 (return-to-node) and
  委托/加签 (delegate/add-approver) on server DB providers. The return transaction now acquires
  ProcessInstance last (canonical lock order) by committing the Running→Returning linearization
  point in its own transaction before touching timer/task/node rows. No behavior change on SQLite;
  no isolation-level dependency; Returning-lease crash recovery unchanged. Additive opt-in
  deadlock-victim retry (WorkFlowOptions.DeadlockRetryAttempts, default 3) backstops the residual
  delegate-vs-add-approver path until lock-order unification (WF-290.2). (#290)
```

---

## 8. Summary

The #290 ABBA is a single-root-cause bug: the 回退 transaction is the **only** multi-row transaction that locks `ProcessInstance` first; every other locks it last via the Seq counter. The fix commits the return's linearization point (STEP-1) in its own transaction so the rest of the return (STEP-2..6) acquires `ProcessInstance` last — making the return conform to the wave-5 §0 canonical order it already claimed to honor. This closes the Delegate-vs-Return, AddApprover-vs-Return, **and** reaper-escalate-vs-Return cycles in one move, with minimal blast radius (one production method, no CAS/isolation/Seq change), and the new non-atomic boundary lands on the already-existing Returning-lease crash-recovery path. The residual Delegate-vs-AddApprover `(Node,Task)` cycle is scoped out to WF-290.2 and mitigated meanwhile by an additive deadlock-victim retry backstop. Structural correctness is provable on SQLite today; the concurrent deadlock-free proof on the five server providers is added as #270-gated skip-clean stubs — honestly the limit of what can be demonstrated before live providers are wired.
