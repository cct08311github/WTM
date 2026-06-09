<!-- Design spec for WalkingTec.Mvvm.WorkFlow approval engine. Tracking: issue #240 (sub-epic of #193).
     Produced by a multi-agent design workflow (research → 3 proposals → judge panel → synthesis → completeness critic),
     then revised to v2 fixing the critic's 2 HIGH foundation issues (consumer-owned migration; 7-provider concurrency gate). -->

# WalkingTec.Mvvm.WorkFlow — Implementation Specification v2

> Status: implementation-ready (v2 — corrected). Target framework version: **10.8.0** (next minor). Branch: `dotnet10`. Origin: Gitea.
> Every load-bearing claim below was verified against repo source (file:line cited inline).
> v2 changes from v1: HIGH-1 DbContext/migration anchor corrected; HIGH-2 7-provider + Memory-guard + Sprint-0 concurrency spike added; MEDIUM InitiatorAutoApprove default changed false; §2.6 packaging deferred / .sync checklist added; FilterOperator order fixed; deferred modes honestly flagged; timeline 6–8 weeks.

---

## 1. Overview & Design Principles

### 1.1 What this is

`WalkingTec.Mvvm.WorkFlow` is a greenfield sibling library (peer to `WalkingTec.Mvvm.Etl`) that adds a **Chinese-corporate approval/workflow engine** to WTM: 串签 / 会签 / 或签 / 加签 / 委托 / 撤回 / 回退 / 条件路由 / 抄送 / 超时, version-pinned definitions, sandboxed routing, race-safe transitions, multi-tenant + audited by construction, with an opt-in low-code designer.

### 1.2 Backbone + grafts

**Backbone: the Extensibility-first proposal.** It contributes the single sharpest modeling decision — 串签/会签/或签 collapse into **one generic Approval node + one `ApproveMode` enum + one completion-policy dispatcher**, not three node types — and the token/marking runtime that natively holds two nodes active (required for 会签 parallelism and inclusive joins).

**Grafted from MVP-first:**
- The **single concurrency primitive standardized from day one** — the verified `TokenService` guard-in-WHERE CAS (`TokenService.cs:95-116`: claim via `ExecuteUpdateAsync(...).Where(active conditions)`, branch on `claimed == 0` → friendly idempotent no-op). Routed through **one shared `GuardedTransition` helper** every state change MUST use.
- The **DIRECT-`: PersistPoco, ITenant`-descendant hard rule** with a HasQueryFilter-presence test, because `DataContext.cs:162` only applies the tenant/soft-delete filter to *root* types (`BaseType == null || BaseType.IsAbstract || BaseType == TopBasePoco || BaseType == BasePoco || BaseType?.BaseType == TreePoco`) — multi-level inheritance silently leaks cross-tenant data.
- **Honest auditability disclosure**: `[AuditChanges]`/ChangeLog does **not** capture engine `ExecuteUpdateAsync` writes (they bypass the EF change-tracker), so the immutable append-only `WorkflowEventLog` is the authoritative engine audit.

**Grafted from Integration-first:**
- Model the whole domain as **ordinary WTM Models** so BaseVM CRUD/Search, code-gen, RBAC, `[AuditChanges]`, tenant filters, and `IWtmWebhookSink` apply with near-zero net-new plumbing.
- The exact `AddWtmEtl`/`AddWtmEtlAlerts` DI split and the optional-`IWtmWebhookSink`-via-`GetService` null-safe pattern.

### 1.3 Weaknesses the judges raised — and how this spec fixes each

| # | Weakness raised | Fix in this spec |
|---|---|---|
| W1 | **会签 double-completion gap**: count-increment and node-completion are two separate guarded writes; could double-complete or lose-completion. | §7.4 makes the **threshold-crossing node-completion transition itself a guarded CAS** (`WHERE NodeInstance.State == Activated`). Count increments are advisory; completion is decided by exactly one CAS winner. Conformance test T-CONC-3. |
| W2 | **RowVersion 7-provider portability hand-waved**; `TopBasePoco` has no token (confirmed — `PersistPoco`/`ITenant` carry none). | §7.2 ships a **concrete per-provider `RowVer` strategy** for all 7 `DBTypeEnum` values (§7.2 table) **plus a Sprint-0 concurrency spike** and a **Sprint-1 7-provider conformance gate** (§10) that fails the build if any relational provider no-ops the CAS. `DBTypeEnum.Memory` is explicitly unsupported at runtime (fail-fast startup guard). |
| W3 | **Oracle `ExecuteUpdateAsync` translation gap** (repo has Oracle stopgaps, #147). | §7.5 names an explicit **Oracle fallback**: `SELECT ... FOR UPDATE` + guarded `UPDATE` inside the same transaction when an `ExecuteUpdateAsync` predicate fails to translate; gated by the same conformance suite. |
| W4 | **Token migration as deferred big-bang** (MVP-first deferred the token engine). | This spec adopts the **token/marking core in Sprint 1** (backbone choice). There is no later "migrate the walker" rewrite. |
| W5 | **Five registries over-engineered before they earn keep.** | §3/§8 ship **ONE seam in MVP** — `INodeKindHandler` (the completion-policy dispatcher) — plus thin `IApproverResolver` / `IRoutingEvaluator` interfaces with exactly the built-in implementations needed. |
| W6 | **`WorkflowEventLog` replay algorithm unspecified** (回退/撤回 correctness). | §5.7 specifies the **explicit discard-and-recount projection**: live execution state is authoritative; the event log is *append-only audit*, never replayed for correctness. On node re-entry, the engine **deletes (soft) live `ApprovalTask`/`NodeInstance` rows for the discarded span and re-materializes from the pinned version graph** — no event-sourcing replay. |
| W7 | **Routing "zero new evaluator" misleading** — Analysis `ApplyFilters` is `IQueryable→IQueryable` for SQL translation; routing needs in-memory boolean over a deserialized form POCO. | §6 is explicit: we **reuse the discipline** but build a **new `Expression<Func<IDictionary<string,object?>,bool>>` compiled-and-cached-by-ContentHash** evaluator. Named as net-new, security-reviewed. |
| W8 | **Durable scheduler reinvents Quartz** (Etl uses Quartz). | §5.10 + §12 reuse the **Etl `EtlSchedulerService`/`EtlHostedService` durable pattern** for `WorkflowTimer`. |
| W9 | **Org-resolution recursion fragility** under-designed. | §5.1/§5.5 define a closed **`IApproverResolver` contract** with mandatory caps, cycle detection, human-dedupe, and an admin-fallback Result code. |
| W10 | **No visual designer in MVP = adoption gap**. | §4 versions the GraphJson schema with an explicit `schemaVersion` integer **published as a contract from Sprint 1**; Wave 4 ships the designer. |
| W11 | **DelegationRule point-in-time default ambiguous** (red-line). | §5.5 makes the window-evaluation policy a **loud, explicit `DelegationWindowMode` enum** (`AtAssignment` default vs `AtAction`), documented in CHANGELOG. |
| W12 | **5-week MVP optimistic by ~2×.** | §12 budgets **6–8 weeks** including a Sprint-0 concurrency spike and Sprint-1 MVP; defers 回退-to-node / 加签 / 委托 / 超时 / designer to named waves. |

### 1.4 Non-negotiable invariants

1. **Every state-changing operation routes through `GuardedTransition`** (guard-in-WHERE CAS, branch on rows-affected). No direct `SaveChanges`-based state flip for transitions.
2. **Every entity is a DIRECT `: PersistPoco, ITenant` (or `: BasePoco, ITenant`) descendant.** A test asserts `HasQueryFilter` is present on each.
3. **Definitions are immutable + version-pinned.** Instances FK the *version*, never the head. No `DoEdit` path on `ProcessDefinitionVersion`; `[BindNever]` on its write surface (mirrors #123).
4. **Routing is whitelist + closed-enum + Expression-Tree only.** No Roslyn / DynamicLinq / string-eval. Fail-closed on no-match.
5. **All timestamps via `Wtm.TimeProvider`**, never `DateTime.Now`.
6. **All concurrency tests on SQLite shared-in-memory**, never EF InMemory (cannot translate `ExecuteUpdateAsync`; #119/#162).
7. **Never bypass the VM layer** to hit DataContext from controllers; never `IgnoreQueryFilters()`.
8. **`DBTypeEnum.Memory` is unsupported** for the workflow engine — fail fast at startup with a clear message when detected.

---

## 2. Project Scaffolding

Mirror `WalkingTec.Mvvm.Etl` structure exactly.

### 2.1 `src/WalkingTec.Mvvm.WorkFlow/WalkingTec.Mvvm.WorkFlow.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>WalkingTec.Mvvm.WorkFlow</AssemblyName>
    <Title>$(AssemblyName)</Title>
    <Description>WTM WorkFlow Module - approval/workflow engine (串签/会签/或签/加签/委托/撤回/回退/条件路由/抄送/超时)</Description>
    <IsPackable>true</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <Import Project="..\..\common.props" />

  <ItemGroup>
    <InternalsVisibleTo Include="WalkingTec.Mvvm.WorkFlow.Test" />
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <!-- No new PackageReference needed for MVP. CPM resolves all versions; NEVER add Version= here. -->

  <ItemGroup>
    <ProjectReference Include="..\WalkingTec.Mvvm.Mvc\WalkingTec.Mvvm.Mvc.csproj" />
  </ItemGroup>
</Project>
```

### 2.2 `test/WalkingTec.Mvvm.WorkFlow.Test/WalkingTec.Mvvm.WorkFlow.Test.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <Import Project="..\..\common.props" />

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="MSTest.TestAdapter" />
    <PackageReference Include="MSTest.TestFramework" />
    <PackageReference Include="Moq" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.Data.Sqlite" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\WalkingTec.Mvvm.WorkFlow\WalkingTec.Mvvm.WorkFlow.csproj" />
    <ProjectReference Include="..\WalkingTec.Mvvm.Test.Mock\WalkingTec.Mvvm.Test.Mock.csproj" />
  </ItemGroup>
</Project>
```

> Test follows the Mvc.Tests SQLite-shared-in-memory harness. For the **7-provider conformance gate** (§10), the test project additionally references `Microsoft.EntityFrameworkCore.SqlServer`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Pomelo.EntityFrameworkCore.MySql`, `Oracle.EntityFrameworkCore`, and `EntityFrameworkCore.Dm` (达梦) **behind a `[TestCategory("ProviderConformance")]` filter** run nightly / on release, not on every PR (CI-capacity mitigation).

### 2.3 `Directory.Packages.props`

No new entries needed for the MVP (`Microsoft.Data.Sqlite`, EF Sqlite, test packages already present for Etl.Test). If a later wave needs a package, add the `<PackageVersion>` here only — never a `Version=` on the csproj `<PackageReference>`.

### 2.4 Solution registration

- `WalkingTec.Mvvm.sln`: add the two projects, `ProjectTypeGuid` `FAE04EC0-301F-11D3-BF4B-00C04F79EFBC`.
- `core.slnf`: add `src/WalkingTec.Mvvm.WorkFlow/...csproj` and `test/WalkingTec.Mvvm.WorkFlow.Test/...csproj`.
- `ci.slnf`: same two paths (it extends core.slnf project list).

### 2.5 DI entrypoint — `src/WalkingTec.Mvvm.WorkFlow/ServiceCollectionExtensions.cs`

```csharp
#nullable enable
public static class ServiceCollectionExtensions
{
    /// Registers the workflow engine. Mirrors AddWtmEtl: Configure<WorkFlowOptions>, the engine
    /// (scoped), node-kind dispatcher + built-in completion policies, approver resolver, routing
    /// evaluator (singleton, caches compiled predicates by ContentHash). Does NOT require IWtmWebhookSink.
    /// Throws InvalidOperationException at startup when DBTypeEnum.Memory is detected — the engine
    /// requires a real relational provider (ExecuteUpdateAsync is not supported by EF InMemory).
    public static IServiceCollection AddWtmWorkFlow(
        this IServiceCollection services, Action<WorkFlowOptions>? configure = null)
    {
        if (configure != null) services.Configure(configure);
        else services.Configure<WorkFlowOptions>(_ => { });

        services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        services.AddScoped<IApproverResolver, DefaultApproverResolver>();
        services.AddSingleton<IRoutingEvaluator, WhitelistRoutingEvaluator>(); // caches by ContentHash
        services.AddSingleton<INodeKindDispatcher, NodeKindDispatcher>();       // built-in completion policies
        return services;
    }

    /// Opt-in. Wires an already-registered IWtmWebhookSink (resolved via GetService, null-safe) into
    /// the engine's notifier. Mirrors AddWtmEtlAlerts. No-op when no sink is registered.
    public static IServiceCollection AddWtmWorkFlowNotifications(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowNotifier, WebhookWorkflowNotifier>();
        return services;
    }

    /// Opt-in (Timeout wave). Adds the durable timer poller (modeled on EtlHostedService).
    public static IServiceCollection AddWtmWorkFlowTimers(this IServiceCollection services)
    {
        services.AddSingleton<ITimeoutActionRegistry, TimeoutActionRegistry>();
        services.AddHostedService<WorkflowTimerHostedService>();
        return services;
    }
}

public static class WorkFlowDbContextExtensions
{
    /// Called from the CONSUMER's DataContext.OnModelCreating (NOT from FrameworkContext) — exactly
    /// mirroring how ApplyEtlModels() works (ServiceCollectionExtensions.cs:141; consumer DataContext.cs:349).
    /// Registers all WorkFlow entities with ToTable/HasIndex/HasOne/RowVer mapping.
    /// Does NOT add HasQueryFilter — auto-applied by DataContext for ITenant/IPersistPoco.
    public static ModelBuilder ApplyWorkFlowModels(this ModelBuilder builder) { /* §11 */ return builder; }
}
```

### 2.6 Packability and NuGet publish — DEFERRED to stable release

**Current publish set (verified in `.github/workflows/publish-nuget.yml`):** Core, Mvc, TagHelpers.LayUI only — 3 packages.

**Recommendation:** Keep WorkFlow as a **consumer `ProjectReference`** initially (like Etl, which is intentionally excluded from the publish list). Defer adding a 4th NuGet package until the engine is stable across at least one production use cycle.

**If WorkFlow is published later**, a checklist of required changes applies:
1. Set `IsPackable=true` in the csproj (already set in §2.1 — confirm before enabling).
2. Add a `Pack WalkingTec.Mvvm.WorkFlow` step to `publish-nuget.yml` and `scripts/publish-to-gitea.sh`.
3. **Update `.sync/github-sanitize.sed`** to sanitize the new assembly name and any internal references from the GitHub mirror output.
4. **Re-run the leak gate** (`git grep -lI <marker>` for mac-mini/tailde842d/GITEA_*/.ts.net) on the sanitized commit before tagging — the auto-sync pipeline is verified end-to-end (10.6.0) but a new assembly introduces new surfaces that could carry internal hostnames in `<RepositoryUrl>` or XML doc comments.
5. Update the smoke-test step in `publish-nuget.yml` to also install `WalkingTec.Mvvm.WorkFlow`.

---

## 3. Domain Model

All entities live in `src/WalkingTec.Mvvm.WorkFlow/Models/`. **Every one is a DIRECT descendant** of `PersistPoco` (audit + soft-delete + tenant) or `BasePoco` (audit-only + tenant), plus `ITenant`. No multi-level inheritance (invariant #2).

`RowVer` below = the per-provider concurrency token defined in §7.2 (NOT present on `BasePoco`/`PersistPoco` — confirmed; net-new for WorkFlow entities).

**Important note on `ProcessDefinitionVersion`:** This entity is the most novel surface in the module. The immutable content-hashed versioning pattern (VersionNo, ContentHash, SHA-256, BindNever on write-root, publish-only insert, no DoEdit) has **no analog in the Etl module** (`EtlJobDefinition` is a plain mutable `BasePoco` with no versioning). All associated mechanisms — canonical-JSON determinism, SHA-256 hashing, version-pin-on-publish, [BindNever]-on-write-root — are greenfield and require dedicated invariant tests (§10).

| Entity | Base | `[AuditChanges]` | Purpose | Key fields |
|---|---|---|---|---|
| **ProcessDefinition** | `PersistPoco, ITenant` | yes | Mutable logical head / catalog entry. Editing repoints `CurrentVersionId`. | `ID`, `TenantCode`, `Code` (unique per tenant), `Name`, `Category`, `IsEnabled`, `CurrentVersionId` (FK→Version, nullable), `IsValid` |
| **ProcessDefinitionVersion** | `PersistPoco, ITenant` | yes (read/publish only) | **IMMUTABLE** content-hashed snapshot. Instances FK THIS. No `DoEdit`; `[BindNever]` on `GraphJson`/`ContentHash`. GREENFIELD — no Etl precedent. | `ID`, `TenantCode`, `DefinitionId` (FK), `VersionNo` (int, monotonic per definition), `SchemaVersion` (int), `GraphJson` (canonical text), `ContentHash` (SHA-256 hex), `PublishedAt`, `PublishedBy`, `IsValid` |
| **ProcessInstance** | `PersistPoco, ITenant` | yes | One running approval. FKs the VERSION. | `ID`, `TenantCode`, `DefinitionVersionId` (FK, pinned), `State` (enum), `InitiatorITCode`, `BusinessType`, `BusinessKey`, `FormDataJson`, `RowVer`, `IsValid` |
| **NodeInstance** | `BasePoco, ITenant` | yes | Runtime materialization of one node. CAS target for 或签/会签 completion. | `ID`, `TenantCode`, `InstanceId` (FK), `NodeKey`, `NodeKind` (enum), `State` (enum), `ApproveMode` (enum, copied for query speed), `ApprovePercent` (decimal?), `RejectGate` (enum), `RejectPolicy` (enum), `ApprovedCount`, `RejectedCount`, `TotalRequired`, `SequencePointer` (int, 串签), `DecidedBy`, `ActivatedAt`, `RowVer` |
| **ApprovalTask** | `PersistPoco, ITenant` | yes | One approver's inbox row. CAS target for claim. | `ID`, `TenantCode`, `NodeInstanceId` (FK), `AssigneeITCode`, `State` (enum), `SequenceOrder` (int), `DelegatedFromITCode` (str?), `AddedByITCode` (str?), `IsRuntimeInjected` (bool), `Comment`, `ActedAtUtc`, `DueUtc` (datetime?), `RowVer`, `IsValid` |
| **WorkflowEventLog** | `BasePoco, ITenant` | no (it IS the audit) | **APPEND-ONLY** 流转记录. One row per transition. Never updated. | `ID`, `TenantCode`, `InstanceId` (FK), `Seq` (monotonic per instance), `ActorITCode`, `Action` (enum), `NodeKey`, `OnBehalfOfITCode` (str?), `AddedByITCode` (str?), `Reason`, `BeforeState`, `AfterState`, `OccurredUtc` |
| **CcRecord** | `BasePoco, ITenant` | no | 抄送 receipt. Non-blocking by construction. | `ID`, `TenantCode`, `InstanceId` (FK), `NodeKey`, `RecipientITCode`, `Trigger` (enum OnSubmit/OnNode/OnComplete), `SentAtUtc`, `ReadAtUtc` (datetime?), `Comment` |
| **DelegationRule** | `PersistPoco, ITenant` | yes | Standing time-bounded 委托. Schema present from Sprint 1. Engine consults it in the 委托 wave. | `ID`, `TenantCode`, `PrincipalITCode`, `DelegateeITCode`, `ScopeDefinitionCode` (str?, null=all), `StartUtc`, `EndUtc`, `IsValid` |
| **WorkflowTimer** | `BasePoco, ITenant` | no | Durable 超时 timer. Schema present from Sprint 1; consumed in the 超时 wave. | `ID`, `TenantCode`, `ApprovalTaskId` (FK?), `NodeInstanceId` (FK), `FireAtUtc`, `Action` (enum), `IdempotencyKey` (unique), `Status` (enum Armed/Fired/Cancelled), `RemindCount`, `RowVer` |

**NodeDefinition / Transition are NOT tables.** They are typed records *embedded in* `ProcessDefinitionVersion.GraphJson` (§4), deserialized on load and cached by `ContentHash`. This preserves the version-pin invariant (one immutable blob, no FK rows that an admin edit could retroactively corrupt).

**Relationships / indexes** (declared in `ApplyWorkFlowModels`, §11):
- `ProcessDefinitionVersion.DefinitionId` → `ProcessDefinition`; unique `(TenantCode, DefinitionId, VersionNo)`.
- `ProcessInstance.DefinitionVersionId` → `ProcessDefinitionVersion`; index `(TenantCode, BusinessType, BusinessKey)`.
- `NodeInstance.InstanceId` → `ProcessInstance`; index `(InstanceId, State)`.
- `ApprovalTask.NodeInstanceId` → `NodeInstance`; index `(TenantCode, AssigneeITCode, State)` (the inbox query).
- `WorkflowEventLog.InstanceId` → `ProcessInstance`; index `(InstanceId, Seq)`.
- `WorkflowTimer`: index `(Status, FireAtUtc)`; unique `(IdempotencyKey)`.

**State enums (closed):**
- `InstanceState`: `Draft, Running, Approved, Rejected, Withdrawn, Terminated`
- `NodeState`: `Pending, Activated, CompletedApproved, CompletedRejected, Skipped, Returned`
- `TaskState`: `NotYetActive, Pending, Suspended, Approved, Rejected, Transferred, Delegated, AddedPending, Expired, AutoApproved, AutoRejected, Cancelled`
- `ApproveMode`: `Sequential, All, Any`
- `RejectGate`: `Immediate, AfterAll`
- `RejectPolicy`: `TerminateInstance, ReturnToPrev, ReturnToNode, ReturnToInitiator`
- `NodeKind`: `Start, Approval, Condition, Cc, Ack, Join, End`
- `EventAction`: `Submit, Approve, Reject, Withdraw, Return, AddApprover, Transfer, Delegate, AutoAdvance, Skip, Notify, TimeoutFire, FailClosed`

---

## 4. Process-Definition JSON Schema (version-pinned)

The entire graph is serialized to **canonical JSON** (deterministic key ordering — sort map/set-derived keys before serializing, per the repo prompt-cache discipline) and hashed (`ContentHash = SHA-256(GraphJson)`). `schemaVersion` is the published contract version from Sprint 1; evolution is additive-only.

```jsonc
{
  "schemaVersion": 1,
  "key": "PurchaseApproval",
  "name": "采购审批",
  "fieldWhitelist": [
    { "field": "amount",     "clrType": "System.Decimal", "allowedRoles": null },
    { "field": "department", "clrType": "System.String",  "allowedRoles": null },
    { "field": "category",   "clrType": "System.String",  "allowedRoles": null }
  ],
  "nodes": [
    { "nodeKey": "start", "kind": "Start" },
    { "nodeKey": "mgr", "kind": "Approval",
      "approveMode": "Sequential",
      "rejectGate": "Immediate",
      "rejectPolicy": "ReturnToInitiator",
      "approverRule": { "type": "ManagerChain", "maxLevel": 2 },
      "cc": [ { "trigger": "OnNode", "rule": { "type": "Role", "value": "FINANCE_WATCH" } } ],
      "timeout": { "duration": "P2D", "businessCalendar": true, "action": "Remind",
                   "remindEveryHours": 24, "maxReminders": 3 }
    },
    { "nodeKey": "gw_amount", "kind": "Condition",
      "branches": [
        { "rule": { "field": "amount", "operator": "Gt", "value": 10000 }, "target": "cfo" }
      ],
      "default": "finance"
    },
    { "nodeKey": "cfo", "kind": "Approval",
      "approveMode": "All", "approvePercent": null,
      "rejectGate": "Immediate", "rejectPolicy": "TerminateInstance",
      "approverRule": { "type": "Role", "value": "CFO" } },
    { "nodeKey": "finance", "kind": "Approval",
      "approveMode": "Any",
      "rejectPolicy": "ReturnToInitiator",
      "approverRule": { "type": "Role", "value": "FINANCE" } },
    { "nodeKey": "end", "kind": "End" }
  ],
  "transitions": [
    { "from": "start",   "to": "mgr" },
    { "from": "mgr",     "to": "gw_amount" },
    { "from": "cfo",     "to": "finance" },
    { "from": "finance", "to": "end" }
  ]
}
```

**Publish flow (§9):** validate → canonicalize → `ContentHash` → if hash equals `CurrentVersion.ContentHash`, **no-op** (idempotent republish) → else INSERT new `ProcessDefinitionVersion` with `VersionNo = max+1` → repoint `ProcessDefinition.CurrentVersionId`.

---

## 5. The 10 Approval Modes

Three cross-cutting per-node policies are fixed first; the 10 modes are values/operations over them. 串签/会签/或签 are **the three `ApproveMode` values on one generic Approval node** dispatched by `INodeKindHandler` (the one MVP seam).

### 5.1 串签 (Sequential) — `ApproveMode = Sequential`
On node activation, only `ApprovalTask[SequenceOrder == 0]` is `Pending`; the rest are `NotYetActive`. Approve → `GuardedTransition` claims the task (`WHERE State == Pending AND SequenceOrder == @ptr`), advances `NodeInstance.SequencePointer` (CAS `WHERE State == Activated AND SequencePointer == @expected`), sets the next task `Pending`. Last approve → `NodeInstance = CompletedApproved` → engine `AdvanceAsync` to next node.
- **Approver list resolved LAZILY per step** via `IApproverResolver`. `ManagerChain` capped by `MaxLevel`; human-dedupe auto-skips duplicates (logged); initiator==A1 → `WorkFlowOptions.InitiatorAutoApprove` policy.
- **Edge:** later approver acts early → claim affects 0 rows → `Result = TaskNotActive` (HTTP 409). Reject mid-sequence → `NodeInstance = CompletedRejected`, `RejectPolicy` decides.
- Concurrency trivial (one live task).

### 5.2 会签 (Joint / ALL, incl. 比例会签) — `ApproveMode = All`
All tasks `Pending` on activation (parallel inboxes within ONE NodeInstance). Each approve: claim the TASK, increment `ApprovedCount` (advisory). **Completion is a guarded CAS** (§7.4): `need = ApprovePercent == null ? TotalRequired : ceil(TotalRequired * ApprovePercent)`; when `ApprovedCount >= need`, attempt `UPDATE NodeInstance SET State=CompletedApproved WHERE Id=@id AND State==Activated` — exactly one winner advances, late approvals no-op. **Early short-circuit:** complete-rejected the instant `TotalRequired - RejectedCount < need`.
- **RejectGate:** `Immediate` (default) → first reject CAS-closes node, cancels remaining Pending. `AfterAll` → wait all, fail if any rejected.
- **Edge:** concurrent approve+reject → both attempt the completion CAS; first commit wins, loser no-ops. Percent rounding fixed `ceil`. Removed/no-handler approver → `WorkFlowOptions.AutoApproveOnMissingHandler` or route-to-admin, never deadlock.

### 5.3 或签 (Any-one) — `ApproveMode = Any`
All tasks `Pending`. First approve wins the canonical CAS: `UPDATE NodeInstance SET State=CompletedApproved, DecidedBy=@me WHERE Id=@id AND State==Activated`. Winner cancels all sibling `Pending` tasks (`reason=OtherApproverActed`). Loser (`claimed==0`) → `Result = NoOp_AlreadyHandled`, never an error.
- **Reject:** a single reject does NOT fail; node `CompletedRejected` only when last pending approver rejects (`ApprovedCount==0 AND PendingCount==0`).
- **Edge:** "any reject blocks" is NOT 或签 — do not overload.

### 5.4 加签 (Add approver at runtime) — *Wave 4; schema PRESENT Sprint-1*
> **Engine behavior for this mode is DEFERRED to Wave 4 and is NOT yet specified to implementation depth.** The schema supports it (`IsRuntimeInjected`, `AddedByITCode`). Open race question: 加签 onto an in-flight 会签 recomputes `TotalRequired` while approvals are landing concurrently — the CAS story for the threshold-recompute race is unresolved and must be addressed before Wave 4 implementation begins.

Current `Pending` holder (or admin) invokes `AddApprover(taskId, mode∈{Before,After}, approvers[], reason)`. Creates `ApprovalTask` rows with `IsRuntimeInjected=true`. On 回退/re-entry, `IsRuntimeInjected` tasks are DROPPED.

### 5.5 委托 / 代理 (Delegate, time-bounded) — *Wave 4; schema PRESENT Sprint-1*
> **Engine behavior for this mode is DEFERRED to Wave 4 and is NOT yet specified to implementation depth.** Open race question: 委托 transitive-chain human-dedupe interaction with 会签 vote-counting is unresolved — if delegatee is already an approver, does it reduce `TotalRequired`? Must be answered before Wave 4.

Two flavors: **转交 (one-shot)** reassigns one live task; **standing 委托** via `DelegationRule`.
- **Window evaluation policy is an explicit, loud `DelegationWindowMode` enum (W11):** `AtAssignment` (default — task created for D if rule active at assignment) vs `AtAction` (window re-checked at act time). Documented in CHANGELOG; never inferred.
- Chains P→D→E resolved transitively, capped `MaxDelegationHops`, cycle (P→D→P) detected → fall back to principal + admin alert.

### 5.6 撤回 (Initiator withdraws) — *MVP*
Initiator-only (or admin). `WorkFlowOptions.WithdrawPolicy ∈ {BeforeAnyAction(L0), BeforeFinalApproval(L1, default), Disabled(L2)}`. Instance-level CAS: `UPDATE ProcessInstance SET State=Withdrawn WHERE Id=@id AND State==Running AND RowVer==@v`.
- **Race vs final approve:** first CAS wins; approve finalized first → `Result = CannotWithdraw_AlreadyFinal`; withdraw first → approve no-ops.
- On success: all `Pending` tasks cancelled; timers cancelled; `WorkflowEventLog` written. Resubmit RESTARTS from start. Downstream side-effect already fired → surface **irreversibility warning** via notifier.

### 5.7 回退 (Reject-back) — *MVP: `ReturnToInitiator`; Wave 3: `ReturnToPrev`/`ReturnToNode`*
Approver chooses 退回 with a target governed by `RejectPolicy`.
- **MVP `ReturnToInitiator` (T3):** `NodeInstance=Returned`; initiator edits `FormDataJson` and resubmits → restart-all + re-evaluate 条件路由. Single-hop, no token bookkeeping.
- **Wave 3 `ReturnToPrev` (T1) / `ReturnToNode` (T2):** DEFERRED. **Open race questions (must be resolved before Wave 3):** (a) CAS story for the span-deletion itself — a return-triggered soft-delete and an in-flight approve CAS on the same NodeInstance must be guarded (the GuardedTransition story for span-deletion is NOT yet specified); (b) `WorkflowEventLog.Seq` monotonicity after re-entry; (c) timers armed on discarded NodeInstances cancellation ordering; (d) `MaxReturnLoops` accounting across multiple ping-pongs.
- **Discard/recount projection:** live execution state is authoritative. On re-entry the engine **soft-deletes the live `NodeInstance`/`ApprovalTask` rows for the discarded span and re-materializes from the pinned version graph** inside one transaction. `IsRuntimeInjected` tasks in the span are dropped. The `WorkflowEventLog` keeps the discarded approvals for audit but they no longer count. Policy: `ReturnResetMode ∈ {Reset(default), Resume}`. `MaxReturnLoops` guards A↔B ping-pong.

### 5.8 条件路由 (Conditional branch) — *MVP: Exclusive; Wave 3: Inclusive*
A `Condition` node has no human task; transitions instantly when reached. Ordered `branches[]` + MANDATORY `default`. **Exclusive (MVP):** first matching rule wins by defined array order (deterministic). **Inclusive (Wave 3):** all matching branches activate in parallel, re-converge at a `Join` node.
- **Edge:** no match + no default impossible at runtime (publish validation enforces a default); if somehow reached → **FAIL CLOSED** (halt + admin alert). Missing/null field → `false` → default. Skipped branch nodes → `NodeState=Skipped`.

### 5.9 抄送 (CC, notify-only) — *MVP*
`Cc` node or `cc[]` on an approval node. **Structurally cannot block:** creates `CcRecord` rows and continues in the SAME transaction (no task, no wait). Recipients notified via `IWtmWebhookSink`. May mark-read/comment but CANNOT approve/reject. Triggers: `OnSubmit/OnNode/OnComplete`.
- **Edge:** a CC recipient who is also a later approver — roles kept separate. "must-acknowledge-before-proceed" is NOT 抄送 → a separate `Ack` node (Wave 3). CC respects tenant-isolation + permission.

### 5.10 超时 (Timeout) — *Wave 5; schema PRESENT Sprint-1*
> **Engine behavior for this mode is DEFERRED to Wave 5 and is NOT yet specified to implementation depth.** Open question: `AutoApprove` side-effect ordering vs the irreversibility warning (who fires first when both fire within the same GuardedTransition window?).

On node activation with a live task, arm a durable `WorkflowTimer`. The `WorkflowTimerHostedService` (modeled on `EtlHostedService`) polls `Armed` timers idempotently. Actions: `Remind`, `AutoApprove` (explicit opt-in), `AutoReject`, `Escalate`. Timeout action uses the SAME `GuardedTransition` `WHERE Task.State == Pending`; `claimed==0` ⇒ human already acted ⇒ no-op.

---

## 6. Conditional Routing — Sandboxed Expression Model

**Reuses the WTM Analysis *discipline*, not its `IQueryable` path (W7).** Analysis `ApplyFilters` (`AnalysisQueryEngine.Filters.cs`) is `IQueryable→IQueryable` for SQL translation; routing needs an in-memory boolean over the deserialized `FormDataJson`. We therefore build a **new, security-reviewed evaluator** that copies the proven safety shape:

1. **Field whitelist per version** — `GraphJson.fieldWhitelist[]` of `{ field, clrType, allowedRoles? }`.
2. **Closed operator enum** — mirrors `FilterOperator` (`AnalysisQueryRequest.cs:219`, verified):

   ```
   Eq, Gt, Gte, Lt, Lte, Contains, In,
   NotEq, NotContains, NotIn
   ```

   A `RoutingRule` is a structured object `{ field, operator, value }`, AND/OR-composable — **never a string expression, no Roslyn, no DynamicLinq, no `sql()`**.

3. **Validation TWICE** —
   - **Publish-time:** `RoutingValidator` rejects any rule referencing an off-whitelist field or type-incompatible operator; enforces a `default` on every Condition node. Mirrors `AnalysisFieldNotFoundException` (`AnalysisQueryEngine.Filters.cs:47`).
   - **Runtime:** `WhitelistRoutingEvaluator` re-validates against the whitelist (off-list → `ROUTING_FIELD_NOT_ALLOWED`, fail-closed), `ChangeType`-coerces `FormDataJson[field]` to `clrType`, then builds `Expression<Func<IDictionary<string,object?>, bool>>`, **compiles once and caches by `ContentHash`**. `In`/`NotIn` capped at **100** items (`AnalysisQueryEngine.Filters.cs:84`: `if (list.Count > 100) throw new InvalidOperationException(...)`).

4. **Determinism:** rules/branches evaluated in defined array order (Exclusive first-match).
5. **Injection-proof by construction:** positive enumeration (closed operator enum over a whitelisted field set), not a blocklist.

---

## 7. Concurrency & Atomicity

### 7.1 The one primitive — `GuardedTransition`
Every state-changing operation routes through a single shared helper modeled on `TokenService` (`TokenService.cs:95-116`, verified): a conditional `ExecuteUpdateAsync` with the expected current state **in the WHERE clause** + `RowVer`, then branch on rows-affected.

```csharp
// claimed == 1 → I won, advance.  claimed == 0 → another actor/timer already advanced → idempotent
// friendly no-op (NoOp_AlreadyHandled), NEVER an exception, NEVER a double-advance.
var claimed = await dc.Set<TEntity>()
    .Where(predicateOnExpectedStateAndRowVer)
    .ExecuteUpdateAsync(s => s.SetProperty(/* next state */).SetProperty(x => x.RowVer, nextRowVer));
return claimed == 1 ? Won : NoOp;
```

Resolves all four race classes:
- **(a) 或签 approver-vs-approver:** node-level CAS `WHERE State==Activated`.
- **(b) 会签 double-completion (W1):** the **node-completion transition itself is a guarded CAS** `WHERE State==Activated` — count increments are advisory.
- **(c) 撤回-vs-final-approve:** instance-level CAS `WHERE State==Running AND RowVer==@v`.
- **(d) 超时-vs-human:** task-level CAS `WHERE State==Pending`.

### 7.2 RowVer — concrete per-provider strategy; **Sprint-0 spike required**

> **CRITICAL PROJECT RISK:** There is ZERO concurrency-token precedent anywhere in the repo (no `IsRowVersion()`, `UseXminAsConcurrencyToken()`, or `IsConcurrencyToken()` in any entity). `ExecuteUpdateAsync` is proven in exactly ONE real concurrency path (`TokenService.cs`, SQLite+SqlServer). The CAS strategy below is a **hypothesis-to-prove**, not a settled fact. The literal first deliverable is **Sprint-0: a build-failing concurrency spike** (§12) that proves the guarded-CAS on every supported provider before any Wave-1 code is written.

`BasePoco`/`PersistPoco`/`ITenant` carry **no** concurrency token (confirmed); `RowVer` is net-new on WorkFlow entities. Provider-portable mapping declared in `ApplyWorkFlowModels`:

| Provider (`DBTypeEnum`) | Strategy (hypothesis — Sprint-0 proves) |
|---|---|
| **SqlServer** | `byte[] RowVer` mapped `IsRowVersion()` (native `rowversion`). |
| **PgSql** | Shadow `xmin` via `.UseXminAsConcurrencyToken()` (no extra column). |
| **SQLite** | App-incremented `uint RowVer`, compared in WHERE + set to `old+1` in same `ExecuteUpdateAsync`. |
| **MySql** | App-incremented `uint RowVer`, same pattern as SQLite. |
| **Oracle** | App-incremented `uint RowVer`, same pattern; with Oracle fallback (§7.5). |
| **DaMeng (达梦)** | App-incremented `uint RowVer`, same pattern. **Must be validated in Sprint-0 spike** — DaMeng is a primary target-market DB for Chinese-corporate deployments; conformance failure here is a ship-blocker. |
| **Memory** | **UNSUPPORTED.** `DBTypeEnum.Memory` (EF InMemory) cannot execute `ExecuteUpdateAsync`; the engine **fails fast at startup** with `InvalidOperationException("WorkFlow engine requires a real relational provider. DBTypeEnum.Memory is not supported.")`. |

A single `[TestCategory("ProviderConformance")]` suite asserts the CAS returns `1` for the winner and `0` for the loser on **all 6 relational providers**; the build fails if any provider no-ops the guard (catches a silent phantom "race lost"). Run nightly / on release.

### 7.3 Transaction wrapping
`AdvanceAsync` wraps **claim-task + complete-node + activate-next-node + create-next-tasks + arm/cancel-timers + append `WorkflowEventLog`** in ONE `DC` transaction. On `DbUpdateConcurrencyException`, re-read and retry bounded (≤3).

### 7.4 会签 completion invariant (test T-CONC-3)
Test asserts: with N parallel `All`-mode tasks all approving concurrently, the node ends `CompletedApproved` **exactly once**, never stuck `Activated` with all tasks done (lost-completion), never double-advanced.

### 7.5 Oracle fallback (W3)
If `Oracle.EntityFrameworkCore` 10.x fails to translate a specific guarded `ExecuteUpdateAsync` predicate, the engine falls back to `SELECT ... FOR UPDATE` + guarded `UPDATE` inside the same transaction. Selected per-provider at startup via the conformance probe.

### 7.6 Test provider
**SQLite shared-in-memory only** for all unit/race tests (EF InMemory lacks `ExecuteUpdateAsync` — #119/#162). Never EF InMemory.

---

## 8. WTM Integration

### 8.1 BaseVM ViewModels
- `ProcessDefinition` CRUD via `BaseCRUDVM<ProcessDefinition>`; inbox via `BasePagedListVM<ApprovalTask, ...>` filtered by `AssigneeITCode == Wtm.LoginUserInfo.ITCode + State==Pending`; `DelegationRule` via `BaseCRUDVM`. Definitions/inbox get list/search/sort/export/code-gen for free.
- Approve/Reject/Withdraw/Return/AddApprover/Transfer are NOT CRUD — a thin `WorkflowActionVM : BaseVM` delegates to `IWorkflowEngine`. **Controllers never touch `DC` directly** (red line).

### 8.2 Tenant isolation + soft-delete (free, but guarded)
Every entity is a DIRECT `: PersistPoco/BasePoco, ITenant` descendant, so `DataContext.OnModelCreating` (`DataContext.cs:162`) auto-applies `IsValid==true` + `TenantCode==this.TenantCode` query filters. **A test asserts `HasQueryFilter` is present on each WorkFlow entity.**

### 8.3 RBAC — who-can-act
Acting user via `Wtm.LoginUserInfo.ITCode`. Approver-eligibility = `ApprovalTask.AssigneeITCode` match PLUS a page-level `[ActionDescription]`/`FunctionPrivilege` gate on every controller action (the #194 hardening). `IApproverResolver` (role / user / `ManagerChain`) reads `FrameworkUserRole`/`FunctionPrivilege` via the `LoginUserInfo`/`LoadBasicInfoAsync` seams; closed `Result` contract with mandatory caps + cycle detection + admin fallback (W9).

### 8.4 Audit
- `[AuditChanges]` on `ProcessDefinition(Version)`/`ProcessInstance`/`NodeInstance`/`ApprovalTask`/`DelegationRule` so VM-driven CRUD auto-writes `ChangeLog`.
- **Authoritative engine audit = the append-only `WorkflowEventLog`** — because engine transitions use `ExecuteUpdateAsync` they bypass the EF change-tracker and `[AuditChanges]` does NOT see them. Every engine transition writes a `WorkflowEventLog` row inside the transaction.

### 8.5 Notifications — `IWtmWebhookSink` cards
Inject `IWorkflowNotifier` (which resolves `IWtmWebhookSink?` via `GetService`, null-safe, exactly like `EtlAlertService`). On node-activation / approval-needed / completion / withdraw / fail-closed-routing / timeout, build a `WebhookMessage` and `SendAsync(msg, ct)`. Null sink ⇒ no-op. Wired only when `AddWtmWorkFlowNotifications()` is called.

### 8.6 Low-code designer (feasible, additive — W10)
Because the definition IS validated JSON with a closed routing schema and a published `schemaVersion`, a future visual designer is a **thin client editor** that emits the same `GraphJson`. **No engine change.** MVP ships JSON-authored definitions + an Admin grid; the drag-drop canvas lands in Wave 6.

### 8.7 Cache
Definition graphs cached by `ContentHash` (immutable ⇒ no invalidation). Role/group memberships use the distributed cache, invalidated on `FrameworkRole`/`FrameworkUserRole` change.

---

## 9. Public API Surface

**DI:** `AddWtmWorkFlow(this IServiceCollection, Action<WorkFlowOptions>?)`, `AddWtmWorkFlowNotifications(this IServiceCollection)`, `AddWtmWorkFlowTimers(this IServiceCollection)` (timeout wave). `WorkFlowDbContextExtensions.ApplyWorkFlowModels(this ModelBuilder)` called from the **consumer's own `DataContext.OnModelCreating`** (NOT FrameworkContext — see §11).

**Engine (internal seam, via `WorkflowActionVM`):**
- `IWorkflowEngine.StartAsync(processKey, businessType, businessKey, IDictionary<string,object?> formData, ct) → ProcessInstance`
- `ApproveAsync(taskId, comment?, ct) → WorkflowActionResult`
- `RejectAsync(taskId, reason, ct) → WorkflowActionResult`
- `ReturnAsync(taskId, ReturnTarget{Prev|Node(key)|Initiator}, reason, ct) → WorkflowActionResult` *(Node/Prev = Wave 3)*
- `WithdrawAsync(instanceId, ct) → WorkflowActionResult`
- `AddApproverAsync(taskId, AddMode, approvers[], reason, ct)` *(Wave 4)*, `TransferAsync(taskId, toITCode, ct)` *(Wave 4)*
- `AdvanceAsync(instanceId, ct)` — the single internal advancement entry (claim → route → fire joins → mint next → arm/cancel timers → log).

`WorkflowActionResult` = closed `Result<T,E>` with codes: `Approved, NodeCompleted, InstanceApproved, Returned, Withdrawn, NoOp_AlreadyHandled, TaskNotActive, NodeClosed, CannotWithdraw_AlreadyFinal, NotInitiator, NotAuthorized, FailClosedRouting, AdminFallback`.

**Controllers** (`: BaseController`, every action `[ActionDescription]`+`FunctionPrivilege`-gated):
- `ProcessDefinitionController` — CRUD; `POST /api/_workflow/definitions/{id}/publish`; `POST /api/_workflow/definitions/validate`; `GET /api/_workflow/definitions/{key}/versions`.
- `WorkflowInstanceController` — `POST /api/_workflow/instances/start`; `POST /api/_workflow/instances/{id}/withdraw`; `GET /api/_workflow/instances/{id}/timeline`.
- `WorkflowTaskController` — `GET /api/_workflow/tasks/mine`; `POST /api/_workflow/tasks/{id}/approve|reject|return`; `POST .../add-approver` (Wave 4); `POST .../transfer` (Wave 4).
- `DelegationRuleController` — CRUD (Wave 4); `WorkflowCcController` — `GET /api/_workflow/cc/mine`, `POST /api/_workflow/cc/{id}/read`.

**`WorkFlowOptions`:**

```csharp
public class WorkFlowOptions
{
    public WithdrawPolicy WithdrawPolicy { get; set; } = WithdrawPolicy.BeforeFinalApproval;

    /// <summary>
    /// When the initiator is also the assigned approver on a node, auto-approve that node.
    /// DEFAULT = FALSE (conservative). Changing this to true silently bypasses an approval step,
    /// which may constitute a control bypass in compliance-sensitive environments.
    /// MUST be documented in CHANGELOG and requires explicit opt-in with a loud warning in startup logs.
    /// </summary>
    public bool InitiatorAutoApprove { get; set; } = false;   // v2: changed from true → false (red-line fix)

    public AutoApproveOnMissingHandlerPolicy AutoApproveOnMissingHandler { get; set; }
    public DelegationWindowMode DelegationWindowMode { get; set; } = DelegationWindowMode.AtAssignment;
    public ReturnResetMode ReturnResetMode { get; set; } = ReturnResetMode.Reset;
    public int MaxLevel { get; set; } = 5;
    public int MaxAddDepth { get; set; } = 3;
    public int MaxDelegationHops { get; set; } = 3;
    public int MaxReturnLoops { get; set; } = 3;
    public string? BusinessCalendarId { get; set; }
    public TimeSpan TimerPollInterval { get; set; } = TimeSpan.FromMinutes(1);
}
```

> **CHANGELOG callout required:** `InitiatorAutoApprove` defaults `false`. Any project that relied on the draft behavior where initiator == approver causes auto-skip must explicitly set `options.InitiatorAutoApprove = true`. Document this as an opt-in behavior change in `CHANGELOG.md` under the active version block.

---

## 10. Test Strategy

- **Framework:** MSTest + FluentAssertions + Moq (Etl.Test convention). **DB: SQLite shared-in-memory** — NEVER EF InMemory.
- **Coverage:** ≥70% V8 thresholds (repo bar); aim 80% on engine logic. TDD: write the race/edge matrix FIRST.

**10-mode behavior matrix** (one fixture class each): 串签 (sequential pointer, early-act 409, dedupe, manager-chain cap), 会签 (ALL, 比例 ceil boundary, impossible-threshold short-circuit, RejectGate Immediate/AfterAll), 或签 (any wins + sibling cancel, unanimous-reject fails, mixed reject/approve), 加签 (schema-present; behavior DEFERRED Wave 4 — test schema presence only), 委托 (schema-present; behavior DEFERRED Wave 4), 撤回 (L0/L1/L2 windows, irreversibility warning), 回退 (ReturnToInitiator restart+re-route MVP; Prev/Node deferred Wave 3 — test schema and discard-recount separately when unresolved races are resolved), 条件路由 (first-match determinism, fail-closed, null→default, off-whitelist throw), 抄送 (non-blocking pass-through, tenant check, no-approve), 超时 (schema-present; behavior DEFERRED Wave 5).

**Concurrency tests** (the make-or-break core):
- T-CONC-1 或签 approver-vs-approver: exactly one winner, loser `NoOp_AlreadyHandled`.
- T-CONC-2 撤回-vs-final-approve: first CAS wins, loser no-ops.
- T-CONC-3 会签 double-completion (§7.4): completes exactly once under N concurrent approvals.
- T-CONC-4 超时-vs-human: timeout no-ops when human already acted (Wave 5).
- **T-PROV-0 (Sprint-0 spike, build-failing gate):** CAS winner=1 / loser=0 proven on SQLite first; then extended to all 6 relational providers in Sprint-1.
- **T-PROV (ProviderConformance, nightly/release):** CAS winner=1 / loser=0 on **SqlServer, PgSql, MySql, SQLite, Oracle, DaMeng** — build fails if any no-ops the guard. Memory excluded (unsupported; startup guard tested separately).

**Invariant tests:** `HasQueryFilter` present on every entity (tenant-leak guard); `ProcessDefinitionVersion` has no `DoEdit` path + `ContentHash` matches `GraphJson`; routing evaluator rejects off-whitelist field + caps `In` at 100 (`AnalysisQueryEngine.Filters.cs:84`); canonical-JSON determinism; `DBTypeEnum.Memory` startup guard throws.

> CI note: **Any PR touching TokenService-adjacent transition logic or controllers MUST also run `WalkingTec.Mvvm.Mvc.Tests`** (the #119/#162 lesson). Read CI logs for `Test Run Successful` rather than trusting the Gitea conclusion (#11 caveat).

---

## 11. Migration / DbContext integration

### How `ApplyWorkFlowModels` is called (HIGH-1 fix)

**`ApplyWorkFlowModels` is called from the CONSUMER's own `DataContext.OnModelCreating`, NOT from the framework's `FrameworkContext`.** This is the exact pattern used by `ApplyEtlModels`:
- Definition: `ServiceCollectionExtensions.cs:141` (`public static ModelBuilder ApplyEtlModels(this ModelBuilder builder)`)
- Consumer call site: `demo/WalkingTec.Mvvm.Demo/DataContext.cs:349` (`modelBuilder.ApplyEtlModels();`)

`FrameworkContext` is a framework-internal context (`DataContext.cs:36`). The tenant + soft-delete query filters applied by `DataContext.OnModelCreating` (`DataContext.cs:162`) are auto-wired for `ITenant`/`IPersistPoco` types that derive from root types — this works correctly for WorkFlow entities because consumers inherit from `DataContext`.

### What `ApplyWorkFlowModels` declares

- `ToTable` names (`Wf_*` prefix for all 9 tables).
- Indexes per §3.
- FK relationships (`HasOne().WithMany()`, `OnDelete(Restrict)`).
- **Per-provider `RowVer` mapping** (§7.2: `IsRowVersion()` on SqlServer; `UseXminAsConcurrencyToken()` on PgSql; `uint` with `IsConcurrencyToken()` on SQLite/MySql/Oracle/DaMeng — selected by `Database.ProviderName` at model-build).
- Does NOT add `HasQueryFilter` — auto-applied by the consumer's `DataContext`.

### EF migration — consumer owns it

**`WalkingTec.Mvvm.WorkFlow` ships ZERO migrations** (exactly like `WalkingTec.Mvvm.Etl`). The migration is generated and owned by the consumer app:

```bash
# Run from the consumer application project directory
dotnet ef migrations add WorkFlow_InitialCreate \
  --context DataContext \
  --project <YourApp>/<YourApp>.csproj \
  --startup-project <YourApp>/<YourApp>.csproj
```

Replace `DataContext` with the consumer's actual DbContext class name (the one that calls `ApplyWorkFlowModels()` in `OnModelCreating`).

This creates 9 tables (`Wf_ProcessDefinition`, `Wf_ProcessDefinitionVersion`, `Wf_ProcessInstance`, `Wf_NodeInstance`, `Wf_ApprovalTask`, `Wf_WorkflowEventLog`, `Wf_CcRecord`, `Wf_DelegationRule`, `Wf_WorkflowTimer`). `DelegationRule`/`WorkflowTimer` ship in the Sprint-1 schema (zero migration churn when their waves land).

### `CHANGELOG.md` entry

New opt-in module entry under the active version block (append to `### Changes`):
- Consumers must: (1) add `modelBuilder.ApplyWorkFlowModels()` to their `DataContext.OnModelCreating`; (2) run `dotnet ef migrations add` against their own context; (3) call `AddWtmWorkFlow()` in `Startup`/`Program.cs`; (4) set `InitiatorAutoApprove = true` explicitly if the old default auto-approve behavior is required.
- Behavior-neutral when not wired (red line: new functionality is opt-in).

---

## 12. Sprint / Wave Plan

One Gitea issue per sub-issue; one branch/PR each; `Closes #N`. Waves sized so files **do not collide** → worktree-isolated Sonnet agents run in parallel. **Serialize anything touching `ServiceCollectionExtensions.cs` / `ApplyWorkFlowModels`** — those are the foundation sub-issues that land first. Single Mac-mini runner (~30-40 min/PR) → keep PRs small, 2-3 parallel max.

### Sprint-0 — Concurrency spike (Week 1, BLOCKING gate before any Wave-1 code)

> **This spike must pass before any engine implementation begins.** There is zero RowVer/ExecuteUpdateAsync precedent in the repo; the entire engine rests on extrapolating the TokenService CAS to 6 providers. Prove it first.

- `WF-0` Minimal test project + 1 entity with `uint RowVer` + `GuardedTransition` prototype. Assert CAS winner=1 / loser=0 on SQLite (in-process, fast). **Build fails if this assertion fails.** Once SQLite passes, extend the `[TestCategory("ProviderConformance")]` fixture to SqlServer, PgSql, MySql, Oracle, DaMeng (nightly). Document the per-provider mapping and any surprises. **The Wave-0 entity + migration work does NOT begin until T-PROV-0 passes on SQLite.**

### MVP (Weeks 2–7, ~6 weeks) — the correct spine only

Ships: token core + version-pin + 串签/会签/或签 + Exclusive 条件路由 + 抄送 + 撤回 + `ReturnToInitiator` + notifications. **Defers** 回退-to-node, 加签, 委托, 超时, designer to named waves. **Note:** the novel `ProcessDefinitionVersion` immutability is greenfield (no Etl analog) and carries additional test burden beyond "mirror Etl exactly".

**Wave 0 — Scaffold + version-pin foundation (Week 2, mostly serial)**
- `WF-1` Create `src/WalkingTec.Mvvm.WorkFlow` + test project; mirror Etl csproj/CPM; register in sln + core.slnf + ci.slnf. `IsPackable=true` in csproj but **NOT added to publish-nuget.yml yet** (§2.6 — deferred).
- `WF-2` Entities `ProcessDefinition`/`ProcessDefinitionVersion` (+ stubs `DelegationRule`/`WorkflowTimer`) as DIRECT `: PersistPoco/BasePoco, ITenant`; `ApplyWorkFlowModels` extension method (consumer calls from their DataContext); `WorkFlow_InitialCreate` migration guidance docs (not shipped as a migration file).
- `WF-3` Per-provider `RowVer` mapping in `ApplyWorkFlowModels` + `[TestCategory("ProviderConformance")]` CAS gate (builds on Sprint-0 proof).
- `WF-4` Publish flow: canonical `GraphJson` + SHA-256 `ContentHash` + immutable version INSERT + repoint head; `[BindNever]` on write-root; immutability + determinism invariant tests (GREENFIELD — extra care, no Etl analog).
- `WF-5` `AddWtmWorkFlow` DI skeleton + `WorkFlowOptions` with `InitiatorAutoApprove=false` default + startup guard for `DBTypeEnum.Memory`.

**Wave 1 — Runtime core + the three ApproveModes (Weeks 3-4, parallel after foundation)**
- `WF-6` Runtime entities `ProcessInstance`/`NodeInstance`/`ApprovalTask`/`WorkflowEventLog`/`CcRecord`; token/marking `WorkflowEngine.StartAsync` + `AdvanceAsync` (consume→route→mint, one transaction).
- `WF-7` `GuardedTransition` shared helper + `WorkflowActionResult` closed union; append-only `WorkflowEventLog` writer.
- `WF-8` 串签 `Sequential` completion policy + lazy `IApproverResolver` (role/user/ManagerChain, dedupe, caps).
- `WF-9` 会签 `All` + 比例 (ceil + impossible-threshold short-circuit) + `RejectGate`; completion-as-CAS (T-CONC-3).
- `WF-10` 或签 `Any` CAS winner + sibling cancel + unanimous-reject; concurrency matrix T-CONC-1/3.

**Wave 2 — Routing + CC + Withdraw + API + ship (Weeks 5-7, parallel)**
- `WF-11` `WhitelistRoutingEvaluator` (whitelist + closed `FilterOperator` + Expression-Tree + `In` cap 100 + cache-by-ContentHash) + `RoutingValidator`; Exclusive gateway in `AdvanceAsync`; fail-closed.
- `WF-12` 撤回 `WithdrawAsync` (L0/L1/L2 + instance CAS + irreversibility warning) + `ReturnToInitiator` (restart + re-route); T-CONC-2.
- `WF-13` 抄送 `CcRecord` non-blocking pass-through + tenant/permission check.
- `WF-14` Controllers (`ProcessDefinition`/publish/validate, `WorkflowInstance`/start/withdraw/timeline, `WorkflowTask`/inbox/approve/reject/return) via `WorkflowActionVM`; `FunctionPrivilege` gating.
- `WF-15` `AddWtmWorkFlowNotifications` + `IWorkflowNotifier` webhook cards; Admin grid for definitions; `docs/workflow.md`; CHANGELOG + version bump (incl. `InitiatorAutoApprove=false` migration note); `/wtm-release-check`; release **10.8.0** (or next minor).

### Increments (post-MVP)

**Wave 3 — Re-entry + parallel gateways (~2 wk)**
> Before starting: resolve the open race questions in §5.7 (span-deletion CAS story, Seq monotonicity, timer cancellation ordering, MaxReturnLoops accounting).
- `WF-16` 回退 `ReturnToPrev`/`ReturnToNode` with discard-and-recount projection (soft-delete span + re-materialize from template; drop `IsRuntimeInjected`); span-deletion guarded-transition; `MaxReturnLoops`.
- `WF-17` Inclusive 条件路由 + `Join` node + `Ack` node (blocking); orphan-token guards.

**Wave 4 — Runtime ops (~2 wk)**
> Before starting: resolve 加签-onto-会签 recompute CAS race and 委托 human-dedupe with TotalRequired questions (§5.4, §5.5).
- `WF-18` 加签 前/后 (IsRuntimeInjected, recompute thresholds, MaxAddDepth, drop-on-return).
- `WF-19` 委托/转交 (DelegationRule activation, `DelegationWindowMode`, hop cap + cycle detect + dedupe).

**Wave 5 — Timeout (durable scheduler) (~2-3 wk)**
> Before starting: resolve AutoApprove side-effect ordering question (§5.10).
- `WF-20` `AddWtmWorkFlowTimers` + `WorkflowTimerHostedService` (modeled on `EtlHostedService`, idempotent keys, business-calendar, cancel-on-every-exit).
- `WF-21` `ITimeoutAction` registry: Remind/AutoApprove/AutoReject/Escalate; timeout-vs-human CAS (T-CONC-4); per-mode timer semantics.

**Wave 6 — Low-code designer (~4-6 wk, additive)**
- `WF-22` LayUI/Vue drag-drop canvas emitting validated `GraphJson` → existing `/publish` + `/validate`; FilterCondition builder (reuses Analysis filter UI); palette from node-kind metadata; version-diff via `ContentHash`. No engine change.
