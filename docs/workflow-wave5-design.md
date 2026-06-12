# Wave-5 Design Addendum — 超时 (WF-20): Timer Reaper + Timeout Actions — Final Synthesized Spec

> Synthesized 2026-06-11 from two adversarially-generated proposals (A: correctness-minimal, B: company-practice-complete) + skeptic adjudication. Verified against origin/dotnet10 @7102789c5 (Wave 4 merged). Authoritative over the spec §5.10 sketch where they differ; deviations from the engine spec are called out inline.
> All new behavior is opt-in behind `AddWtmWorkFlowTimers()` (today an empty stub in ServiceCollectionExtensions). A host that never calls it is byte-identical to 10.10.0+Wave-4.

## 0. Synthesis verdicts (contested points)

| Point | Verdict | Why |
|---|---|---|
| Fire delivery semantics (R3) | **B: transactional claim-then-execute** — per-timer txn { fire CAS + action-decision CAS(es) + re-arm INSERT + event rows }; post-claim node continuation and notifications post-commit | Crash pre-commit → timer stays Armed → clean retry; no lost auto-action (A's non-txn variant silently drops an authored SLA action on crash). Multi-host dedupe unchanged (fire CAS is the mutex). Lock-order rule below makes it deadlock-safe. |
| Sequential timeout scope | **B: per-active-step task-scoped timers** (inferred; no `perStep` field this wave) | 每步 N 小时 is the actual company practice; "node-scoped SLA on a 5-step sequential" is almost never the authored intent. Races identical to the task-CAS discipline. `perStep` override field cut — additive later. |
| Escalate semantics | **Hybrid (skeptic fix S1)**: task-scoped (Sequential step) timers → REAL reassignment via the WF-19 assignee-bound CAS + any-state collision pre-check (FIX-2 lesson) with notify-only downgrade on collision; node-scoped (All/Any) timers → notify-only | B's "reassign all pending tasks to one target" breaks the unique index `(NodeInstanceId, AssigneeITCode, Generation)` on the second task and re-creates the voted-participant crash WF-19 FIX-2 just closed. Reassigning a 会签 quorum to one person is a TotalRequired-semantics problem deferred to its own design. |
| Business calendar | **Hybrid (skeptic fix S2)**: ship `IBusinessCalendar` seam + `PassThroughBusinessCalendar` default; for `businessCalendar:true` graphs with only the pass-through registered: `Remind` → arm with wall-clock math + LogWarning (early reminder is harmless); `AutoApprove/AutoReject/Escalate` → **skip arming + LogWarning** + publish-time reject for NEW graphs | B's pass-through-for-everything silently fires auto-actions EARLIER than the authored business-time deadline (weekends) — a compliance lie. A's skip-everything kills harmless reminders too. Per-action-severity is fail-closed exactly where it matters. |
| ITimeoutActionRegistry (spec §2.5 sketch) | **A: dropped** — closed `switch` in an internal executor; `TimerFireOutcome` internal closed union | Open extension point over engine-internal race surfaces is a correctness liability (W5 one-seam discipline). Registry can be added additively later. Stub XML remarks updated accordingly (called-out deviation). |
| Memory provider in hosted service | **A: throw out of `ExecuteAsync`** → default `BackgroundServiceExceptionBehavior.StopHost` | Spec invariant #8 (fail fast); engine ctor would fail the first workflow op anyway; .NET default behavior; loud > limping. |
| Returning-lease reclaim | **B: new portable `ReclaimReturningLeaseByRowVerAsync`** (no DateTime in UPDATE WHERE; expiry filtered client-side on the RowVer-pinned snapshot); shipped nullable-DateTime method left untouched, unused | Why ship a caller on the exact hazard class (nullable-DateTime UPDATE predicate) that WF-19 FIX-8 hard-blocks AtAction for on Oracle/DaMeng? RowVer-pinned is strictly portable. |
| EventAction enum | **B: append** `TimeoutRemind`, `TimeoutEscalate`, `DelegationExpiredReverted` (after the last shipped member; append-only) | Distinct audit reasons; closed-union discipline. `TimeoutFire` (shipped) is used for auto-action fires; `FailClosed` reused for suppression/empty-target events. |
| R5 sweep option shape | **B: enum** `DelegationExpiredSweep { Off, RevertToPrincipal }`, default `RevertToPrincipal` | Extensible (future EscalateToAdmin member); defensible default — only reachable inside the doubly-opt-in intersection (AddWtmWorkFlowTimers + AtAction non-default), and it is what the shipped WF-19 ctor warning promises. |
| DueUtc | **B: engine-stamped derived output** — set `ApprovalTask.DueUtc = activation + duration` at the SAME site that arms the covering timer; never an input (external DueUtc never arms anything) | Powers shipped inbox ordering; plain column write, no predicate participation. |

**Lock-order rule (new invariant for every timer-path transaction):** acquire rows in the order **WorkflowTimer → ApprovalTask → NodeInstance → ProcessInstance(Seq)**. The shipped return txn (STEP-2 timers → STEP-3 tasks → STEP-4 node) and the Wave-4 DelegateTaskAsync txn (task → node-epoch → instance-Seq) are already consistent with it; human approve/reject paths are single-statement autocommits and hold nothing across waits.

## 1. Components & registration

`AddWtmWorkFlowTimers()` (fills the shipped stub; additive; no `BuildServiceProvider`):
```
services.TryAddSingleton<IBusinessCalendar, PassThroughBusinessCalendar>();
services.AddScoped<WorkflowTimerExecutor>();                  // internal
services.AddHostedService<WorkflowTimerHostedService>();      // BackgroundService, Etl envelope
```
- **WorkflowTimerHostedService**: singleton over `IServiceProvider`; per tick `CreateScope()`; `@now` bound ONCE per tick via `(_sp.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime` (Etl precedent); per-timer try/catch (poisoned timer never blocks the batch); whole-tick try/catch; 3-attempt startup retry. First scope calls `ValidateDbType(dc)` UNCONDITIONALLY and lets the exception escape `ExecuteAsync` (Memory fail-fast, StopHost). Tick phases: (1) fire due timers, (2) Returning-lease reclaim, (3) AtAction expired-delegation sweep.
- **WorkflowTimerExecutor** (internal, scoped): per-timer fire pipeline (§4); closed `switch` on `TimerAction`; outcomes in internal closed union `TimerFireOutcome { Fired, LostRace, OrphanRetired, HumanActedNoOp, SupersededNoOp, DowngradedToRemind, FailClosed, EscalateCollisionNotifyOnly }`.
- **`WorkflowEngine.SystemActTaskAsync` (internal)**: system claim seam — mirrors Approve/Reject minus assignee-RBAC; reuses the EXISTING post-claim continuation extracted into a private helper shared with the human path (human path byte-identical — locked by the full existing suite + an event-sequence snapshot test). Executor type-tests `engine as WorkflowEngine`; custom `IWorkflowEngine` registered → auto-actions unsupported → loud Remind downgrade + `FailClosed` event (no `IWorkflowEngine` interface change).
- **Timestamps**: engine-side arm/cancel sites follow the engine's shipped bound-once `DateTime.UtcNow` convention; the hosted service uses the Etl TimeProvider pattern. Spec §1.4 #5 (`Wtm.TimeProvider`) vs actual engine precedent discrepancy is recorded here, not silently "fixed". Never SQL `CURRENT_TIMESTAMP`.
- **Tenant filters**: reaper candidate SELECTs use `IgnoreQueryFilters()` WITH the mandatory justification comment — cross-tenant system sweep; every downstream write is a PK + RowVer (+State/Generation) single-row CAS so no cross-tenant write is possible; event-log TenantCode copied from the source instance row. Invariant test pins this (T-TMO-21).

## 2. Arm lifecycle

Source: `NodeDef.Timeout` (`TimeoutDef { Duration (ISO-8601), BusinessCalendar, Action, RemindEveryHours, MaxReminders }`, shipped Sprint-1, currently dead). Per-node authoring; scope by mode:
- **All/Any**: ONE node-scoped timer (`ApprovalTaskId = NULL`) per node-generation, armed in `AdvanceTokenAsync` immediately after the `ActivateNodeInstanceAsync` winner (rows==1) + handler `OnEnterAsync` mint. Key `tmo:n:{NodeInstanceId:N}:{Generation}:0`.
- **Sequential**: per-ACTIVE-STEP task-scoped timer (`ApprovalTaskId = stepTask.ID`): armed for step 1 at node activation, for steps 2..n at pointer-advance Step B (NotYetActive→Pending), and for WF-18 injected steps when their slot activates. Key `tmo:t:{TaskId:N}:0`. NotYetActive/AddedPending tasks NEVER have running timers — structural (arm rides activation, never mint).
- 回退 re-entry flows through the same activation sites at gNew → fresh timers, fresh keys, `RemindCount=0` (Reset semantics free).

Arm = guarded INSERT (check-before-insert + unique `IdempotencyKey` violation caught and swallowed — `MintNodeInstanceGuardedAsync` pattern). `FireAtUtc = @now + XmlConvert.ToTimeSpan(Duration)` via `IBusinessCalendar.AddBusinessTime(@now, duration, options.BusinessCalendarId)`. `Generation = node.Generation` copied. `DueUtc` stamped on covered task(s) at the same site.

Arm-time guards (legacy graphs published before WF-20 validation): `BusinessCalendar==true` with only pass-through registered → per-action-severity (§0 verdict): Remind arms wall-clock + warn; auto/escalate SKIP + warn. `Action ∈ {AutoApprove,AutoReject}` while `AllowTimerAutoAction==false` → arm normally (fire-time downgrade is authoritative).

## 3. New GuardedTransition methods (additive; every existing predicate byte-identical)

| Method | Predicate / effect |
|---|---|
| `FireTimerAsync` | `WHERE ID==@t AND Status==Armed AND RowVer==@v` SET `Status=Fired, RowVer+=1` — the multi-host mutex. rows==0 → cancelled or another host won → zero side effects. |
| `CancelTimersForNodeAsync` | `WHERE NodeInstanceId==@n AND Status==Armed` SET `Status=Cancelled, RowVer+=1` (bulk, Status-only — consistent with shipped `CancelTimersForReturnAsync`). |
| `CancelTimerForTaskAsync` | `WHERE ApprovalTaskId==@task AND Status==Armed` SET `Status=Cancelled, RowVer+=1` (Sequential step claim/advance hygiene). |
| `EscalateTaskAssigneeAsync` | `WHERE ID==@task AND State==Pending AND RowVer==@v AND Generation==@g AND AssigneeITCode==@current` SET `AssigneeITCode=@target, RowVer+=1` (WF-19 FIX-1 discipline; task-scoped escalate only). |
| `ReclaimReturningLeaseByRowVerAsync` | `WHERE ID==@i AND State==Returning AND RowVer==@v` SET `State=Running, ReturningLeaseUtc=NULL, RowVer+=1` — expiry checked client-side on the pinned snapshot; NO DateTime in UPDATE WHERE (portable; shipped nullable-DateTime variant left untouched/unused). |

Fire binds RowVer; cancels stay Status-only bulk. Status never returns to Armed (re-arm is always a NEW row) → no ABA; `Status==Armed` is the single contended bit.

## 4. Fire pipeline (per candidate timer)

```
Candidates: SELECT TOP(@TimerBatchSize) WHERE Status==Armed AND FireAtUtc <= @now ORDER BY FireAtUtc
            -- shipped (Status, FireAtUtc) index; non-nullable DateTime SELECT-side only; IgnoreQueryFilters (justified)
GATE-0 (pre-checks, no txn): inst.State != Running OR timer.Generation != inst.Generation OR node.State != Activated
        → txn { FireTimerAsync }  -- retire orphan: terminal, zero side effects, no event spam (Race C)
TXN (lock order §0): FireTimerAsync (rows==0 → ROLLBACK, done)
                     + action-decision CAS(es)        (§5)
                     + remind next-link INSERT        (§5 Remind)
                     + WorkflowEventLog append(s)     (Seq via shipped AllocateSeqAsync envelope)
COMMIT
POST-COMMIT: node continuation for auto-actions (existing increment/TryComplete/advance path — re-drivable, see Known limitation);
             notifications — re-gated on a fresh read (node Activated + generation match) — ≤1 bounded-staleness reminder accepted
```

## 5. Action semantics

**Remind (催办)** — default-safe. In-txn: next-link INSERT when `RemindEveryHours != null AND RemindCount+1 < min(MaxReminders ?? options.MaxRemindersDefault, options.MaxRemindersHardCap)`: key `tmo:{n|t}:{id}:{gen?}:{RemindCount+1}`, `FireAtUtc=@now+RemindEveryHours`, Generation copied → chain can neither break nor double-send (crash pre-commit → link k still Armed, unique key absorbs replay; crash post-commit → ≤1 lost notification, chain intact). Event `TimeoutRemind` (actor NULL). Post-commit notify CURRENT Pending assignees (re-read at fire — honors delegation/escalation reassignments).

**Escalate** — task-scoped timer (Sequential step): collision pre-check — target has ANY task row on `(NodeInstanceId, Generation)` regardless of State (WF-19 FIX-2 lesson; unique-index classes) → downgrade to notify-only + `FailClosed` event detail "escalate target already participant". Else `EscalateTaskAssigneeAsync` + best-effort `AdvanceNodeApproverSetEpochAsync` + event `TimeoutEscalate` (actor NULL, OnBehalfOf=old assignee) + re-arm follow-up reminder against the new assignee (chain continues). Target = `TimeoutDef.EscalateTo` (new additive graph field) else `options.AdminFallbackITCode`; both empty → FAIL-CLOSED: no reassign, `FailClosed` event + Warning + reminder to current assignee. Node-scoped timer (All/Any): notify-only to admin + current assignees (quorum reassignment deferred — TotalRequired semantics under multi-reassign is its own design).

**AutoApprove / AutoReject** — double-gated (§6 R6). In-txn DRAIN (bound `pendingTaskCount + 8`): per Pending task of the covered scope, `SystemActTaskAsync` claims via the EXISTING `ClaimApprovalTaskAsync` predicate (`State==Pending AND RowVer AND Generation==@timerGen`) with `nextState: AutoApproved|AutoRejected`; rows==0 (human won / superseded) → skip, no event. Post-commit: the SAME post-claim continuation the human path runs (increments → handler TryComplete → `CompleteNodeInstanceAsync(generation, epoch)` → advance). AutoReject routes through handlers → Any-mode unanimity and All-mode RejectGate semantics respected structurally. Sequential: claims the active step; pointer-advance already treats AutoApproved as passed (shipped); next step claimed next drain iteration.

## 6. Race resolutions (exact guards)

**R1 timeout vs human (T-CONC-4):** two-level CAS; the ApprovalTask row is the single arbiter. Fire CAS dedupes hosts; the task claim uses the byte-identical shipped `State==Pending` predicate, so exactly one of {human, timer} gets rows==1; the loser observes rows==0 → no event, no notification. Node-level composition: continuation CASes (`State==Activated` + Generation + epoch) auto-no-op if the node was concurrently decided. Spec §5.10's "ordering" question dissolves: the task CAS IS the window.

**R2 timeout vs 回退 (Race C):** three independent gates — (1) return STEP-2 bulk cancel vs fire CAS mutually exclusive on `Status==Armed`; (2) fire-winner actions all bind `Generation==@timerGen` (+ span tasks already Cancelled → `State==Pending` fails too); (3) notifications re-gated post-commit on generation+state re-read. Orphan Armed timers of a superseded span self-heal via GATE-0 retirement. Supersede-not-delete keeps the FK target alive (T-RET-4 preserved).

**R3 crash/delivery:** transactional claim-then-execute (§0 verdict). Pre-commit crash → Armed → retried idempotently. Post-commit-pre-continuation crash for auto-actions → tasks claimed but node not completed: **same crash profile as the shipped human approve path** (no engine-wide self-heal this wave — documented Known limitation; a future reaper phase can re-drive "Activated node with zero Pending tasks"). Post-commit-pre-notify crash → ≤1 lost notification. Multi-host double-fire impossible (fire CAS). No lease column / no Claimed state needed — the open txn IS the transient claim state.

**R4 lifecycle:** ARM sites (§2) — all idempotent (deterministic keys + unique catch). CANCEL sites (hygiene; winner-of-the-decision-CAS calls it): AdvanceTokenAsync auto-complete, gateway fork, End/instance-Approved (instance-wide), branch-into-Join, Join winner, Join orphan fail-closed, All TryCompleteApproved/Rejected, Any first-win sibling-cancel + unanimous reject, Sequential reject node-complete, Sequential step claim/advance (task-scoped), instance Rejected (instance-wide), `WithdrawAsync` (instance-wide — closes the spec §5.6 gap), `ReturnToInitiatorAsync`, 回退 STEP-2 (shipped). **Missed-cancel proof:** an orphan fires once → GATE-0 retires it terminally; every mutation predicate it could reach is independently fail-safe (`State==Pending(+Gen)`, `State==Activated(+Gen)`); notifications re-gated. Cost: one retire cycle, zero state change, ≤1 bounded-staleness reminder.

**R5 AtAction-expired delegated tasks:** reaper phase-3, gated `DelegationWindowMode==AtAction AND options.DelegationExpiredSweep==RevertToPrincipal`: SELECT expired delegated Pending tasks (nullable-DateTime SELECT-side only; AtAction is ctor-blocked on Oracle/DaMeng so unreachable there) → per-row the EXISTING revoke revert shape (`AssigneeITCode=DelegatedFromITCode`, clear rule fields, `State==Pending AND RowVer AND Generation`) + epoch bump + `DelegationExpiredReverted` event (actor NULL) + notify principal. Revert NARROWS authority back to the accountable approver — fail-safe vs escalating or leaving it stuck. Concurrent human claim wins cleanly (rows==0, partial success). WF-19 ctor warning text updated to reference the shipped reaper.

**R6 compliance defaults:** two keys — graph authors `action: autoApprove|autoReject` (version-pinned intent) AND ops sets `AllowTimerAutoAction=true` (default **false**). Publish-time: validator rejects auto actions while the gate is off. Fire-time (authoritative): gate off → LOUD downgrade to Remind + `FailClosed` event + Warning; task stays Pending. Audit: `TaskState=AutoApproved/AutoRejected` (the state is the marker; Sequential already consumes it), event `TimeoutFire` with `ActorITCode=NULL` + `OnBehalfOfITCode=assignee`, one event row per claimed task, no double-count.

**R7 催办 loop:** new row per link (never re-arm — ABA + audit); deterministic keys; cap `min(MaxReminders ?? MaxRemindersDefault(3), MaxRemindersHardCap(10))`; `RemindEveryHours==null` → one-shot. Chain inherits Generation (Race-C gate on every link).

**R8 scope:** ships per-node (All/Any) + per-step (Sequential) from `TimeoutDef`; `DueUtc` derived-only; arbitrary per-task DueUtc-driven timers deferred (no authoring surface, no cancel owner); `IBusinessCalendar` seam ships with per-action-severity fail-closed arming (§0 verdict S2); real calendar data is consumer-specific.

## 7. Options / graph / validator deltas

Options (all additive): `AllowTimerAutoAction=false`; `ReturningLeaseTtl=30min` (replaces the hardcoded constant — zero behavior change); `TimerBatchSize=100`; `MaxRemindersDefault=3`; `MaxRemindersHardCap=10`; `DelegationExpiredSweep=RevertToPrincipal` (enum, only reachable in the doubly-opt-in intersection). Consumed as-is: `TimerPollInterval` (1min), `BusinessCalendarId`, `AdminFallbackITCode`.

Graph (additive-nullable, AckMode discipline, NO schemaVersion bump): `TimeoutDef.EscalateTo (string?)`. Nothing else.

Validator (publish-time, NEW graphs only — TimeoutDef validated for the first time): Duration parses ISO-8601 and > 0; `RemindEveryHours > 0`; `MaxReminders >= 1`; `EscalateTo` non-whitespace when present; `Timeout` only on task-minting node kinds; `businessCalendar:true` rejected when only pass-through is registered is NOT checkable at publish → instead reject `businessCalendar:true` combined with `Action ∈ {AutoApprove,AutoReject,Escalate}` (the dangerous class; Remind+calendar allowed, arms wall-clock+warn); auto actions rejected while `AllowTimerAutoAction==false`.

Schema: **ZERO database schema/migration delta** (WorkflowTimer consumed as shipped; "no migration churn in Wave 5" promise kept). EventAction +3 appended members. IWorkflowNotifier +3 DIMs (`NotifyTimeoutRemindAsync`, `NotifyTimeoutEscalatedAsync`, `NotifyTimeoutAutoActionedAsync`) default no-op; WebhookWorkflowNotifier overrides (identifiers-only cards, never form data/PII); every DIM has a verified call site (WF-15 dead-code lesson, T-TMO-19).

## 8. Test matrix (SQLite shared-in-memory + WF-0 barrier; provider rows nightly per #270)

| ID | Scenario | Assert |
|---|---|---|
| T-TMO-01 | timeout AutoApprove vs human approve, both orders (=T-CONC-4) | exactly one outcome; loser rows==0 silent; one decision event; timer Fired |
| T-TMO-02 | two hosts, same Armed timer, same snapshot | exactly one FireTimerAsync rows==1; one event/notification/re-arm |
| T-TMO-03 | crash (rollback) after fire CAS pre-commit | timer still Armed; next tick fires; no duplicate Seq/link |
| T-TMO-04 | fire vs 回退 STEP-2 cancel, both orders (T-RET-4 analog) | one Status winner; gOld actions rows==0; no FK abort; gNew re-arms fresh |
| T-TMO-05 | orphan retirement per terminal shape (Approved/Rejected/Superseded/Withdrawn) | timer→Fired; zero state change; zero notification; no accumulation |
| T-TMO-06 | remind chain: insert/cap/one-shot/crash-replay | link k+1 atomic with flip; caps honored; unique key absorbs replay |
| T-TMO-07 | gate off + Action=AutoApprove fires | downgrade to Remind; task Pending; FailClosed event; flip option → auto-acts |
| T-TMO-08 | AutoReject on Any-mode (3 tasks) + racing human approve | per-task claims through handlers; unanimity preserved; sibling-cancel beats auto-reject |
| T-TMO-09 | AutoApprove on k-of-n All + concurrent 加签 epoch bump | threshold completes exactly once; stale-epoch completion re-reads |
| T-TMO-10 | Sequential per-step: NotYetActive zero timers; advance cancels n, arms n+1 | structural invariant holds at every step incl. 加签-injected |
| T-TMO-11 | Withdraw/instance-terminal sweeps + racing fire | instance-wide cancel; racing fire retires via GATE-0 |
| T-TMO-12 | Escalate (task-scoped): reassign, collision→notify-only, both-targets-empty→FailClosed | assignee-bound CAS; epoch bump; no unique-index exception ever |
| T-TMO-13 | Returning-lease reclaim by RowVer | expired → Running; live untouched; dead owner's commit fails RowVer |
| T-TMO-14 | AtAction expired-delegation sweep | revert/clear/epoch/event/notify; human claim wins cleanly; Off/AtAssignment → no-op |
| T-TMO-15 | Memory fail-fast | ExecuteAsync throws → StopHost; no timer processed |
| T-TMO-16 | business-calendar severity: Remind arms+warns; auto skips+warns; new-publish rejects dangerous combo | per-action fail direction |
| T-TMO-17 | DueUtc stamped at arm sites; external DueUtc arms nothing | derived-only contract |
| T-TMO-18 | poller robustness: one poisoned timer in batch of N | N-1 processed; poisoned stays Armed; host alive |
| T-TMO-19 | notifier DIMs: each has a live call site; null/throwing notifier harmless | WF-15 dead-code lesson |
| T-TMO-20 | third-party notifier compiled against 6-method interface | loads; DIMs no-op; cards contain no FormDataJson |
| T-TMO-21 | tenant invariant: HasQueryFilter on WorkflowTimer; reaper cross-tenant sweep mutates nothing cross-tenant | IgnoreQueryFilters pinned |
| T-TMO-22 | arm idempotency under concurrent double-activation; 回退 re-arm distinct keys | exactly one row per key |
| T-PROV-W5 | FireTimer/Escalate/LeaseReclaim winner-1-loser-0 on 6 providers; no nullable-DateTime in any new UPDATE WHERE | nightly, #270 gated |

## 9. Sub-issue scopes (single issue WF-20; sequenced sub-scopes, Wave-4 discipline)

1. **WF-20.1 — Reaper infra + fire/cancel primitives.** GuardedTransition (+`FireTimerAsync`, `CancelTimersForNodeAsync`, `CancelTimerForTaskAsync`, `ReclaimReturningLeaseByRowVerAsync`), WorkflowTimerHostedService + WorkflowTimerExecutor skeleton (GATE-0 + fire txn + Remind only), WorkFlowOptions additions, `AddWtmWorkFlowTimers` fill, Memory fail-fast, IBusinessCalendar seam. Tests: T-TMO-02/03/05/15/18/21/22.
2. **WF-20.2 — Arm sites + cancel obligations + DueUtc.** All arm sites (§2), every cancel site (§6 R4), validator TimeoutDef rules, lease-TTL option wiring. Tests: T-TMO-04/10/11/13/16/17.
3. **WF-20.3 — Remind chain + notifier DIMs.** Next-link txn, caps, 3 DIMs + webhook overrides + call sites. Tests: T-TMO-06/19/20.
4. **WF-20.4 — Auto-actions.** `SystemActTaskAsync` + shared post-claim helper extraction (human path byte-identity snapshot test), drain, gates. Tests: T-TMO-01/07/08/09 + human-path regression.
5. **WF-20.5 — Escalate + AtAction sweep + docs.** `EscalateTaskAssigneeAsync` + collision downgrade, EscalateTo field, phase-3 sweep, ctor-warning text update, docs/workflow.md §timeout. Tests: T-TMO-12/14 + T-PROV-W5 rows.

## 10. Deferred (explicit, with the unresolved question named)

- Per-task DueUtc-driven timers (no authoring surface; no cancel owner).
- Node-scoped quorum Escalate-as-reassignment (TotalRequired semantics under multi-reassign-to-one-target).
- Manager-chain escalation targets (resolver integration mid-fire needs its own fail-closed design).
- Multi-stage timeout pipelines (`stages[]`: remind→escalate→auto) — each stage transition is its own race surface; additive later.
- Real business-calendar data/UI (consumer-specific 调休 tables).
- Engine-wide self-healing drain for crash-after-claim-before-completion (pre-existing human-path profile too).
- Timer admin/ops API + grid (needs RBAC + audit design; Wave-6).
- `TaskState.Expired` taxonomy for un-acted siblings.
- #270 live-provider execution (containers) — T-PROV-W5 rows join the gated suite.
