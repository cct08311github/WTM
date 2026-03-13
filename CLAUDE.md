# CLAUDE.md

## Project Mission

Personal fork of WalkingTec MVVM Framework (WTM), taken over 2026-03. Goal: **stable, modernized, actively-evolved** .NET rapid-development framework.

Current phase: **Takeover & Revival** — stabilize, modernize, improve quality, then add features.

## Decision Priorities (in order)

1. **Compatibility** — avoid breaking existing users; deprecate before removing
2. **Security** — fix vulnerabilities immediately (P0), no shortcuts
3. **Quality** — nullable annotations, structured logging, test coverage
4. **Performance** — optimize only with evidence (profiling, benchmarks)

## Build & Test Commands

```bash
# Build entire solution
dotnet build WalkingTec.Mvvm.sln -c Release

# Run all .NET tests
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal

# Run a single .NET test class
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "FullyQualifiedName~AnalysisControllerTests" -c Release

# Run a single .NET test method
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "Name=Query_happy_path_returns_200_with_aggregated_rows" -c Release

# Run JS tests
cd test/WalkingTec.Mvvm.Js.Tests && npm ci && npm test

# Pack NuGet packages (CI uses /p:Version=$PKG_VERSION)
dotnet pack src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.TagHelpers.LayUI/WalkingTec.Mvvm.TagHelpers.LayUI.csproj -c Release -o nupkgs

# Vulnerability scan
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

## Architecture (quick ref)

WTM is an ASP.NET Core 8 framework. Four VM types: `BaseCRUDVM<T>`, `BasePagedListVM<T,S>`, `BaseImportVM<T>`, `BaseBatchVM<T>` — all extend `BaseVM` with `WTMContext Wtm`.

| Project | Role |
|---------|------|
| `src/WalkingTec.Mvvm.Core` | Core: VMs, DataContext, Models, Analysis engine |
| `src/WalkingTec.Mvvm.Mvc` | Controllers, startup extensions, JS assets |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI TagHelpers |

## Docs

- `docs/analysis-mode.md` — Analysis Mode manual
- `docs/lookup-cache.md` — Lookup Cache manual
- `CHANGELOG.md` — version history (update when releasing)
- `version.props` — single source of version number

## Detailed Rules

All detailed conventions, architecture, testing patterns, and dependency policies are in `.claude/rules/`:

- `architecture.md` — full architecture, Analysis Mode, security, compatibility
- `dotnet-conventions.md` — code style, nullable, EF Core, testing patterns, build & release
- `dependency-management.md` — upgrade policy, .NET version strategy, package versioning
