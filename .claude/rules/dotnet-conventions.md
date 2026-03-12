# .NET / C# Conventions for WTM

## Nullable

- New files: fully nullable-annotated, never add `#nullable disable`
- catch blocks: use `?.` + fallback, never null-forgiving `!`
- Double-`!` chains forbidden — split into separate null checks
- `DC!` / `Wtm!` null-forgiving only where framework guarantees initialization

## EF Core

- Multi-tenancy via global query filters on `ITenant`
- `IgnoreQueryFilters()` requires a comment explaining why
- SQLite shared in-memory for tests: `DataSource=name?mode=memory&cache=shared`

## Testing

- Controller tests: direct instantiation with `MockWtmContext.CreateWtmContext()`
- VM tests: inject via `MockWtmContext.CreateWtmContext(dataContext)`
- `method.Invoke()` wraps exceptions in `TargetInvocationException` — always unwrap
- JS tests: fresh `makeEnv()` per test to avoid IIFE state leakage

## Build & Release

- `version.props` is the single source of version number
- Update `CHANGELOG.md` with every version bump
- Breaking changes: document migration path, bump minor version
- `dotnet build -c Release` + `dotnet test -c Release` must pass before any PR
