# WalkingTec.Mvvm.WorkFlow — Module Developer Guide

> Target framework: **10.8.0+** · Branch: `dotnet10` · Status: MVP (串签/会签/或签 + 条件路由 + 抄送 + 撤回 + 回退发起人)

---

## 1. What This Module Is

`WalkingTec.Mvvm.WorkFlow` is an opt-in sibling library (peer to `WalkingTec.Mvvm.Etl`) that adds a **Chinese-corporate approval / workflow engine** to WTM applications. It provides:

- **Version-pinned process definitions** — immutable, content-hashed JSON graphs so in-flight approvals are never affected by definition changes.
- **Three approval modes** — 串签 (sequential), 会签 (all/joint), 或签 (any-one), each dispatched by one generic `ApproveMode` enum on a single Approval node type.
- **Race-safe concurrency** via a `GuardedTransition` CAS helper (modeled on `TokenService`) — no double-approvals, no lost-completions.
- **Opt-in notifications** through the shared `IWtmWebhookSink` (DingTalk / WeCom / Feishu / Slack / Teams).
- **RBAC + multi-tenant** by construction — all entities are DIRECT `PersistPoco / ITenant` descendants; DataContext auto-applies query filters.
- **Consumer-owned migrations** — the module ships zero migrations; consumers run `dotnet ef migrations add` against their own `DataContext`.

### Deferred (roadmap)

| Wave | Feature |
|---|---|
| Wave 3 | 回退-to-arbitrary-node (ReturnToPrev / ReturnToNode); Inclusive gateway + Join node |
| Wave 4 | 加签 (add approver at runtime); 委托/转交 (delegation) |
| Wave 5 | 超时 (timeout / durable timer) |
| Wave 6 | Low-code visual designer |

---

## 2. The 10 Approval Modes

All modes are values of the `ApproveMode` enum on a single Approval node. Three are implemented in MVP; seven are available in deferred waves (schema already present).

| # | Mode | Status | Behavior |
|---|---|---|---|
| 1 | **串签** (Sequential) | ✅ MVP | Tasks activate one-by-one in order; each approver acts before the next is notified. |
| 2 | **会签** (All / Joint) | ✅ MVP | All approvers are notified simultaneously; all (or a configurable `ApprovePercent` ratio) must approve. |
| 3 | **或签** (Any-one) | ✅ MVP | All approvers are notified; first to approve completes the node; others' tasks are cancelled. |
| 4 | **加签** | Wave 4 | Current approver adds extra approvers at runtime (`IsRuntimeInjected`). |
| 5 | **委托/转交** | Wave 4 | Standing time-bounded delegation via `DelegationRule`; explicit `DelegationWindowMode` (AtAssignment default). |
| 6 | **撤回** | ✅ MVP | Initiator withdraws a running instance (three policy levels: BeforeAnyAction / BeforeFinalApproval / Disabled). |
| 7 | **回退** | ✅ ReturnToInitiator (MVP) | Approver sends instance back to initiator for revision; Wave 3 adds ReturnToPrev / ReturnToNode. |
| 8 | **条件路由** | ✅ Exclusive (MVP) | Condition node with ordered branches and a mandatory default; whitelist-validated, Expression-Tree evaluated. |
| 9 | **抄送** | ✅ MVP | Non-blocking CC records created in the same transaction; recipients notified via `IWtmWebhookSink`. |
| 10 | **超时** | Wave 5 | Durable `WorkflowTimer`; schema present from Sprint 1. Actions: Remind / AutoApprove / AutoReject / Escalate. |

---

## 3. Authoring a Process Definition

A process is described as **canonical JSON** (deterministically sorted keys), stored in `ProcessDefinitionVersion.GraphJson`, and SHA-256 hashed into `ProcessDefinitionVersion.ContentHash`.

### Minimal JSON example

```jsonc
{
  "schemaVersion": 1,
  "key": "PurchaseApproval",
  "name": "采购审批",
  "fieldWhitelist": [
    { "field": "amount",     "clrType": "System.Decimal" },
    { "field": "department", "clrType": "System.String"  }
  ],
  "nodes": [
    { "nodeKey": "start", "kind": "Start" },
    { "nodeKey": "mgr", "kind": "Approval",
      "approveMode": "Sequential",
      "rejectGate": "Immediate",
      "rejectPolicy": "ReturnToInitiator",
      "approverRule": { "type": "Role", "value": "MANAGER" }
    },
    { "nodeKey": "cfo_gate", "kind": "Condition",
      "branches": [
        { "rule": { "field": "amount", "operator": "Gt", "value": 10000 }, "target": "cfo" }
      ],
      "default": "end"
    },
    { "nodeKey": "cfo", "kind": "Approval",
      "approveMode": "Any",
      "rejectPolicy": "TerminateInstance",
      "approverRule": { "type": "Role", "value": "CFO" }
    },
    { "nodeKey": "end", "kind": "End" }
  ],
  "transitions": [
    { "from": "start",    "to": "mgr"      },
    { "from": "mgr",      "to": "cfo_gate" },
    { "from": "cfo",      "to": "end"      }
  ]
}
```

### Version-pinning

Running instances FK `ProcessDefinitionVersion.ID` — never the mutable `ProcessDefinition` head. Editing a definition creates a **new immutable version** (VersionNo incremented, ContentHash recomputed). In-flight approvals continue using the version they started on.

### Publishing a process

Call the publish endpoint — the engine validates, canonicalizes, hashes, and either repoints the head (new version) or no-ops if the hash matches (idempotent republish):

```
POST /api/_workflow/definitions/{id}/publish
Content-Type: application/json
Body: { "graphJson": "{ ... }" }
```

Validation only (dry-run):

```
POST /api/_workflow/definitions/validate
Content-Type: application/json
Body: { "graphJson": "{ ... }" }
```

### Conditional routing rules

Rules use a **closed operator enum** evaluated against the `FormDataJson` of the running instance. Never a string expression, no Roslyn, no DynamicLinq.

Supported operators: `Eq`, `Gt`, `Gte`, `Lt`, `Lte`, `Contains`, `In`, `NotEq`, `NotContains`, `NotIn`.

`In` / `NotIn` value lists are capped at **100 items** (mirrors the Analysis Mode cap).

Fields must be declared in `fieldWhitelist` or the routing evaluator fails closed.

---

## 4. Consumer Migration Step

`WalkingTec.Mvvm.WorkFlow` ships **zero migrations** — identical to `WalkingTec.Mvvm.Etl`.

**Step 1** — Call `ApplyWorkFlowModels()` from your application's `DataContext.OnModelCreating`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyEtlModels();        // if also using Etl
    modelBuilder.ApplyWorkFlowModels();   // WorkFlow tables
}
```

**Step 2** — Generate the migration in your app project:

```bash
dotnet ef migrations add WorkFlow_InitialCreate \
  --context DataContext \
  --project YourApp/YourApp.csproj \
  --startup-project YourApp/YourApp.csproj
```

This creates 9 tables: `Wf_ProcessDefinition`, `Wf_ProcessDefinitionVersion`, `Wf_ProcessInstance`, `Wf_NodeInstance`, `Wf_ApprovalTask`, `Wf_WorkflowEventLog`, `Wf_CcRecord`, `Wf_DelegationRule`, `Wf_WorkflowTimer`.

`Wf_DelegationRule` and `Wf_WorkflowTimer` ship in the Sprint-1 schema (zero migration churn when their waves land).

---

## 5. DI Registration (`Program.cs` / `Startup.cs`)

```csharp
// Required — registers engine, approver resolver, routing evaluator, completion handlers.
services.AddWtmWorkFlow(options =>
{
    // Safe defaults — change only with explicit intent.
    options.WithdrawPolicy         = WithdrawPolicy.BeforeFinalApproval; // default
    options.InitiatorAutoApprove   = false;                              // default; see note below
    options.AutoApproveOnMissingHandler = AutoApproveOnMissingHandlerPolicy.FailClose; // default
    options.MaxLevel               = 5;
    options.MaxReturnLoops         = 3;
});

// Opt-in — wires IWtmWebhookSink into the engine notifier.
// No-op when no sink is registered.
services.AddWtmWorkFlowNotifications();

// Opt-in — add shared webhook sinks (configure at least one provider).
services.AddWtmWebhookSink(o => {
    o.DingTalk.Enabled   = true;
    o.DingTalk.WebhookUrl = Environment.GetEnvironmentVariable("DINGTALK_WEBHOOK");
});

// Opt-in Wave 5 — durable timeout poller (not yet implemented).
// services.AddWtmWorkFlowTimers();
```

### `InitiatorAutoApprove = false` (default — migration note)

If your project previously relied on a draft behavior where the initiator being the first approver caused that step to be silently auto-skipped, set this explicitly:

```csharp
options.InitiatorAutoApprove = true; // explicit opt-in required
```

Leaving it at `false` (the default) is the safe, compliant choice for most environments.

### `DBTypeEnum.Memory` is unsupported

The engine throws `InvalidOperationException` at startup when `DBTypeEnum.Memory` (EF InMemory) is detected. The guarded-CAS primitive (`ExecuteUpdateAsync`) is not supported by EF InMemory. Use SQLite, SQL Server, PostgreSQL, MySQL, Oracle, or DaMeng.

---

## 6. Engine Controllers and Endpoints

All controllers extend `BaseController` with `[ActionDescription]` / `FunctionPrivilege` gates on every action.

| Controller | Key Endpoints |
|---|---|
| `ProcessDefinitionController` | `GET /api/_workflow/definitions` · `POST /api/_workflow/definitions/{id}/publish` · `POST /api/_workflow/definitions/validate` · `GET /api/_workflow/definitions/{key}/versions` |
| `WorkflowInstanceController` | `POST /api/_workflow/instances/start` · `POST /api/_workflow/instances/{id}/withdraw` · `GET /api/_workflow/instances/{id}/timeline` |
| `WorkflowTaskController` | `GET /api/_workflow/tasks/mine` · `POST /api/_workflow/tasks/{id}/approve` · `POST /api/_workflow/tasks/{id}/reject` · `POST /api/_workflow/tasks/{id}/return` |

Approver-eligibility is enforced at the controller action layer (`AssigneeITCode` match) via `WorkflowActionVM` — controllers never touch `DC` directly.

---

## 7. Opt-in Notifications (`IWorkflowNotifier`)

Register `AddWtmWorkFlowNotifications()` to wire `IWorkflowNotifier`. When no `IWtmWebhookSink` is registered the notifier is a silent no-op on every event.

**Notification events fired:**

| Event | When | Level |
|---|---|---|
| `NotifyTaskAssignedAsync` | Node activates — task assigned to an approver | Info |
| `NotifyApprovedAsync` | Approver approves a task | Info |
| `NotifyRejectedAsync` | Approver rejects a task | Warning |
| `NotifyInstanceCompletedAsync` | Instance reaches `Approved` terminal state | Info |
| `NotifyWithdrawnAsync` | Initiator withdraws the instance | Warning |
| `NotifyReturnedToInitiatorAsync` | Approver returns task to initiator (回退) | Warning |

**Non-blocking guarantee:** all notifications are sent **after** the engine transaction commits. A delivery failure is logged at `Error` level and **never** propagates to the engine caller — a webhook error cannot roll back an approval decision.

**No PII / form data in cards:** cards carry only instance/task identifiers, node key, actor ITCode, and decision outcome. `FormDataJson` is never included.

---

## 8. Admin Grid for Process Definitions

The module ships a read-only admin grid (`ProcessDefinitionListVM`) for browsing process definitions.

```csharp
// In your area controller (inherit BaseController):
[ActionDescription("流程定義管理")]
public class WfProcessDefinitionController : BaseController
{
    [ActionDescription("列表")]
    public ActionResult Index()
    {
        var vm = CreateVM<ProcessDefinitionListVM>();
        return PartialView(vm);
    }
}
```

The grid is tenant-scoped automatically (DataContext query filter on `TenantCode`). Searcher fields: `Code` (partial match), `Name` (partial match), `Category`, `IsEnabled`. Grid actions include a read-only detail dialog and a version-history dialog.

In-grid editing is intentionally absent — definitions are published via the API/designer endpoint.

---

## 9. Sandboxed Routing Whitelist

The routing evaluator enforces two safety layers:

1. **Publish-time validation** — `RoutingValidator` rejects any rule referencing an off-whitelist field or type-incompatible operator; requires a `default` branch on every Condition node.
2. **Runtime re-validation** — `WhitelistRoutingEvaluator` validates again at evaluation time; an off-whitelist field access fails closed (`ROUTING_FIELD_NOT_ALLOWED`).

There is no string expression, no Roslyn, no `DynamicLinq`, and no SQL injection surface. The routing model is positive-enumeration over a closed operator set — not a blocklist.

---

## 10. Concurrency Guarantees

Every state-changing operation routes through `GuardedTransition` — a conditional `ExecuteUpdateAsync` with the expected current state in the WHERE clause:

```csharp
// claimed == 1 → this caller won.  claimed == 0 → another actor already acted → idempotent no-op.
var claimed = await db.Set<ApprovalTask>()
    .Where(t => t.ID == taskId && t.State == TaskState.Pending && t.RowVer == expectedRowVer)
    .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, TaskState.Approved)
                               .SetProperty(t => t.RowVer, x => x.RowVer + 1), ct);
```

Race classes handled:

| Race | CAS target |
|---|---|
| 或签 concurrent approvers | NodeInstance `WHERE State == Activated` |
| 会签 double-completion | NodeInstance `WHERE State == Activated` (threshold-crossing transition) |
| 撤回 vs. final-approve | ProcessInstance `WHERE State == Running AND RowVer == @v` |
| 超时 vs. human (Wave 5) | ApprovalTask `WHERE State == Pending` |

`DBTypeEnum.Memory` (EF InMemory) is unsupported — the engine fails fast at startup (see §5).

---

## 11. Audit Trail

- `[AuditChanges]` on `ProcessDefinition`, `ProcessDefinitionVersion`, `ProcessInstance`, `NodeInstance`, `ApprovalTask`, `DelegationRule` — VM-driven CRUD writes `ChangeLog` automatically.
- **Authoritative engine audit = the append-only `WorkflowEventLog`** — engine transitions use `ExecuteUpdateAsync` (bypasses the EF change tracker), so `[AuditChanges]` does NOT see them. Every engine state flip writes a `WorkflowEventLog` row inside the transaction. Timeline endpoint: `GET /api/_workflow/instances/{id}/timeline`.

---

## 12. 委托/转交 (Delegation) — Wave 4

### What is a DelegationRule?

A `DelegationRule` grants one principal (delegator) the ability to transfer approval authority to a delegate for a bounded time window. It is a **standing rule** — it affects the next node activation that resolves the delegator as an approver, not in-flight tasks that were already minted before the rule was created.

```
Wf_DelegationRule
  DelegatorITCode   VARCHAR  -- the approver granting authority
  DelegateITCode    VARCHAR  -- the person receiving authority
  StartsUtc         DATETIME -- inclusive window start (app-supplied, never DB GETDATE())
  ExpiresUtc        DATETIME -- inclusive window end; NULL = no expiry
  IsEnabled         BIT      -- soft on/off without deletion
```

When the `DelegationResolvingDecorator` resolves approvers for a node, any delegator whose `DelegationRule` is active at mint time is replaced by the delegate. The resulting `ApprovalTask` records `DelegatedFromITCode` (the original principal) and `DelegationRuleId` for provenance.

### Authority Freezing — `DelegationWindowMode`

| Mode | When window is checked | Frozen at |
|---|---|---|
| `AtAssignment` (default) | Only at mint time | Authority is frozen the moment the task is created |
| `AtAction` (opt-in) | At mint **and** again at claim time | Window re-checked on every approve/reject |

```csharp
services.AddWtmWorkFlow(options =>
{
    // default — authority frozen at mint; expired rule does not revoke in-flight tasks
    options.DelegationWindowMode = DelegationWindowMode.AtAssignment;

    // opt-in — window re-checked at claim; expired tasks remain Pending until manually cleared
    // Recommended only when paired with Wave-5 AddWtmWorkFlowTimers (not yet shipped)
    // options.DelegationWindowMode = DelegationWindowMode.AtAction;
});
```

**AtAssignment** (default): once a task is created, the delegate's authority is permanent regardless of whether the rule window later expires. This is the low-surprise default — revocation requires an explicit admin call to `RevokeDelegationAsync`.

**AtAction**: the `ClaimDelegatedTaskAsync` CAS adds `AND (DelegationExpiresUtc IS NULL OR @now <= DelegationExpiresUtc)` to the guard predicate. There is no TOCTOU gap — the window check and the state flip happen in the same atomic `ExecuteUpdateAsync`. If the window has expired the engine returns `WorkflowActionResult.DelegationExpired` (a distinct closed-union result code, not `AlreadyHandled`). Without Wave-5 timeout reaper, expired AtAction tasks remain Pending indefinitely and require either manual reassignment or `RevokeDelegationAsync`.

### Hop Cap and Cycle Prevention

- `MaxDelegationHops` (default 3): the decorator refuses to create a chain longer than this. A `DelegationHopsExceeded` result is returned.
- **Cycle detection**: if the target delegate is also a delegator whose chain would loop back to any member of the current chain, the entire node minting fails closed (`DelegationCycle`). Partial activation is never written.
- **转办 collision refusal**: if the delegate is already a direct participant in the same node (has a Pending task), the delegation is refused (`DelegateAlreadyParticipant`).

### Revoking Delegation — `RevokeDelegationAsync`

An admin or the delegator can revoke all in-flight delegated tasks for a given rule:

```csharp
// Revokes all Pending tasks minted under delegationRuleId.
// Each revoked task is atomically reverted to the original principal (DelegatedFromITCode).
// Returns the count of successfully revoked tasks.
int revoked = await engine.RevokeDelegationAsync(
    delegationRuleId: ruleId,
    actorITCode: adminITCode,
    reason: "Rule expired — manual sweep",
    ct: cancellationToken);
```

Internally `RevokeDelegationAsync` iterates `GuardedTransition.RevokeDelegatedTasksAsync`, an `IAsyncEnumerable<RevokeDelegatedTaskOutcome>` that performs per-task CAS operations:

```
For each Pending task under delegationRuleId:
  CAS: WHERE ID == taskId AND State == Pending AND RowVer == @v AND Generation == @g
  SET: AssigneeITCode ← DelegatedFromITCode, clear delegation fields, RowVer += 1
  → Revoked     (rows == 1: task reverted to principal)
  → NotPending  (rows == 0: another actor acted concurrently — idempotent no-op)
  → NotDelegated (DelegatedFromITCode was blank — not a delegation task, skipped)
  Best-effort: bump ApproverSetEpoch on the owning NodeInstance
```

Each revoked task writes a `WorkflowEventLog` entry (`EventAction.Delegate`, detail `[委托撤销 rule=<id>]`). The event log write is best-effort — revocation itself succeeds even if the audit row fails.

### Mid-Flight Invariant

Delegation **never changes `TotalRequired`** on an in-flight node. It is always a 1-for-1 task reassignment: one Pending task flips its `AssigneeITCode` from delegator → delegate (via `ReassignTaskAssigneeAsync` CAS at activation time). Approval quorum counts are unaffected.

### Race Handling

| Race | Outcome |
|---|---|
| Concurrent approve vs. expired AtAction window | CAS miss → `DelegationExpired` (disambiguated by re-read after miss) |
| Concurrent revoke vs. claim | Exactly one wins (CAS); loser returns `NotPending` or `AlreadyHandled` |
| Concurrent revoke vs. revoke | Both are CAS — first writer wins per task; second gets `NotPending` |
| AtAction approve exactly at boundary | `@now == DelegationExpiresUtc` is inclusive — task is claimed (see T-DEL-08) |

### Startup Warnings

When `DelegationWindowMode == AtAction` the engine logs a `LogWarning` at startup reminding operators to pair it with Wave-5 `AddWtmWorkFlowTimers`. This is a compliance-relevant configuration.

---

## 13. Related Documentation

- [Analysis Mode](/analysis-mode) — field whitelist pattern reused by the routing evaluator
- [Lookup Cache](/lookup-cache) — caching patterns available to approver resolvers
- [Production Readiness](/production-readiness) — scorecard and security checklist
- [ETL Module](/etl) — sibling module; `AddWtmEtl` / `AddWtmEtlAlerts` DI pattern mirrored here
