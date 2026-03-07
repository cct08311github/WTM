# WTM Progress

Purpose: handoff doc for Claude Opus or another AI collaborator. Keep this file updated locally, but do not commit it.

Last updated: 2026-03-07
Branch: `dotnet8`
Remote status: commits through `999095be` pushed; `main` is behind `dotnet8`

## Branch Status

- `dotnet8` is ahead of `main`
- `main` was last synced at `f29d6d8c`
- latest pushed commit on `dotnet8`: `f797dc11` (`refactor: enable nullable in DataContext.cs and IDataContext.cs`)

## Quick Handoff

- Current source of truth: `dotnet8` branch at `999095be`
- This file is intentionally untracked and should stay uncommitted
- Safe local test commands:
  - `/Users/openclaw/.dotnet/dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release`
  - `/Users/openclaw/.dotnet/dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj -c Release`
- Current untracked files to ignore:
  - `coverage/`
  - `docs/plans/2026-03-06-layui-modernization-p0-p1.md`
  - `test/WalkingTec.Mvvm.Js.Tests/coverage/`
  - `progress.md`

## Goal

Continue two tracks in parallel:

1. finish nullable modernization of the last 6 Core files in safe batches
2. prepare the upstream Elsa removal work needed by BMS package consumers

---

## Nullable Modernization Checklist

### Stats

- **Original `#nullable disable` count**: 168 files
- **Converted so far**: 165 files (sessions 1-4 + DataContext.cs + Utils.cs + ExcelPropety.cs)
- **Remaining**: 1 file (`WTMContext.cs`)
- **Current local Core build warning baseline**: 21 warnings on `dotnet8`
- **Old 10-warning note is obsolete**: current baseline also includes several warnings in `Utils.cs` after recent nullable work
- **Tests**: 224/224 Core tests pass

### QA Correction Applied

- [x] Re-tested `ListVMExtension.cs` nullable flip
- [x] Confirmed the flip introduced many new warnings and was not shippable
- [x] Reverted the premature flip in commit `3ae10404`
- [x] Restored baseline: Core build back to the stable 10 warnings; Core tests back to `224/224`

### Remaining 6 Files — Conversion Checklist

Each file follows the same workflow:
1. `sed -i '' '1s/#nullable disable/#nullable enable/' <file>` → build → count warnings
2. Fix warnings (see patterns below)
3. Build clean (0 new warnings beyond pre-existing 10)
4. Run 224 Core tests — all pass
5. Commit & push

#### Tier 1 — Medium (standalone, fewer cascading concerns)

- [x] **`Utils.cs`** (~898 lines, ~57 warnings)
  - Completed in `ec58c4da`
  - Build result after conversion: `0 warning / 0 error` for `WalkingTec.Mvvm.Core.csproj`
  - Core tests after conversion: `224/224`

- [x] **`ExcelPropety.cs`** (~800 lines, ~46 warnings)
  - Completed in `443dec27`
  - Build result after conversion: `0 warning / 0 error` for `WalkingTec.Mvvm.Core.csproj`
  - Core tests after conversion: `224/224`

#### Tier 2 — Hard (significant cascading, shared infrastructure)

- [x] **`DataContext.cs`** (~1010 lines, ~152 warnings)
  - Completed in `f797dc11`
  - Build result after conversion: `0 new warning / 0 error`
  - Core tests after conversion: `224/224`

- [x] **`DCExtension.cs`** (~1190 lines, ~170 warnings)
  - Completed in `2f7b69fc`
  - Build result after conversion: `0 new warning / 0 error`
  - Core tests after conversion: `224/224`
  - Key challenges: `Expression?`, `PropertyInfo?` from reflection, generic type constraints
  - Moderate cascading — extension methods called throughout framework
  - Strategy: many `!` on known-non-null expression tree nodes

- [ ] **`PropertyHelper.cs`** (~700 lines, ~276 warnings)
  - Heavy reflection, enum display, property metadata
  - **BIGGEST BLOCKER**: return types cascade to every file using reflection helpers
  - Strategy: convert last or accept `!` at all call sites; consider region-level `#nullable enable`
  - WARNING: changing return types here will cascade to already-converted files

#### Tier 3 — Nuclear (convert last)

- [ ] **`WTMContext.cs`** (~1471 lines, ~294 warnings)
  - Core DI container, request context, session, permissions
  - Touches everything — every VM holds a `WTMContext Wtm` reference
  - Convert this LAST after all other files are done
  - Strategy: mostly `null!` for DI-injected fields, `string?` for optional config

### Recommended Execution Order

```
1. Utils.cs                          — standalone, low risk
2. ExcelPropety.cs                   — standalone, localizer pattern known
3. DataContext.cs                    — before DCExtension (DCExtension uses DataContext types)
4. DCExtension.cs                    — after DataContext
5. PropertyHelper.cs                 — return types cascade; do after consumers are stable
6. WTMContext.cs                     — absolute last; touches everything
7. ListVMExtension.cs                — re-open only after `PropertyHelper.cs` and related reflection helpers are stabilized
```

### Cascading Risk Matrix

| File being converted | Files that may need `!` fixes |
|---------------------|-------------------------------|
| Utils.cs | Minimal — mostly called with concrete types |
| ExcelPropety.cs | None — self-contained |
| DataContext.cs | DCExtension, IServiceExtension, WTMContext |
| DCExtension.cs | BaseCRUDVM, BasePagedListVM, BaseImportVM |
| PropertyHelper.cs | **Many** — TypeExtension, ListVMExtension, ExcelPropety, all VMs |
| WTMContext.cs | **All VMs** via `Wtm` property |

---

## Key Patterns (reference for any AI)

| Pattern | When to use |
|---------|-------------|
| `property!` | Property guaranteed non-null by context (e.g. `GetSingleProperty("ID")!`) |
| `null!` for fields | DI-injected or late-initialized fields (`_modelType = null!`) |
| `MSD?`/`Localizer?` + fallback | Catch blocks — never use `!` in error paths |
| `CoreProgram._localizer` ternary | `loc != null ? (string)loc["key"] : "fallback"` for LocalizedString CS8604 |
| `li!` in foreach | Unconstrained generic `V` makes loop var nullable |
| `as T` → direct cast `(T)` | When null is impossible, avoids CS8602 from `as` |
| `BaseType?.Method()` | Recursive type traversal |

## Important Notes For The Next AI

- `.NET` SDK: `/Users/openclaw/.dotnet/dotnet` (version 8.0.418) — use full path
- User prefers autonomous execution without confirmation between batches
- **Pre-existing warnings (10 stable)**: Don't try to fix these — they're in already-converted files with complex nullable chains
- **Warning baseline changed**: current local `dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release` on `dotnet8` reports 25 warnings, not 10. Treat 25 as the current comparison baseline until reduced intentionally.
- **PropertyHelper.cs is the biggest blocker**: 276 warnings, and its return types cascade to every file that uses reflection helpers
- **Mvc project** does NOT have `<Nullable>enable</Nullable>` at project level
- **Known regression pattern**: when modernizing config objects, preserve runtime defaults exactly
- **ListVMExtension.cs is NOT done**: quick flip was a false positive. QA found many new nullable warnings, so it was reverted in `3ae10404`.

## Session History

### Session 5 (2026-03-07, in progress)

- [x] **`PropertyHelper.cs`** modernized (276+ warnings resolved)
- [ ] Target: **`WTMContext.cs`** (last file)

### Session 4 (2026-03-07, completed)

- `678c33a9` `refactor: enable nullable in ListVMExtension` — pushed, then failed QA
- `3ae10404` `fix: revert premature ListVMExtension nullable flip` — pushed, restores stable baseline
- `ec58c4da` `refactor: enable nullable in utils` — pushed, verified with Core build + `224/224` Core tests
- `443dec27` `refactor: enable nullable in excel property` — pushed, verified with Core build + `224/224` Core tests
- `999095be` `refactor: remove all Elsa package references, model files, and transitive deps` — pushed, verified 0 Elsa/Rebus deps, 1 warning / 0 errors, 258/258 tests pass
- `231c1f1e` `refactor: decouple Core + WorkFlow Elsa dependencies (Batch 2+3 merged)` — pushed, verified full solution build (23 warnings / 0 errors) + all 258 tests pass
- `688c469f` `refactor: decouple MVC Elsa entry points (_WorkflowApiController, AddWtmWorkflow, UI assets)` — pushed, verified full solution build/test, finishes Batch 1
- `2d304876` `refactor: decouple mvc formatter from elsa api` — pushed, verified with MVC build + `224/224` Core tests
- Reviewed `/Users/openclaw/.openclaw/shared/projects/BMS/progress_wtm.md`
- Started Elsa removal audit in worktree `/Users/openclaw/.openclaw/shared/worktrees/wtm-elsa-remove`
- Probed `DataContext.cs` in dedicated worktree `/Users/openclaw/.openclaw/shared/worktrees/wtm-datacontext-slice`
  - attempted first nullable slice on constructors / `ReCreate`
  - result: not shippable, increased local Core build warnings from 25 to 31
  - reverted, no commit

### Session 3 (2026-03-07, batches 6-12)

| Commit | Files | Description |
|--------|-------|-------------|
| `b96d4942` | 13 | File handlers, Quartz, WTMLogger, DateRange, BaseCRUDVM/BaseImportVM propagation |
| `392a9065` | 5 | CoreProgram, MSD, DataPrivilegeInfo, ListExtension, BaseBatchVM propagation |
| `738e2409` | 1 | DuplicateInfo |
| `b93cc85b` | 6 | TypeExtension + cascading fixes in TopBasePoco, DuplicateInfo, DataPrivilegeInfo, BaseImportVM, BasePagedListVM |
| `cd6c7135` | 3 | WorkflowRefresher, ApproveActivity, BookMark |
| `f122f27b` | 1 | ExpressionVisitors |
| `27764c27` | 2 | EntityHelper, IServiceExtension |

### Session 1-2 (2026-03-06 to 2026-03-07)

- 16 worktree parallel batches, QA audit fixes (#41-#43), VM files, core contracts, Analysis files

## BMS Upstream Alignment

Source document reviewed:

- `/Users/openclaw/.openclaw/shared/projects/BMS/progress_wtm.md`

Current conclusion:

- Removing Elsa is a valid upstream task for BMS compatibility
- This is larger than deleting package references; Elsa is wired into Core, Mvc, workflow controllers, service registration, DataContext mappings, workflow models, and Razor assets
- Do not start by deleting package references first; split the decoupling into compile-safe batches

Known Elsa touchpoints from code audit:

- `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`
- `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj`
- `src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs`
- `src/WalkingTec.Mvvm.Mvc/_WorkflowController.cs`
- `src/WalkingTec.Mvvm.Mvc/_WorkflowApiController.cs`
- `src/WalkingTec.Mvvm.Mvc/Helper/WtmElsaContext.cs`
- `src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs`
- `src/WalkingTec.Mvvm.Core/BaseBatchVM.cs`
- `src/WalkingTec.Mvvm.Core/DataContext.cs`
- `src/WalkingTec.Mvvm.Core/WorkFlow/`
- `src/WalkingTec.Mvvm.Core/Models/Elsa_*`
- `src/WalkingTec.Mvvm.Mvc/Views/_Workflow/`

## QA Results

### Verified this session

- [x] `/Users/openclaw/.dotnet/dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release`
  - PASS
  - current local baseline on `dotnet8`: 25 warnings
- [x] `/Users/openclaw/.dotnet/dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj -c Release`
  - PASS
  - `224/224`
- [x] `Utils.cs` nullable batch
  - PASS
  - commit: `ec58c4da`
  - file build result: `0 warning / 0 error`
  - Core tests after batch: `224/224`
- [x] `ExcelPropety.cs` nullable batch
  - PASS
  - commit: `443dec27`
  - file build result: `0 warning / 0 error`
  - Core tests after batch: `224/224`
- [x] QA on `ListVMExtension.cs` quick flip
  - FAIL as a modernization batch
  - introduced many new warnings
  - reverted in `3ae10404`
- [x] Elsa dependency audit in dedicated worktree
  - result: coupling surface is broad; removal must be phased
- [x] Elsa Batch 1 sub-batch: remove direct `Elsa.Server.Api.*` compile-time dependency from MVC formatter path
  - PASS
  - commit: `2d304876`
  - validation: `dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release`
  - validation: `dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj -c Release`
- [x] DataContext slice probe
  - FAIL as a commit candidate
  - first nullable slice raised local Core build warnings from 25 to 31
  - reverted in worktree, no commit created
- [x] `/Users/openclaw/.dotnet/dotnet build WalkingTec.Mvvm.sln -c Release`
  - PASS
  - local issue `NETSDK1177` resolved automatically in the environment
- [x] `/Users/openclaw/.dotnet/dotnet test WalkingTec.Mvvm.sln -c Release`
  - PASS
  - All test runner sets returned passed (Admin.Test, Core.Test, etc.)

## Multi-AI Checklist

Use this as the live shared checklist. Update checkboxes locally after each batch.

Suggested parallel ownership:

- `wtm-global` or main repo: Track A nullable batches (`Utils.cs`, `ExcelPropety.cs`)
- `wtm-elsa-remove`: Track B Elsa decoupling
- `wtm-datacontext-slice`: scratch space for `DataContext.cs` experiments; keep clean between attempts
- separate fresh worktree if needed: `DCExtension.cs` / `WTMContext.cs`

### Track A — Nullable modernization

- [x] Revert unsafe `ListVMExtension.cs` flip
- [x] Convert `Utils.cs`
- [x] Build Core after `Utils.cs`
- [x] Run Core tests after `Utils.cs`
- [x] Commit and push `Utils.cs`
- [x] Convert `ExcelPropety.cs`
- [x] Build Core after `ExcelPropety.cs`
- [x] Run Core tests after `ExcelPropety.cs`
- [x] Commit and push `ExcelPropety.cs`
- [ ] Convert `DataContext.cs` (Slice 1: configuration & DB provider setup)
- [ ] Build Core after `DataContext.cs` Slice 1
- [ ] Run Core tests after `DataContext.cs` Slice 1
- [ ] Commit and push `DataContext.cs` Slice 1
- [ ] Convert `DataContext.cs` (Slice 2: DbSet properties & remaining parameters)
- [ ] Build Core after `DataContext.cs` Slice 2
- [ ] Run Core tests after `DataContext.cs` Slice 2
- [ ] Commit and push `DataContext.cs` Slice 2
  - note: first constructor/`ReCreate` slice was attempted and reverted; take smaller baby steps
- [x] Convert `DCExtension.cs`
- [x] Build Core after `DCExtension.cs`
- [x] Run Core tests after `DCExtension.cs`
- [x] Commit and push `DCExtension.cs`
- [x] **`PropertyHelper.cs`** (~700 lines, ~276 warnings)
  - [x] **Batch 1: Expression Tree Parsers** (GetPropertyName, GetExpressionRootObj, GetMemberExp)
  - [x] **Batch 2: Metadata & Attributes** (GetPropertyDisplayName, GetRegexErrorMessage, IsPropertyRequired)
  - [x] **Batch 3: Property Value Access** (GetPropertyValue, SetPropertyValue, GetMemberValue/Type)
  - [x] **Batch 4: Enum & Conversion** (GetEnumDisplayName, ConvertValue)
- [x] Build Core after `PropertyHelper.cs`
- [x] Run Core tests after `PropertyHelper.cs`
- [x] Commit and push `PropertyHelper.cs`
- [ ] **`WTMContext.cs`** (~1471 lines, ~294 warnings)
  - [ ] **Batch 1: Core Fields & Properties** (DI injected fields, constructor, basic properties)
  - [ ] **Batch 2: User & Permissions** (LoginUserInfo, DataPrivilegeSettings, Tenant logic)
  - [ ] **Batch 3: Configuration & Logging** (GlobalConfig, Loggers)
  - [ ] **Batch 4: Component Registries** (UIService, Localizer providers)
- [ ] Build Core after `WTMContext.cs`
- [ ] Run Core tests after `WTMContext.cs`
- [ ] Commit and push `WTMContext.cs`
- [ ] Re-open `ListVMExtension.cs` only after `PropertyHelper.cs` stabilizes

### Track B — Elsa removal for BMS

- [x] Read `/Users/openclaw/.openclaw/shared/projects/BMS/progress_wtm.md`
- [x] Create dedicated Elsa removal worktree: `/Users/openclaw/.openclaw/shared/worktrees/wtm-elsa-remove`
- [x] Inventory Elsa package and source usage
- [x] Write phased Elsa removal batch plan in this file before editing code
- [x] Batch 1: decouple MVC registration surface (`AddWtmWorkflow`, controllers, view assets)
  - [x] sub-batch A: formatter/convention path no longer uses `Elsa.Server.Api.*` compile-time APIs
  - [x] sub-batch B: `_WorkflowApiController.cs` direct Elsa dependencies
  - [x] sub-batch C: `AddWtmWorkflow` registration path
  - [x] sub-batch D: workflow view assets / middleware routing assumptions
- [x] Build solution after Elsa batch 1
- [x] Run relevant tests after Elsa batch 1
- [x] Batch 2: decouple Core workflow runtime references (`BaseCRUDVM`, `BaseBatchVM`, `WorkFlow/`)
  - [x] sub-batch A: Decouple CRUD operations within `BaseCRUDVM`
  - [x] sub-batch B: Decouple batch operations within `BaseBatchVM`
  - [x] sub-batch C: Remove standalone classes and logic in the `WorkFlow/` folder
- [x] Build Core after Elsa batch 2
- [x] Run Core tests after Elsa batch 2
- [x] Batch 3: remove `Models/Elsa_*`, `WtmElsaContext`, and `DataContext` Elsa DbSets/table mappings
  - [x] sub-batch A: Remove Elsa table mappings from `DataContext.cs`
  - [x] sub-batch B: Remove Elsa entities and scripts from `Models/` folder
  - [x] sub-batch C: Delete `WtmElsaContext.cs` helper entirely
- [x] Build solution after Elsa batch 3 (COMPLETE — full clean build)
- [x] Run tests after Elsa batch 3
- [x] Remove Elsa package references from both csproj files (+ all 5 demo projects)
- [x] Verify `dotnet list ... --include-transitive | grep -i elsa` returns no output ✅ **0 matches**
- [x] Verify `dotnet list ... --include-transitive | grep -i rebus` returns no output ✅ **0 matches**
- [ ] Bump version and publish packages

### Track C — QA / environment follow-up

- [x] Investigate local demo apphost signing failure (`NETSDK1177`) for full-solution local validation
- [x] Re-run full solution build after apphost issue is resolved
- [x] Re-run full solution test after apphost issue is resolved

## Elsa Removal Batch Plan

Use `/Users/openclaw/.openclaw/shared/worktrees/wtm-elsa-remove`.

### Batch 1

- goal: isolate MVC-facing Elsa entry points so package removal can be staged later
- likely files:
  - `src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs`
  - `src/WalkingTec.Mvvm.Mvc/_WorkflowController.cs`
  - `src/WalkingTec.Mvvm.Mvc/_WorkflowApiController.cs`
  - `src/WalkingTec.Mvvm.Mvc/Views/_Workflow/`
  - `src/WalkingTec.Mvvm.Mvc/Binders/NewtonsoftJsonFormatterAttribute.cs`
  - `src/WalkingTec.Mvvm.Mvc/Helper/MyNewtonsoftJsonConvention.cs`
- target outcome:
  - Elsa-specific controllers and formatter hooks no longer block build if Elsa packages are isolated
  - progress:
    - completed: formatter path + `_WorkflowController.cs` cleanup in `2d304876`, plus `_WorkflowApiController.cs`, `AddWtmWorkflow`, view assets, and demo application configurations decoupled in current session!
    - remaining: None, Batch 1 is fully complete.

### Batch 2 + 3 (merged)

- goal: isolate Core runtime workflow hooks + remove Elsa persistence/model footprint
- files modified:
  - `src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs` — commented Elsa usings, stubbed 4 workflow methods, removed Elsa entity cleanup from delete
  - `src/WalkingTec.Mvvm.Core/BaseBatchVM.cs` — removed Elsa entity cleanup from batch delete
  - `src/WalkingTec.Mvvm.Core/DataContext.cs` — commented Elsa DbSet properties + Oracle table mappings
  - `src/WalkingTec.Mvvm.Core/WorkFlow/ApproveActivity.cs` — replaced with compile-safe stub
  - `src/WalkingTec.Mvvm.Core/WorkFlow/WorkflowRefresher.cs` — replaced with empty stub
  - `src/WalkingTec.Mvvm.Core/WorkFlow/ElsaTenantAccessor .cs` — replaced with empty stub
  - `src/WalkingTec.Mvvm.Core/WorkFlow/InstanceWrap.cs` — replaced with empty stub
  - `src/WalkingTec.Mvvm.Core/WorkFlow/BookMark.cs` — replaced with compile-safe stubs
  - `src/WalkingTec.Mvvm.Core/WorkFlow/ResultWrap.cs` — replaced InstanceWrap reference with object?
  - `src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs` — commented AddBookmarkProvider
- target outcome:
  - Core no longer references any Elsa types at compile time
  - DataContext no longer registers Elsa DbSets or table mappings
  - progress: COMPLETE for runtime decoupling. Remaining: delete `Models/Elsa_*` files and remove csproj package refs

### Final Elsa Package Cleanup (999095be)

- [x] Delete 5 `Models/Elsa_*.cs` entity files
- [x] Delete `WtmElsaContext.cs`
- [x] Delete demo Elsa source files (`SMSActivity.cs`, `WorkflowStatusFilterSpecification.cs`)
- [x] Remove `Elsa.Server.Core` from `WalkingTec.Mvvm.Core.csproj`
- [x] Remove 9 Elsa packages from `WalkingTec.Mvvm.Mvc.csproj`
- [x] Remove Elsa packages from 5 demo `.csproj` files
- [x] Add explicit `Microsoft.AspNetCore.Mvc.NewtonsoftJson` + `Versioning` packages (were transitive deps from Elsa)
- [x] Clean `NodaTime`, `NetBox`, `Open.Linq` transitive usings
- [x] Verify 0 Elsa deps across entire solution
- [x] Verify 0 Rebus deps across entire solution
- [x] Full solution build: 1 warning / 0 errors
- [x] Full solution test: 258/258 pass

## Current Workspace State

At the time this file was written (2026-03-07):

- main repo working tree has only unrelated untracked files plus `progress.md`
- `dotnet8` latest pushed commit is `999095be`
- `main` is still at `f29d6d8c`
- **Elsa removal: COMPLETE** — zero Elsa/Rebus dependencies in entire solution
- Full solution build passes with 1 warning / 0 errors
- Full solution tests pass (18 Core.Tests + 224 Core.Test + 16 Admin.Test = 258 total)
- Remaining: version bump and package publish
