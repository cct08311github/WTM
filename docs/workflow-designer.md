# Workflow Designer

The `WalkingTec.Mvvm.WorkFlow` package ships an opt-in, low-code visual workflow designer
(`/_workflow-designer`) for authoring and publishing `ProcessDefinition` graphs without
writing raw JSON.

---

## Prerequisites

### 1. Register the designer

In your `Program.cs` (or `Startup.cs`), add the designer after the core workflow registration:

```csharp
builder.Services.AddWtmWorkFlow(opts => { /* ... */ });
builder.Services.AddWtmWorkFlowDesigner();   // opt-in; zero effect if omitted
```

And in the middleware pipeline, after `UseWtmContext`:

```csharp
app.UseWtmContext();
app.UseWtmWorkFlowDesigner();   // registers /_workflow_designer/assets/* static-file path
```

### 2. Consumer wwwroot prerequisites

The designer page loads LayUI and jQuery from your application's `wwwroot`:

```
wwwroot/
  jquery.min.js
  layui/
    css/layui.css
    layui.js
```

These paths are identical to the WTM admin shell requirements and are NOT served by the
framework itself. Ensure they exist before navigating to the designer.

### 3. RBAC — FunctionPrivilege registration

The designer page and every API endpoint are guarded by URL-RBAC (PrivilegeFilter).
Register the two privilege constants in your admin role setup:

```csharp
// WorkflowPrivileges.DesignerPage = "/_workflow-designer"
// WorkflowPrivileges.DesignerBase = "/api/_workflow/designer"
new FunctionPrivilege { MenuName = "工作流设计器", Url = WorkflowPrivileges.DesignerPage, ... },
new FunctionPrivilege { MenuName = "工作流设计器 API", Url = WorkflowPrivileges.DesignerBase, ... },
```

Only roles that hold both privileges can open the designer and call the mutating endpoints.
`[AllRights]` is intentionally absent — design/publish is a privileged operation.

### 4. Consumer migration note

`AddWtmWorkFlowDesigner()` registers the `ProcessDefinitionDraft` entity.
Run your EF Core migration to create the `Wf_DefinitionDraft` table:

```bash
dotnet ef migrations add AddDesignerDraft
dotnet ef database update
```

Schema: `DefinitionId` (FK → `ProcessDefinition`), `GraphJson` (text),
`BaseContentHash` (nullable), `RowVersion`, `LastSavedBy`, `LastSavedAt`.
One draft per `(TenantCode, DefinitionId)`.

#### 10.11.0 transport contract changes (FIX-B1)

If you have a custom client or HTTP proxy that calls the designer API:

| What changed | Before 10.11.0 | 10.11.0+ |
|---|---|---|
| Publish CAS guard header | `X-WTM-Expected-Hash` (wrong — was silently ignored) | `X-WTM-WF-Expected-Hash` |
| Draft base-hash | `?base=<hash>` query parameter | `X-WTM-WF-Base-Hash` request header |

The built-in JavaScript client (`framework_workflow_designer_core.js`) has been updated automatically.
Only custom clients or test harnesses that were calling these endpoints directly need to migrate.

#### 10.11.0 antiforgery scope changes (FIX-B3a / FIX-B3b)

`AddWtmWorkFlowDesigner()` no longer sets `AntiforgeryOptions.HeaderName` globally.
If your host had previously relied on the global `HeaderName` being `"X-WTM-WF-XSRF"`,
remove that dependency — the designer now handles its own antiforgery header extraction
without touching global options.

Two additional mutating endpoints now require the `X-WTM-WF-XSRF` antiforgery header:
- `POST /api/_workflow/designer/definitions` (`CreateDefinition`)
- `PUT /api/_workflow/designer/definitions/{code}` (`UpdateDefinitionMetadata`)

These were missing the antiforgery requirement before 10.11.0.

#### 10.11.0 `IProcessDefinitionPublisher` additive change (FIX-B3d)

`IProcessDefinitionPublisher.PublishRawAsync` was added in WF-21.1 as a Default Interface Method (DIM).
Third-party implementations compiled against 10.10 continue to load without modification.
The default implementation throws `NotSupportedException` at call time if not overridden;
this provides a clear error message rather than a load-time failure.

To support the raw-path publish in your custom implementation, add:

```csharp
public Task<PublishResult> PublishRawAsync(
    string definitionCode,
    string rawGraphJson,
    string? publishedBy,
    string? expectedBaseContentHash,
    CancellationToken cancellationToken = default)
{
    // Implement raw JSON → canonical → validate → publish pipeline here.
    // See ProcessDefinitionPublisher (the built-in implementation) for reference.
    throw new NotImplementedException();
}
```

---

## Opening the designer

Navigate to `/_workflow-designer` (optionally with `?code=<definitionCode>` to open a specific
definition directly). The page is a static HTML file served from the WorkFlow assembly's
embedded resources — no Razor views or MVC controllers are required.

The designer loads bootstrap configuration (antiforgery token, current user, options) from
`GET /api/_workflow/designer/bootstrap`. No configuration is baked into the HTML.

---

## Authoring a workflow

### Step 1: Create a definition head

Click **新建流程** in the definition list, enter a Code (pattern `^[A-Za-z0-9_\-\.]{1,64}$`),
Name, and optional Category. The head is created but unpublished (no version yet).

### Step 2: Edit nodes and transitions

Select the definition to open the edit panel. Three views are available:

| Tab | Description |
|-----|-------------|
| **表单视图** | Per-node property panels (9 node kinds covered); transitions table with per-edge condition editor; field whitelist editor. All inputs via DOM elements — no eval, no innerHTML. |
| **源码视图** | Raw JSON textarea with syntax validation and pretty-print. Full escape hatch for complex payloads not covered by the form panels. |
| **图形视图** | Read-only SVG auto-layout diagram (BFS rank-from-Start). Updates on tab switch. |

### Node kinds

| Kind | 说明 |
|------|------|
| `Start` | 流程起点 (one per graph) |
| `End` | 流程终点 |
| `Approval` | 审批节点 (串签/会签/或签) |
| `Condition` | 条件网关 (exclusive routing) |
| `ParallelGateway` | 并行网关 (split) |
| `InclusiveGateway` | 包容网关 (inclusive split) |
| `Join` | 汇聚节点 (multi-token merge) |
| `Cc` | 抄送节点 |
| `Ack` | 确认节点 |

### Step 3: Save as draft

The **保存草稿** button calls `PUT /api/_workflow/designer/definitions/{code}/draft` with
If-Match / If-None-Match concurrency control. Drafts are stored server-side (one per definition
per tenant). localStorage provides a best-effort crash-recovery cache labeled **本机备份** —
it is NOT the durability story.

### Step 4: Validate

Click **校验** to run `POST /api/_workflow/designer/validate`. Validation errors include an
optional `nodeKey` to focus the offending node. The form module also shows advisory lint badges
(dangling transitions, missing approver rules, unreachable nodes) in real time.

### Step 5: Publish

Click **发布** → confirm dialog shows `vN → vN+1`. If the draft was last saved by someone
other than the current actor, the confirm dialog attributes the edit. Server-side:

- Byte-identical content → `IdempotentNoOp` (no new version; toast confirms).
- New content → `Published` (new immutable `ProcessDefinitionVersion` created; draft deleted in-transaction).
- Concurrent publish by another user → `409 BaseVersionChanged` → conflict panel shown; reload and re-apply via source mode.

---

## Fidelity contract (raw-path LTGT decision)

The designer transmits graph documents on a **raw body** path (`Content-Type: application/json`,
`Request.Body` read directly) that **bypasses**:

1. The typed MVC binding pipeline (which would drop unknown fields via STJ's default `Skip` behavior).
2. The `StringIgnoreLTGTConverter` (which strips `<` / `>` from all string values on the typed path).

**Why**: round-trip fidelity requires unknown fields and exact number literals to survive a
save → reload cycle unchanged. A no-op save must produce the same `ContentHash` as the stored
version (`IdempotentNoOp`).

**Compensating controls** (the LTGT bypass is deliberately scoped):
- GraphJson is **never rendered as HTML** anywhere the designer or the notifier touch it; only
  `textContent` / SVG `<text>` element content is used (T-DSN-9 / WF-21.7 jest assertions).
- The webhook notifier escapes all graph-authored strings at the sink (WF-21.0, PR #296).
- Validator charset whitelist (`^[A-Za-z0-9_\-\.]{1,64}$`) blocks injection-bearing nodeKeys
  at publish time.
- Head metadata (Code, Name, Category) continues to flow through the typed binding path and
  retains the LTGT defense.

**Typed-path asymmetry** (documented, test-pinned by T-DSN-13):
- `<b>ok</b>` in a string field: survives raw publish byte-exact; arrives stripped on the
  shipped typed `POST /api/_workflow/definitions/{code}/publish` endpoint.
- `"approvePercent":"0.5"`: accepted by the typed path (`AllowReadingFromString`); rejected
  by the raw path (strict `_baseOptions`).
- Both behaviors are intentional and load-bearing; do not "fix" one without a CHANGELOG entry.

---

## Version history

Select a definition and click **版本历程** to open the version-history panel:

- VersionNo, ContentHash prefix (8 chars), SchemaVersion, PublishedAt, PublishedBy, IsCurrent.
- **载入为草稿**: copies the selected published version's GraphJson into the draft buffer.
  Subsequent publish creates `VersionNo = max+1` (rollback-by-republish). Version rows are
  **never** mutated or deleted.
- Version-to-version diff UI is deferred (cheap later on canonical JSON).

---

## `schemaVersion` gate

The designer rejects draft/publish operations for `schemaVersion != 1` with HTTP 400
(`SchemaVersionUnsupported`). On the client side, the form panel shows a locked banner:

> 此流程图使用较新的 schema 版本，表单编辑已停用

Source view and SVG view remain available in read-only mode.

The shipped typed `POST /api/_workflow/definitions/{code}/publish` endpoint is unaffected
(behavior pinned by T-DSN-13).

---

## Security notes

- **No eval / new Function** in any designer JS module (T-DSN-9, jest-enforced).
- **No CDN / network assets** — all JS/CSS served from the WorkFlow assembly's embedded
  resources or the consumer's `wwwroot`. No `'unsafe-eval'` in the CSP directive.
- **Antiforgery**: mutating endpoints (`PUT`, `POST`, `DELETE`) validate a per-session XSRF
  token from the `X-WTM-WF-XSRF` header (issued by `GET bootstrap`). This is the framework's
  first antiforgery use; zero existing surfaces are changed.
- **Actor is always server-set**: `PublishedBy` / `LastSavedBy` always comes from
  `Wtm.LoginUserInfo.ITCode`. Client DTO fields for these are marked `[BindNever]`.

---

## Related documentation

- [Workflow engine](/workflow) — authoring guide, graph schema, engine behavior
- [Analysis Mode](/analysis-mode) — field whitelist pattern reused by condition routing
- [Production Readiness](/production-readiness) — security checklist
