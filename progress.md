# WTM Progress

Purpose: current handoff for the next AI collaborator. This file is repo-tracked in the current branch history, so keep it consistent with actual code and test status.

Last updated: 2026-03-08
Primary branch: `dotnet8`
Current HEAD: `bfc58961` (`docs: reconcile progress handoff with verified repo state`)

## Branch Status

- `dotnet8` is the active mainline in this fork
- `dotnet8` is the only branch that should be treated as source of truth
- local `dotnet8` is ahead of `origin/dotnet8` by 8 commits
- all local `codex/*`, `feature/*`, and `main` branches/worktrees have been removed to stop branch drift and handoff confusion
- the test coverage work from `feature/8.1.15-testing` is already merged into local `dotnet8`
- several stale remote branches still exist on `origin`; ignore them unless you are explicitly doing remote branch cleanup

## Verified System State

### Elsa removal

- Elsa/Rebus decoupling work is already merged on `dotnet8`
- key completion point is `999095be`
- code still contains explanatory `Elsa removed` comments/stubs, but the package references and runtime wiring were removed intentionally

### Nullable modernization

- `PropertyHelper.cs` is now `#nullable enable`, but it is **not complete**
- `WTMContext.cs` is still `#nullable disable`
- `Extensions/ListVMExtension.cs` is still `#nullable disable`
- only 2 Core files remain explicitly disabled, but the nullable cleanup is not functionally done because many enabled files still warn
- current forced rebuild baseline:
  - `~/.dotnet/dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -t:Rebuild`
  - result: `56 warnings / 0 errors`
- do not claim "only WTMContext remains" unless `PropertyHelper.cs`, `Utils.cs`, `ExcelPropety.cs`, and other enabled files are brought back to a stable warning target

### Testing

The previously separate `feature/8.1.15-testing` work is now merged into `dotnet8` and verified.

- `src/WalkingTec.Mvvm.Core.Tests`: 61 tests passing
- `src/WalkingTec.Mvvm.Mvc.Tests`: 24 tests passing
- `test/WalkingTec.Mvvm.Core.Test`: 224 tests passing
- `test/WalkingTec.Mvvm.Admin.Test`: 29 tests passing
- `test/WalkingTec.Mvvm.Js.Tests`: 123 tests passing

### JWT/token updates

- `src/WalkingTec.Mvvm.Mvc/Auth/JwtAuth/TokenService.cs` now adds `jti`
- `DoLoginAsync` tests use SQLite shared in-memory instead of EF InMemory where relational translation matters

## Verified Commands

- `~/.dotnet/dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -t:Rebuild`
  - PASS
  - `56 warnings / 0 errors`
- `~/.dotnet/dotnet test WalkingTec.Mvvm.sln -c Release`
  - PASS
  - Core.Tests `61/61`
  - Mvc.Tests `24/24`
  - Core.Test `224/224`
  - Admin.Test `29/29`
- `npm test` in `test/WalkingTec.Mvvm.Js.Tests`
  - PASS
  - `123/123`

## Commits Added In This Session

- `88ab0a55` `test(8.1.15): comprehensive test coverage — 50+ tests across 7 new files`
- `b2039b7b` `fix(test): resolve CI compile errors in Core.Tests and Admin.Test`
- `7cf188e7` `fix(tests): add missing Token namespace and fix Moq nullable ambiguity`
- `476b6eed` `fix(tests): add missing using System.Threading.Tasks for Task.FromResult`
- `819457ac` `fix(tests): fix NullReferenceException in DoLoginAsync and EF provider conflict`
- `96dc0446` `fix(tests): use EmptyContext for DoLoginAsync tests, FrameworkContext for TokenFixture`
- `99942071` `fix(tests): use SQLite shared in-memory for DoLoginAsync; add jti to JWT`

## Backlog Tracking

Detailed backlog is tracked in GitHub Issues, not in this file.

- #50 Continue Core nullable cleanup from the verified `56 warnings / 0 errors` baseline
- #51 Clean up nullable warnings in `PropertyHelper.cs`
- #52 Evaluate and convert `ListVMExtension.cs`
- #53 Evaluate and convert `WTMContext.cs`

Use this document for verified repo state and handoff notes only.
