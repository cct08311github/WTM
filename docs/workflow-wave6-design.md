# Wave-6 Design Addendum — 低代码设计器 (WF-21): Visual Workflow Designer — Final Synthesized Spec

> Status: APPROVED FOR IMPLEMENTATION. Synthesized 2026-06 from two adversarially-generated proposals (A: form-first fidelity-first, B: hand-rolled SVG node-link canvas) + skeptic adjudication. Every load-bearing claim verified against `origin/dotnet10` @6cb6fb9cd (v10.11.0): `Definition/WorkflowGraphSerializer.cs`, `Definition/ProcessDefinitionPublisher.cs`, `Definition/WorkflowGraphValidator.cs`, `Controllers/WorkflowDefinitionController.cs`, `Notifications/WebhookWorkflowNotifier.cs`, `Models/ProcessDefinition*.cs`, Mvc `_DashboardDesignerController.cs` / `framework_dashboard_designer.js` (PR #238 precedent), `Helper/MvcOptionExtension.cs` (`StringIgnoreLTGTConverter`), `Helper/FrameworkServiceExtension.cs:1130-1140` (`UseWtmStaticFiles`).
> All new behavior is opt-in behind `AddWtmWorkFlowDesigner()` + `UseWtmWorkFlowDesigner()`. A host that never calls them is byte-identical to v10.11.0 — except the WF-21.0 security prerequisite (notifier escape-at-sink + validator nodeKey checks), which is a deliberate, CHANGELOG'd fail-closed fix for a publishable-today injection vector.
> Maintainer-only document (race analysis + injection constructions). Excluded from the GitHub mirror via `.sync/github-excludes.txt`, same as the Wave-3/4/5 addenda.

---

## 0. Synthesis verdicts (contested points)

| Point | Verdict | Why |
|---|---|---|
| Editor surface (R1) | **A: form-first + read-only auto-layout SVG view; editable drag-drop canvas DEFERRED** | Skeptic arithmetic broke B's scope 2–3x (Drawflow-class canvas ≈ 2,500 LOC alone; #238 needed 1,087 LOC for a grid with no edge concept; jsdom cannot test drag/hit-testing, forcing Playwright on the single flaky runner). The server contract below is exactly what a canvas wave sits on later — nothing is wasted. Zero new vendored libs either way (no eval-free, license-clean, vendorable node-link lib exists in or out of the repo today). |
| Fidelity keystone (R3) | **A's raw-bytes server path (Canonicalize seam + PublishRawAsync persisting canonical raw bytes) + skeptic-forced client-side lossless-number JSON codec + no-edit byte passthrough** | The JS `JSON.parse` int64/lexical break (`9007199254740993` → `…992`, `0.50` → `0.5`, `1E2` → `100`) broke BOTH proposals' "preserved verbatim" claims on any dirty save. Fix: the client never converts an untouched number token to a JS double — a hand-rolled raw-token-preserving codec (§2.3, one of the skeptic's two endorsed fix directions) keeps every number it did not edit as its original literal. Untouched unknown fields of every type survive byte-exact through a dirty save. |
| `[JsonExtensionData]` retrofit of the 9 schema POCOs (B) | **REJECTED this wave** | Skeptic-verified red-line hit: it silently changes the SHIPPED typed publish endpoint for every existing NuGet consumer (IdempotentNoOp → spurious new version for clients whose payloads carried previously-dropped fields; unknown string fields would bypass `StringIgnoreLTGTConverter` while declared strings stay stripped). Fidelity is achieved entirely on the NEW raw path; the typed path's drop behavior is pinned by contrast test T-DSN-13 and documented. Retrofit stays available as a later, CHANGELOG'd, deliberate change. |
| Draft storage (R2) | **B: one server table `ProcessDefinitionDraft` + RowVersion concurrency (If-Match transport); A's localStorage REJECTED as primary** | Skeptic break on A: localStorage is a durability illusion on IT-managed intranet desktops (profile wipes, machine roaming) and invisible to a colleague taking over. B's internal contradiction (RowVersion cannot ride in a raw GraphJson body) resolved: concurrency token travels in the `If-Match` header. A's no-editable-canvas decision removes B's LayoutJson/`ProcessDefinitionLayout` entirely — the draft row stores GraphJson text only, one table, one migration note. |
| Publish concurrency | **Skeptic-forced server-side CAS**: publish carries `expectedBaseContentHash`; the publisher compares it against `head.CurrentVersion.ContentHash` INSIDE the existing transaction; mismatch → 409 `BaseVersionChanged` | A's client-side hash comparison was a TOCTOU and advisory-only — the server would happily mint v6 from a v4-based edit, silently superseding v5. ~5 lines inside the verified `ProcessDefinitionPublisher` transaction make the guarantee real. Byte-identical content still short-circuits to `IdempotentNoOp` regardless of the expected hash (no false conflicts on no-op saves). |
| nodeKey injection (cross-cutting, both proposals broke) | **WF-21.0 prerequisite, lands first**: (a) escape-at-sink in `WebhookWorkflowNotifier` (verified interpolation of graph-authored `NodeKey` into card markdown at lines 60/81/110/190/212/242/273); (b) additive validator checks `InvalidNodeKey` (charset whitelist) + `DuplicateNodeKey` | Markdown-link injection (`[重新登入](http://evil)`) needs NO angle brackets and is publishable TODAY through the shipped typed endpoint — P0 per repo decision priorities. Escape-at-sink protects already-published graphs; the validator check fail-closes new publishes. Publish-time-only, so in-flight instances keep running (WF-20 set the "validated for the first time, NEW graphs only" precedent). |
| schemaVersion | **Designer endpoints fail-closed: reject `schemaVersion != 1` with a closed error. Shipped typed validate/publish behavior UNCHANGED this wave (pinned by test); global validator check filed as a follow-up issue** | New surface ⇒ no compatibility constraint ⇒ fail-closed is free. Changing the shipped endpoint is a behavior change that deserves its own CHANGELOG'd issue, not a smuggled side effect. Designer UI: `schemaVersion != 1` documents load in locked read-only mode (source view + SVG view only). |
| Anti-forgery | **Designer-scoped ASP.NET `IAntiforgery` (cookie + `X-WTM-WF-XSRF` header double-submit)** — first antiforgery use in the repo, scoped so zero existing surfaces change | Repo-wide there is no antiforgery (verified); both proposals punted. The hard rule stands: designer mutating endpoints are cookie-authenticated JSON APIs and get real CSRF protection. Scoping to the designer controller keeps this compatible (existing dashboard/API surfaces untouched) while establishing the pattern cleanly. Token issued via `GET bootstrap`; validated by a designer-local action filter. |
| Page/API authorization | **WF-14 URL-RBAC (PrivilegeFilter + `WorkflowPrivileges` constants) for page + ALL designer endpoints; deliberately stricter than the dashboard `[AllRights]` precedent** | Publish/draft/create are privileged operations; `[AllRights]` would hand them to every logged-in user. The dashboard triple ([AllRights] + server-actor + finally-delete) maps here as: URL-RBAC (stricter), server-actor everywhere (`Wtm.LoginUserInfo` only, `[BindNever]` DTO discipline), transactional draft-delete-on-publish — plus antiforgery on top. |
| XSS tier | **A's DOM-node-only discipline — NO trusted-HTML-template + `_esc()` tier at all** | Skeptic-verified: #238's `_esc()` does not escape single quotes — one single-quoted attribute template away from stored XSS across ~750 LOC of forms. Eliminating the template tier eliminates the class. LayUI `layer.open` receives DOM nodes (`content: element`); `layer.msg`/`layer.alert` are jest-banned sinks for any server-derived string. |
| Condition branch ↔ transition sync (B break) | **Merge-not-regen**: branch edits update the matching `(from,to)` TransitionDef entries IN PLACE in the lossless tree; only add/remove the specific edges that changed; never rebuild the node's out-edge list | B's "auto-regenerate outgoing TransitionDefs" destroyed unknown fields and legal `condition` payloads on Condition out-edges (validator only *mandates* conditions on InclusiveGateway edges; it does not forbid them elsewhere). Specified as a hard client-model rule with a dedicated regression test (T-DSN-15). |
| Definition heads / dead grid actions | Ship head-create/list/metadata endpoints (no creation path exists today — publish 404s on unknown code; only tests seed heads). Repoint `ProcessDefinitionListVM`'s dead `_WfProcessDefinition` Details/Versions GridActions (controller verified nonexistent) at the designer page | Smallest gap between shipped WF-14/15 and a usable authoring loop; converts a shipped dead link into the feature. CHANGELOG entry (dead-link fix, not a behavior change). |
| #238 latent 404 (`framework_dashboard_designer.js` not in the Mvc `EmbeddedResource` list) | **Own issue, own one-line PR — NOT in WF-21**; WF-21 answers the bug class structurally (manifest-name test + TestServer 200 test on every designer asset) | Repo workflow rule: out-of-scope discovered work gets its own issue. The structural guard ships regardless. |

---

## 1. Architecture overview

Three layers, all owned by `WalkingTec.Mvvm.WorkFlow` (the designer is useless without the engine; Mvc must not grow optional-module weight):

- **Client** — three hand-written, dependency-free IIFE modules (each ≤ 800 LOC, #238 idiom; zh-CN UI strings inline, American-English code/comments):
  - `framework_workflow_designer_core.js` — lossless JSON codec (§2.3), graph model + dirty tracking + no-edit byte passthrough, API layer (tolerant of `NumberHandling.WriteAsString` responses), source mode (raw JSON textarea + validate/format), antiforgery header injection.
  - `framework_workflow_designer_forms.js` — LayUI shell; per-NodeKind property panels for all 9 kinds; transitions table with per-edge condition editor; fieldWhitelist editor; client lint badges. All panel content built as DOM nodes (`document.createElement` + `textContent` + `addEventListener`), handed to `layer.open type:1` as elements, never HTML strings.
  - `framework_workflow_designer_view.js` — read-only SVG renderer: BFS rank-from-Start layered auto-layout (pure function, deterministic, ordinal tie-breaks), `createElementNS` + `textContent` only, no `foreignObject`, numeric-only attribute composition. The future instance-progress overlay and editable canvas both build on this seam.
- **Server** — new `WorkflowDesignerController` (`api/_workflow/designer`, URL-RBAC) + `WorkflowDesignerPageController` (`/_workflow-designer`, avoids the legacy Elsa `/_Workflow` demo collision) + scoped `IWorkflowDefinitionStore` + additive `IProcessDefinitionPublisher.PublishRawAsync` + one new entity `ProcessDefinitionDraft`. Controllers never touch `IDataContext` (red line) — all DB work in the store/publisher services.
- **Fidelity layer** — stored GraphJson is the source of truth; the designer is a JSON patcher, not a model rebuilder. Raw-body transport end to end; the typed MVC binding pipeline (which drops unknown fields AND strips `<`/`>` via `StringIgnoreLTGTConverter`) is bypassed by construction on every designer graph payload (§2).

The page is a static embedded `designer.html` with **zero inline scripts** (stricter than #238's inline bootstrap; nonce-CSP-ready for epic #807 with no rework). Boot config (current user display name, antiforgery token, options, `?code=` echo) arrives via `URLSearchParams` + `GET api/_workflow/designer/bootstrap`.

---

## 2. Fidelity mechanism (R3 — the correctness keystone)

### 2.1 Verified ground truth

- All 9 schema POCOs in `Definition/WorkflowGraphSchema.cs` are sealed with fixed properties; zero `JsonExtensionData`/`UnmappedMemberHandling` in the project → STJ default `Skip` silently destroys unknown fields on `Deserialize`.
- `[FromBody] WorkflowGraph` binding additionally routes through `StringIgnoreLTGTConverter` (Read strips `<`/`>` from every inbound string; Write is pass-through).
- `WorkflowGraphSerializer.WriteCanonical` is a pure `JsonElement` walker: ordinal-sorted keys at every depth, arrays in authored order, **numbers re-emitted via `WriteRawValue(GetRawText())` (exact literal preserved)**, strings re-emitted via `WriteStringValue` (escape forms normalized — so client-side string escape normalization is harmless: equal string VALUES canonicalize to equal bytes).

### 2.2 Server side

- `WorkflowGraphSerializer.Canonicalize(string json)` — additive public seam: `JsonDocument.Parse` + existing `WriteCanonical`. ~10 lines; `Serialize`/`Deserialize` untouched. Canonicalization without the typed model: unknown fields and exact number literals survive.
- `IProcessDefinitionPublisher.PublishRawAsync(string code, string rawGraphJson, string? publishedBy, string? expectedBaseContentHash, CancellationToken ct)` — pipeline:
  1. `JsonDocument.Parse` (controller already gated size/content-type/object-root) → `Canonicalize` → `WorkflowGraphHasher.ComputeHash`.
  2. Typed `Deserialize` of the SAME document **for validation only** (`WorkflowGraphValidator.Validate(graph, options)` reads known fields only; fail-closed). Designer path additionally rejects `graph.SchemaVersion != 1` → closed error (§0 verdict).
  3. Existing transaction shape (verified `ProcessDefinitionPublisher`): load head by Code (tenant filter auto-applied) → if `CurrentVersion.ContentHash == newHash` → `IdempotentNoOp` (regardless of `expectedBaseContentHash`) → else **CAS**: `expectedBaseContentHash` must equal `CurrentVersion.ContentHash` (or be null when `CurrentVersionId == null`, i.e. first publish) → mismatch → `PublishOutcome.BaseVersionChanged` (HTTP 409) → else INSERT immutable `ProcessDefinitionVersion` (`VersionNo = max+1`, GraphJson = **the canonicalized RAW bytes**, ContentHash, `SchemaVersion` copied verbatim from the document, PublishedAt/By, TenantCode from head) + repoint `head.CurrentVersionId`. Draft row for this definition deleted in the SAME transaction.
- Existing typed `PublishAsync` byte-for-byte untouched (compatibility red line). `PublishOutcome` gains the appended member `BaseVersionChanged` (closed-union discipline, append-only).
- Raw-body designer endpoints take NO typed `[FromBody]` parameter — they read `HttpContext.Request.Body` (cap `WorkFlowOptions.Designer.MaxGraphBytes`, default 1 MiB; `Content-Type: application/json` enforced; malformed JSON / non-object root → 400 closed error). This bypasses both unknown-field dropping AND the LTGT strip — the only two designer payloads that skip the strip are graph documents, which are never rendered as HTML anywhere the designer or (post WF-21.0) the notifier control.

### 2.3 Client side — lossless-number JSON codec (`WtmJsonRaw`, inside `_core.js`)

Hand-rolled JSON tokenizer/emitter (~250 LOC, pure, eval-free, jest-fixtured):

- `parse(text)` → tree of plain objects (insertion order kept — irrelevant, server canonicalizes), arrays, native strings/booleans/nulls, and **`RawNum` class instances wrapping the exact number literal** (`new RawNum("9007199254740993")`). `RawNum` is a class — `instanceof` checked, unforgeable by JSON data (parse only mints it for number tokens; a data object `{"__raw":…}` stays a plain object).
- `stringify(tree)` → emits `RawNum.raw` verbatim; strings via `JSON.stringify` (escape normalization is canonicalizer-safe per §2.1); objects/arrays recursively.
- Form binding helpers: `readNum(rawNum)` → `Number(raw)` for display; `writeNum(obj, key, userText)` → validate against the JSON number grammar → store `new RawNum(userText)`. Only fields the USER edits get new literals.

### 2.4 The four save/publish rules

1. **Not dirty → the payload IS the originally loaded byte string** (kept verbatim alongside the parsed tree). Unchanged graph ⇒ byte-identical canonical bytes ⇒ same ContentHash ⇒ `IdempotentNoOp`. T-DSN-1 is mandatory.
2. **Dirty → `WtmJsonRaw.stringify(tree)`**: untouched numbers keep raw literals; untouched unknown fields of every type ride along untouched; edited known fields carry the user's input. A new version was being authored anyway — and unlike both original proposals, nothing untouched is corrupted.
3. **Condition branch sync is merge-not-regen** (§0 verdict): edits to `branches[]`/`default` locate the matching TransitionDef by `(from,to)` and mutate only `to`-retargets/additions/removals that the user actually performed; unknown fields and `condition` payloads on surviving edges are untouched.
4. **`schemaVersion != 1`** → form mode locked with banner 「此流程图使用较新的 schema 版本，表单编辑已停用」; source view + SVG view stay available; designer publish refuses (server enforces too).

Residual (documented, not a gap): on a dirty save, string escape FORMS may normalize (`é` → `é`) — the server canonicalizer already normalizes string escapes identically for both old and new versions, so this can never produce a spurious hash change for equal values.

---

## 3. Server surface (R2)

### 3.1 Endpoints — `Controllers/WorkflowDesignerController.cs`, `[ApiController][Route("api/_workflow/definitions-designer" → "api/_workflow/designer")] : BaseController`, NO `[AllRights]` (PrivilegeFilter URL-RBAC on every action)

| Endpoint | Binding | Notes |
|---|---|---|
| `GET bootstrap` | — | Antiforgery token (`IAntiforgery.GetAndStoreTokens`), current user display, designer options echo. The static page's only config source. |
| `GET definitions` | typed | Paged head list: Code/Name/Category/IsEnabled/CurrentVersionNo/has-draft flag. Tenant filter auto-applied. |
| `POST definitions` | typed DTO | Create head `{code,name,category}`; code regex `^[A-Za-z0-9_\-\.]{1,64}$`; duplicate-in-tenant → 409; tenant/CreateBy server-set; `[BindNever]` on echo fields. Head fields keep the typed-binding LTGT defense (they ARE rendered in UI). Closes the "publish 404s on unknown code" gap. |
| `PUT definitions/{code}` | typed DTO | Head metadata (Name/Category/IsEnabled) via narrow `UpdateProperty`. |
| `GET definitions/{code}/graph` | — | Envelope `{graphJson (verbatim stored string), versionId, versionNo, contentHash, schemaVersion, publishedAt, publishedBy, draft? {graphJson, rowVer, lastSavedBy, lastSavedAt, baseContentHash}}`. GraphJson is a JSON string property — string escaping is byte-faithful on `JSON.parse`; never re-serialized through the typed model. Client tolerates stringly numbers (`NumberHandling.WriteAsString`). |
| `GET definitions/{code}/versions` | — | VersionNo/ContentHash/SchemaVersion/PublishedAt/PublishedBy/IsCurrent history. |
| `GET versions/{id}/graph` | — | Verbatim GraphJson of one immutable version (read-only view; tenant-scoped → cross-tenant id behaves as 404). |
| `PUT definitions/{code}/draft` | **raw body** | Body = GraphJson text. `If-None-Match: *` → create (optional `?base=<contentHash>` records the version the edit started from); `If-Match: "<rowVer>"` → update; stale/missing → 409 (NO create-on-missing under If-Match — kills the post-publish resurrection race, §3.2). Size cap, well-formed-JSON gate. Antiforgery validated. |
| `GET definitions/{code}/draft` / `DELETE …/draft` | — / — | Load / explicit discard (DELETE antiforgery-validated). |
| `POST definitions/{code}/publish` | **raw body** | Body = current edit buffer (rule §2.4-1/2). `X-WTM-Expected-Hash` header = `expectedBaseContentHash`. → `PublishRawAsync`. 200 (`Published` / `IdempotentNoOp`), 400 (`ValidationFailed` / `SchemaVersionUnsupported` / malformed), 404 (`DefinitionNotFound`), 409 (`BaseVersionChanged`). Antiforgery validated. |
| `POST validate` | **raw body** | Parse → typed model → `WorkflowGraphValidator.Validate(graph, _options)` (options overload — 14g `AllowTimerAutoAction` gate stays enforced). Never writes. Response includes the closed error code + message + best-effort `nodeKey` (additive optional field on the response DTO). |

Shipped `WorkflowDefinitionController` (`POST {code}/publish`, `POST validate`) is untouched.

### 3.2 Draft semantics — `Models/ProcessDefinitionDraft.cs`

- **DIRECT** `: PersistPoco, ITenant` (red line — `DataContext` applies `HasQueryFilter` to root types only). Columns: `DefinitionId` (FK head), `GraphJson` (text), `BaseContentHash` (nullable), `RowVersion` (WF-2 RowVer infra), `LastSavedBy`, `LastSavedAt`. One draft per `(TenantCode, DefinitionId)` (unique index). Registered in `ApplyWorkFlowModels` → **consumer migration note in CHANGELOG** (same discipline as #236 / Wave-1).
- Engine never reads drafts. No designer code path can UPDATE or DELETE a `ProcessDefinitionVersion` row — `IWorkflowDefinitionStore`'s public surface has no such member (reflection-asserted, T-DSN-14; preserves the PR #272 immutability finding structurally).
- **Skeptic holes closed**: (1) RowVersion travels in `If-Match`, not the raw body (B's contradiction). (2) Shared-draft attribution: the publish confirm dialog surfaces `LastSavedBy/LastSavedAt` when they differ from the current actor; the publish event-log payload records `draftLastSavedBy` alongside the server-set `PublishedBy` (dual-audit discipline). (3) Resurrection: publish deletes the draft in-transaction; a stale editor's later `PUT` carries the old `If-Match` → row gone → 409 → client prompts reload of the new current version. localStorage is demoted to a best-effort crash-recovery cache only (clearly labeled 本機備份), never the durability story.

---

## 4. Security envelope (R4)

- **WF-21.0 upstream fixes (prerequisite, own PR)**:
  - `WebhookWorkflowNotifier`: private `EscapeMd(string)` applied to every graph-/user-authored interpolation in card bodies (NodeKey, actor/assignee codes) — escapes `` ` `` `*` `_` `[` `]` `(` `)` `<` `>` and strips CR/LF. Protects ALREADY-PUBLISHED graphs (escape-at-sink); identifiers-only cards stay identifiers-only.
  - Validator additive checks (publish-time, new graphs only — WF-20 precedent): `InvalidNodeKey` (nodeKey must match `^[A-Za-z0-9_\-\.]{1,64}$`; transitions/branches/joinNodeKey reference nodeKeys, so node-level enforcement covers every rendered surface) + `DuplicateNodeKey` (today a duplicate-key graph passes with undefined engine behavior — verified the validator builds a HashSet with no uniqueness assert). CHANGELOG entries; in-flight instances unaffected.
- **RBAC**: page + every designer endpoint behind PrivilegeFilter URL gates; new `WorkflowPrivileges.DesignerBase = "/api/_workflow/designer"` and `DesignerPage = "/_workflow-designer"` constants for consumer FunctionPrivilege registration (admin-only by configuration). Actor/tenant always server-side (`Wtm.LoginUserInfo.ITCode` / tenant query filter); `[BindNever]` on every server-set DTO field.
- **Anti-forgery**: `AddWtmWorkFlowDesigner()` calls `services.AddAntiforgery(o => o.HeaderName = "X-WTM-WF-XSRF")`; `GET bootstrap` issues the token; a designer-local action filter runs `IAntiforgery.ValidateRequestAsync` on every mutating action (PUT/POST/DELETE). Scoped to the designer controller — zero change to any existing surface. First antiforgery use in the repo, documented as the pattern for future cookie-authenticated JSON write surfaces.
- **LTGT bypass is deliberate and scoped**: only raw graph-document endpoints skip the strip (fidelity demands it — a byte-faithful client cannot round-trip stored angle brackets through the typed pipeline). Compensating controls: GraphJson is rendered exclusively via `textContent`/SVG-`<text>` in the designer; the notifier sink escapes (WF-21.0); responses are `application/json`; head metadata keeps the typed LTGT defense.
- **Client discipline (jest-enforced, extends `dashboard-designer.test.js`)**: zero inline `<script>` in `designer.html`; all dynamic DOM via `createElement`/`createElementNS` + `textContent` + `addEventListener`; `layer.open` receives DOM nodes only; zero `eval` / `new Function` / string-`setTimeout` / `innerHTML` / `insertAdjacentHTML` / `outerHTML` / inline `on*` across all three modules (source assertions); `layer.msg`/`layer.alert` never receive server-derived strings (validator ErrorMessages embed raw nodeKeys — they render via `textContent` in the error strip only); SVG `d`/transform attributes composed from numbers only; no `foreignObject`.
- **CSP**: satisfies the shipped default `script-src 'self' 'unsafe-inline'` without `'unsafe-eval'` (`WtmCspOptions.cs:29-42`) trivially — no third-party JS at all — and is already compatible with future nonce mode (no inline scripts anywhere).

---

## 5. Validation & preview UX (R5)

- **Server authority**: existing-shape `POST validate` (raw body → typed model → options overload) debounced on structural edits + explicit 校验 button; publish re-validates fail-closed server-side regardless. First-error-only is accepted for MVP (verified `GraphValidationResult` is single-error by contract); the response DTO's additive optional `nodeKey` lets the UI focus the offending node/form; a `ValidateAll` overload is deferred.
- **Client lint** (advisory, instant, from B): dangling transition from/to, Approval without approverRule, Condition without default, unreachable-from-Start BFS, gateway `joinNodeKey` not a Join, InclusiveGateway out-edge without condition — badges in the node list + problem strip, DOM-method-built. Lint never blocks save; publish always routes through server validate.
- **Preview** = the read-only SVG view of the CURRENT edit buffer, computed entirely client-side. Zero server artifacts ⇒ the dashboard's create-then-finally-delete dance is moot by construction; the transactional draft-delete-on-publish is the only cleanup obligation. Path/token simulation deferred (needs an engine dry-run seam; faking routing client-side would contradict sandboxed-evaluator-as-only-truth).

---

## 6. Node-type form coverage (R6)

Full schemaVersion-1 coverage ships (forms are cheap once the canvas is cut; source mode is the universal escape hatch — the cut line sits inside polish, not coverage). Forms expose exactly `Definition/WorkflowGraphSchema.cs`, nothing invented:

- **Start / End / Join**: key (charset-validated live, mirroring WF-21.0) + name.
- **Approval**: approveMode Sequential(串签)/All(会签)/Any(或签); approvePercent `(0,1]` (All only); rejectGate Immediate/AfterAll (All only); rejectPolicy TerminateInstance/ReturnToPrev/ReturnToNode (node picker — server validator authoritative via `GetValidReturnTargets`)/ReturnToInitiator; ApproverRuleDef string-typed Type User/Role/ManagerChain+MaxLevel/Initiator(labeled Wave-4) with typed value inputs; inline CC list (CcTrigger + ApproverRuleDef); full TimeoutDef sub-form (friendly d/h/m composer emitting ISO-8601 `P2D`/`PT8H`, businessCalendar toggle, TimerAction, remindEveryHours, maxReminders, escalateTo — WF-20 fields).
- **Condition**: ordered branches (whitelist-field dropdown + closed FilterOperator dropdown + clrType-typed value input; In/NotIn tag input client-capped at 100) + mandatory default picker; transition sync per §2.4-3 (merge-not-regen).
- **ParallelGateway / InclusiveGateway**: joinNodeKey dropdown filtered to Join nodes; Inclusive out-edge condition edited on the edge in the transitions table (affordance enforced; validator enforces truth).
- **Ack**: ackMode All/Any/Quorum (+ quorum count) — the schema defines nothing else for Ack.
- **Cc node**: CcRuleDef + trigger.
- **Top-level**: fieldWhitelist editor (field, clrType dropdown of known CLR type names, allowedRoles tags) feeding every rule editor's field dropdown; transitions table (from/to pickers + per-edge condition editor).
- **Source-mode-only (not formized, preserved verbatim via §2)**: nested and/or RoutingRuleDef groups (Wave-3 schema; shown as read-only 「复杂条件」 summary, never flattened) and any unknown/future fields.

---

## 7. Versioning UX (R7)

- Definition list view (tenant-filtered, same shape as `ProcessDefinitionListVM`) with create-head dialog and enable/disable; per-definition version-history drawer (VersionNo, ContentHash prefix, SchemaVersion, PublishedAt/By, IsCurrent).
- Read-only view of any published version (forms locked + SVG + source view via `GET versions/{id}/graph`); 「载入为草稿」 copies an old version into the edit buffer — publish then creates `VersionNo = max+1` (rollback-by-republish). Version rows are never mutated or deleted, structurally (§3.2).
- Publish flow: validate → confirm dialog showing `vN → vN+1` (or 「内容未变化，将不会产生新版本」), draft attribution when `LastSavedBy != actor` → outcome toast distinguishing `Published` / `IdempotentNoOp` / 409 `BaseVersionChanged` (「该流程在你编辑期间已被他人发布新版本」 → offer reload + re-apply via source mode).
- `ProcessDefinitionListVM` dead GridActions (`_WfProcessDefinition` Details/Versions — controller verified nonexistent anywhere) repointed at `/_workflow-designer` (CHANGELOG: dead-link fix).
- Deferred: version-to-version diff (canonical JSON makes it cheap later); in-flight instance progress overlay (pure client join on the shipped `GET instances/{id}/timeline [AllRights]` + the SVG renderer — designed-for via stable nodeKey anchoring, not shipped).

---

## 8. Asset pipeline (R8)

- Assets are the WorkFlow assembly's FIRST embedded resources (csproj has zero today — verified): `designer/designer.html` (≤5 KB), `designer/framework_workflow_designer.css` (≤10 KB), `designer/framework_workflow_designer_core.js` / `_forms.js` / `_view.js`. Underscore-only file/dir names (`EmbeddedFileProvider` mangles hyphens in resource paths). csproj: `<EmbeddedResource Include="designer\**" />` wildcard — the #238 latent-404 class cannot recur by omission — double-guarded by T-DSN-16 (manifest names + TestServer 200 per referenced URL).
- Delivery: opt-in `UseWtmWorkFlowDesigner()` adds a second `StaticFileOptions { RequestPath = "/_workflow_designer/assets", FileProvider = new EmbeddedFileProvider(WorkFlow assembly) }`. `UseWtmStaticFiles` (Mvc assembly `/_js` provider, `FrameworkServiceExtension.cs:1130-1140`) untouched. ETag/maintenance/timeout allowlists: documented additive consumer opt-in — `WtmETagOptions` defaults are NOT mutated (no silent default change).
- `AddWtmWorkFlowDesigner()` registers `IWorkflowDefinitionStore`, antiforgery, and `WorkFlowOptions.Designer` — follows the `AddWtmWorkFlowTimers/Notifications` opt-in family; `AddWtmWorkFlow` alone changes nothing; no `BuildServiceProvider` anywhere (10.9.0 startup-crash lesson).
- layui + jquery load from the CONSUMER wwwroot exactly per the dashboard precedent (`/layui/...`, `/jquery.min.js`) — documented prerequisite; every missing-dependency hint references local vendored paths, never CDN (the `framework_analysis.js` jsdelivr hint is the anti-pattern; fixing it is a flagged one-line drive-by in its own issue).
- No bundler, no build step (verified none exists for framework JS): committed source is the served artifact. Budget: 3 JS modules ≤ 150 KB total raw (skeptic calibration: A's single-file 70 KB was 2–3x optimistic; #238's one module is 51 KB for a third of this form surface) + CSS + HTML ≈ ≤ 165 KB — still under #238's 192 KB because zero vendored libraries are added.
- Jest: `test/WalkingTec.Mvvm.Js.Tests/__tests__/workflow/workflow-designer*.test.js` loading modules from source via fs+vm with mocked layui/fetch (dashboard-designer.test.js pattern); added to `collectCoverageFrom`.

---

## 9. Server/asset deltas (file-scoped)

| File | Delta |
|---|---|
| `Notifications/WebhookWorkflowNotifier.cs` | WF-21.0: `EscapeMd` at every graph-/user-authored interpolation (lines 60/81/110/190/212/242/273 + Field rows where user-controlled). |
| `Definition/WorkflowGraphValidator.cs` + `Definition/GraphValidationError.cs` | WF-21.0: additive `InvalidNodeKey` (charset) + `DuplicateNodeKey` checks/codes. |
| `Definition/WorkflowGraphSerializer.cs` | Additive `public static string Canonicalize(string json)`; `Serialize`/`Deserialize` untouched. |
| `Definition/IProcessDefinitionPublisher.cs` + `ProcessDefinitionPublisher.cs` | Additive `PublishRawAsync(code, rawGraphJson, publishedBy, expectedBaseContentHash, ct)` (pipeline §2.2; CAS + draft-delete in-txn). `PublishOutcome` += `BaseVersionChanged` (append-only). |
| `Definition/IWorkflowDefinitionStore.cs` + `WorkflowDefinitionStore.cs` (new) | Scoped, `IDataContext`-injected: head create (tenant-scoped Code uniqueness), head metadata `UpdateProperty`, paged list + CurrentVersion join, version history, version GraphJson fetch, draft CRUD. NO `ProcessDefinitionVersion` update/delete member. |
| `Models/ProcessDefinitionDraft.cs` (new) + `ApplyWorkFlowModels` | Direct `: PersistPoco, ITenant`; unique `(TenantCode, DefinitionId)`; RowVersion. CHANGELOG migration note. |
| `Controllers/WorkflowDesignerController.cs` (new) | §3.1 endpoint table; raw-body actions read `Request.Body` (size cap, content-type gate); antiforgery filter on mutating actions; no `[AllRights]`. |
| `Controllers/WorkflowDesignerPageController.cs` (new) | `/_workflow-designer`; URL-RBAC; streams embedded `designer.html` (no Razor/Views infra in WorkFlow). |
| `Controllers/WorkflowPrivileges.cs` | `+ DesignerBase`, `+ DesignerPage` constants. |
| `WorkFlowOptions.cs` | Additive `Designer` sub-options (`MaxGraphBytes = 1_048_576`). No existing default changed. |
| `ServiceCollectionExtensions.cs` | Additive `AddWtmWorkFlowDesigner()` (store + antiforgery + options) / `UseWtmWorkFlowDesigner()` (static-file seam). |
| `ViewModels/ProcessDefinitionListVM.cs` | Repoint dead `_WfProcessDefinition` GridActions at the designer page. |
| `ViewModels/WorkflowDtos.cs` | Designer DTOs (`[BindNever]` discipline); `ValidateResponseDto` += optional `nodeKey`. |
| `WalkingTec.Mvvm.WorkFlow.csproj` | First `<EmbeddedResource Include="designer\**" />` ItemGroup. |
| `designer/designer.html`, `designer/framework_workflow_designer.css`, `designer/framework_workflow_designer_{core,forms,view}.js` (new) | §1/§8. Zero inline scripts; DOM-method-only; lossless codec in `_core`. |
| `test/WalkingTec.Mvvm.Js.Tests/__tests__/workflow/*` (new) | Codec/XSS/lint/form jest suites; `collectCoverageFrom` updated. |
| `docs/workflow-designer.md` (new) + `docs/workflow.md` | Authoring guide, RBAC + layui/jquery prerequisites, fidelity contract (raw-path LTGT decision, typed-vs-raw asymmetries); fix the §6 endpoint table (currently lists nonexistent GET list/versions endpoints + a `{graphJson:string}` body the controller never accepted). |
| SEPARATE ISSUES (not in WF-21 PRs) | (a) Mvc csproj missing `framework_dashboard_designer.js` EmbeddedResource → runtime 404 (verified); (b) `framework_analysis.js` CDN hint → vendored path; (c) global validator `SchemaVersionUnsupported` check (CHANGELOG'd behavior change); (d) antiforgery adoption guidance for other cookie-authenticated framework write surfaces. |

---

## 10. Test matrix (T-DSN; WorkFlow.Test = SQLite shared-in-memory; jest = jsdom fs+vm)

| ID | Scenario | Assert |
|---|---|---|
| T-DSN-1 | **RoundTripNoOp (mandatory ContentHash-stability row)**: seed published version whose canonical GraphJson contains an unknown field + literal `0.50`; republish the byte-identical document via the raw HTTP endpoint (TestServer) | `IdempotentNoOp`; no new `ProcessDefinitionVersion` row; stored bytes unchanged; proves full MVC-pipeline (incl. LTGT) bypass |
| T-DSN-2 | UnknownFieldPreservation: unknown object/array/string fields at graph/node/transition/rule depth; edit a DIFFERENT known field; publish | new version contains the unknown fields at ordinal-sorted positions; republish-unchanged → NoOp |
| T-DSN-3 | Int64/lexical raw preservation (jest + server): `{"xExternalRef":9007199254740993}`, `0.50`, `1E2` survive a DIRTY save of an unrelated edit byte-exact; `RawNum` unforgeable from data | codec emits original literals; server canonical bytes contain `9007199254740993` |
| T-DSN-4 | CanonicalizeEquivalence: spec §4 PurchaseApproval + one graph per NodeKind: `Canonicalize(raw) == Serialize(Deserialize(raw))` for known-field docs; key-scrambled input → byte-identical canonical output | number raw text preserved by `Canonicalize` |
| T-DSN-5 | PublishCas: two publishers from base v4; first mints v5; second (edited, expected=H4) → 409 `BaseVersionChanged`; second (byte-identical to v5) → `IdempotentNoOp`; concurrent same-body double-publish → one Published + one NoOp, no duplicate VersionNo | CAS inside txn; unique-index backstop |
| T-DSN-6 | DraftLifecycle: `If-None-Match:*` create; `If-Match` update; stale → 409; publish deletes draft in-txn; post-publish stale PUT → 409 (no resurrection); tenant A cannot touch tenant B's draft; size/malformed gates | RowVersion + HasQueryFilter isolation |
| T-DSN-7 | RawBoundary: body > MaxGraphBytes / malformed JSON / non-object root / wrong content-type / validation failure | closed-code 4xx; zero DB writes |
| T-DSN-8 | Rbac/AntiSpoof/Antiforgery: every endpoint + page → 403 without FunctionPrivilege URL; PublishedBy always session ITCode (body fields ignored); mutating call without `X-WTM-WF-XSRF` → 400; bootstrap token accepted | server-actor + CSRF gate |
| T-DSN-9 | XssEvalZero (jest): node name `<img src=x onerror=…>` / `' autofocus onfocus='…` inert via textContent in list/forms/SVG; module sources: 0× eval/new Function/innerHTML/insertAdjacentHTML/outerHTML/inline on*; `designer.html` zero inline scripts; layer.msg/alert never receive server strings | source + DOM assertions |
| T-DSN-10 | NotifierEscape (WF-21.0): nodeKey carrying markdown-link + HTML payloads renders escaped in every card body; plain identifiers byte-identical (regression) | escape-at-sink |
| T-DSN-11 | ValidatorNodeKey (WF-21.0): charset reject + duplicate reject with closed codes; previously-published exotic-key version still loads/executes (engine untouched) | publish-time-only fail-close |
| T-DSN-12 | SchemaVersionGate: designer publish/draft of `schemaVersion:2` → 400 closed code; typed legacy endpoint behavior pinned UNCHANGED; jest: form lockout, source+view available | asymmetry is deliberate and loud |
| T-DSN-13 | Typed-vs-raw asymmetry pins: `<b>` in a string survives raw publish byte-exact / arrives stripped via typed publish; `"approvePercent":"0.5"` accepted typed (`AllowReadingFromString`) / rejected raw (strict `_baseOptions`) | documented behavior, loud on change |
| T-DSN-14 | VersionImmutability: `GET versions/{id}/graph` verbatim (not head's); load-old + publish → `max+1`, old rows untouched; reflection: store has no version update/delete member | PR #272 invariant structural |
| T-DSN-15 | FormCoverage + branch-sync regression: all 9 NodeKinds authored via forms → server validate green (approvePercent bounds, In/NotIn 100 cap, Inclusive edge condition, ISO-8601 composer vs WF-20 checks); Condition branch edit preserves unknown fields + condition on surviving `(from,to)` edges (merge-not-regen) | B-break pinned |
| T-DSN-16 | EmbeddedAssets: manifest resource names contain every referenced asset; TestServer GET each `/_workflow_designer/assets` URL → 200 + content-type; WITHOUT `UseWtmWorkFlowDesigner` → 404 and `AddWtmWorkFlow`-only host boots unchanged | anti-#238-404 + opt-in default guard |
| T-DSN-17 | ClientLint (jest): the 6 lint classes badge correctly; lint never blocks save; publish routes through server validate; server `nodeKey` focuses the offending form | advisory-only contract |
| T-DSN-18 | E2eSmoke (Mac-mini flake SOP applies): admin opens designer → create head → form-author Start→Approval(串签)→End → 校验 → publish v1 → republish unchanged → `IdempotentNoOp` toast → version history shows v1 | full authoring loop |

---

## 11. Sub-issue scopes (single epic WF-21 under #240; sequenced, file-scoped, Wave-4/5 discipline)

1. **WF-21.0 — Security prerequisites (independent PR; P0; lands first).** `Notifications/WebhookWorkflowNotifier.cs` (EscapeMd at sink), `Definition/WorkflowGraphValidator.cs` + `GraphValidationError.cs` (`InvalidNodeKey`, `DuplicateNodeKey`), CHANGELOG. Tests: T-DSN-10/11. Side-files (own issues, not this PR): Mvc dashboard-designer-js 404 fix; `framework_analysis.js` CDN hint.
2. **WF-21.1 — Fidelity foundation (FIRST designer scope — unknown-field preservation before any UI exists).** `WorkflowGraphSerializer.Canonicalize`, `IProcessDefinitionPublisher.PublishRawAsync` + `PublishOutcome.BaseVersionChanged` (CAS, canonical-raw-bytes persistence, schemaVersion gate). Tests: T-DSN-1/2/4/5 + T-DSN-13 pins (raw half via direct publisher calls; HTTP halves re-run in 21.3).
3. **WF-21.2 — Definition catalog.** `IWorkflowDefinitionStore` + impl, `WorkflowDesignerController` read/create/metadata actions (GET definitions / POST definitions / PUT {code} / GET graph / GET versions / GET versions/{id}/graph), `WorkflowPrivileges` constants, DTOs. Tests: T-DSN-8 (RBAC half)/14 + 404/409 paths.
4. **WF-21.3 — Draft store + raw endpoints + antiforgery.** `ProcessDefinitionDraft` + `ApplyWorkFlowModels` (+ CHANGELOG migration note), draft GET/PUT/DELETE with If-Match rules, `POST publish` (raw + `X-WTM-Expected-Hash` + in-txn draft delete), `POST validate` (raw, +nodeKey), `GET bootstrap`, `AddWtmWorkFlowDesigner` antiforgery wiring, `WorkFlowOptions.Designer`. Tests: T-DSN-5 (HTTP)/6/7/8 (antiforgery half)/12/13 (HTTP).
5. **WF-21.4 — Page + asset pipeline.** `WorkflowDesignerPageController`, `designer/designer.html` + css shells, csproj EmbeddedResource wildcard, `UseWtmWorkFlowDesigner` static-file seam, ETag-opt-in doc note. Tests: T-DSN-16.
6. **WF-21.5 — Client core module.** `framework_workflow_designer_core.js`: `WtmJsonRaw` lossless codec, model + dirty tracking + byte-passthrough, API layer (WriteAsString-tolerant, XSRF header), source mode, localStorage crash-cache. Jest: T-DSN-3 (client half)/9 (core sources)/12 (lockout).
7. **WF-21.6 — Forms + lint module.** `framework_workflow_designer_forms.js`: 9 NodeKind panels, transitions table + edge condition editor, fieldWhitelist editor, Condition merge-sync, client lint. Jest: T-DSN-15/17/9 (forms sources).
8. **WF-21.7 — SVG view + versioning UX + docs + e2e.** `framework_workflow_designer_view.js` (deterministic auto-layout, read-only), version-history drawer + publish confirm/conflict flows, `ProcessDefinitionListVM` repoint, `docs/workflow-designer.md` + `docs/workflow.md` §6 correction, CHANGELOG, e2e smoke. Tests: T-DSN-18 + jest layout determinism + T-DSN-9 (view sources).

Dependencies: 21.0 independent; 21.1 → 21.2 → 21.3 sequential (server contract); 21.4 parallel after 21.3; 21.5 → 21.6 → 21.7 sequential (client layers). Each scope is one PR with its test rows green; WorkFlow.Test must stay green per PR (WF-wave discipline).

---

## 12. Deferred (explicit, with the unresolved question named)

- **Editable drag-drop canvas (Wave 6.1+)**: the only deliverable demanding either a vendored diagram lib (fresh eval/license/vendoring audit, 300 KB–1 MB) or a multi-week hand-rolled editor with un-jest-testable interaction surface. Builds directly on this server contract + the `_view.js` renderer. Unresolved: layout persistence (positions must stay OUT of GraphJson to keep ContentHash semantic — a designer-owned layout row keyed by VersionId was B's answer; revisit then).
- **In-flight instance progress overlay**: pure client join on shipped `GET instances/{id}/timeline [AllRights]`; needs marking→nodeKey coloring rules for parallel/inclusive tokens.
- **Path/token simulation**: needs an engine dry-run seam; client-side faking contradicts sandboxed-evaluator-as-only-truth.
- **`ValidateAll` multi-error overload**: validator is first-error-only by shipped contract; client lint covers the live-feedback UX meanwhile.
- **Version-to-version diff UI**: cheap later on canonical JSON.
- **`[JsonExtensionData]` retrofit of the typed publish path**: deliberate, CHANGELOG'd compatibility decision — unresolved: whether legacy clients' NoOp-flip is acceptable.
- **Global validator `SchemaVersionUnsupported` check**: behavior change to shipped validate/publish; own issue + CHANGELOG; pinned asymmetry (T-DSN-12) keeps it loud meanwhile.
- **Per-user drafts**: single shared draft fits the few-admins intranet profile; multi-draft needs merge UX that does not exist.
