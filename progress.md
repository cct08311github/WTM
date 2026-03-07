# WTM Progress

Purpose: current handoff for the next AI collaborator. This file is repo-tracked in the current branch history, so keep it consistent with actual code and test status.

Last updated: 2026-03-07
Primary branch: `dotnet8`
Current HEAD: `99942071` (`fix(tests): use SQLite shared in-memory for DoLoginAsync; add jti to JWT`)

## Branch Status

- `dotnet8` is the active mainline in this fork
- local `dotnet8` is ahead of `origin/dotnet8` by 7 commits
- local `main` is behind `dotnet8` by 35 commits and should be fast-forwarded if you want `main` to mirror the active line
- `feature/8.1.15-testing` has been merged into `dotnet8` by cherry-picking its 7 commits
- old `codex/*` branches that looked unmerged were checked with `git cherry`; their patches are already effectively present in `dotnet8`

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
  - `/Users/openclaw/.dotnet/dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -t:Rebuild`
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

- `/Users/openclaw/.dotnet/dotnet build src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -t:Rebuild`
  - PASS
  - `56 warnings / 0 errors`
- `/Users/openclaw/.dotnet/dotnet test WalkingTec.Mvvm.sln -c Release`
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

## Recommended Next Work

1. Fast-forward `main` to `dotnet8` if both branches are meant to represent the same line locally.
2. Push `dotnet8` if the new test coverage work should exist on the remote.
3. Continue nullable cleanup from the real baseline of `56` Core warnings, not the obsolete `10/21/25` numbers.
4. Focus first on `PropertyHelper.cs` warning cleanup, then decide whether `ListVMExtension.cs` or `WTMContext.cs` is the safer next conversion.
