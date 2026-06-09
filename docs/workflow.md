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

## 12. Related Documentation

- [Analysis Mode](/analysis-mode) — field whitelist pattern reused by the routing evaluator
- [Lookup Cache](/lookup-cache) — caching patterns available to approver resolvers
- [Production Readiness](/production-readiness) — scorecard and security checklist
- [ETL Module](/etl) — sibling module; `AddWtmEtl` / `AddWtmEtlAlerts` DI pattern mirrored here
