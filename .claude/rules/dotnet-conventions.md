# .NET / C# Conventions for WTM

## Code Style

- New code: fully nullable-annotated, no `#nullable disable`
- Prefer explicit over implicit; avoid magic strings
- Follow existing naming conventions in the codebase
- Comments only where logic is non-obvious; no boilerplate doc comments

## Nullable

- `WalkingTec.Mvvm.Core` has `<Nullable>enable</Nullable>`; legacy files carry `#nullable disable`
- catch blocks: use `?.` + fallback, never null-forgiving `!`
- Double-`!` chains forbidden — split into separate null checks
- `DC!` / `Wtm!` null-forgiving only where framework guarantees initialization

## EF Core

- Multi-tenancy via global query filters on `ITenant`
- `IgnoreQueryFilters()` requires a comment explaining why
- SQLite shared in-memory for tests: `DataSource=name?mode=memory&cache=shared`

## Testing

### Test projects

| Project | Framework | Covers |
|---------|-----------|--------|
| `test/WalkingTec.Mvvm.Core.Test` | MSTest | Core VM, Analysis engine, Analysis controller |
| `test/WalkingTec.Mvvm.Admin.Test` | MSTest | Admin-layer controllers |
| `test/WalkingTec.Mvvm.Js.Tests` | Jest | `framework_analysis.js` pure functions + DOM |
| `test/WalkingTec.Mvvm.Test.Mock` | (helper) | `MockWtmContext.CreateWtmContext()`, `MockController.CreateController<T>()` |

### Patterns

- Controller tests: direct instantiation with `MockWtmContext.CreateWtmContext()`
  ```csharp
  var controller = new _AnalysisController(registry);
  controller.Wtm = MockWtmContext.CreateWtmContext();
  ```
- Data injection without DB: override `GetSearchQuery()` in test ListVM to return static in-memory collection; reset in `[TestInitialize]`
- VM tests: inject via `MockWtmContext.CreateWtmContext(dataContext)`; use SQLite shared in-memory for relational queries
- `method.Invoke()` wraps exceptions in `TargetInvocationException` — always unwrap
- JS tests: fresh `makeEnv()` per test to avoid IIFE state leakage

## Build & Release

- `version.props` is the single source of version number
- Update `CHANGELOG.md` with every version bump
- Breaking changes: document migration path, bump minor version
- `dotnet build -c Release` + `dotnet test -c Release` must pass before any PR
