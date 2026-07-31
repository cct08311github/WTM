---
paths:
  - "**/*.cs"
  - "**/*.cshtml"
---

# .NET Conventions (WTM-specific)

Only what a competent .NET engineer would get wrong *here*.

## Nullable

- Never commit `#nullable disable` in new code — `grep -rln "#nullable disable" src/` is currently 0 and must stay there.
- Never chain double-`!` null-forgiving operators (`obj!.Prop!`) — split into separate null checks. A single `!` is fine where the compiler cannot infer non-null but the value is guaranteed.
- New or changed public API members get XML doc comments.

## Async

- **Never** `.GetAwaiter().GetResult()` on a request path. `WTMContext.LoginUserInfo` is a *synchronous* getter that used to do exactly that on cache miss and starved the ThreadPool under load (#128). Since 10.5.4 `WtmMiddleware.InvokeAsync` calls `EnsureLoginUserInfoAsync()` before controllers run — in middleware or anywhere early in the pipeline, `await wtm.EnsureLoginUserInfoAsync()` and never touch the getter from async code.
- `ConfigureAwait(false)` in library code (`Core`, `Etl`) but **not** in MVC controller code.

## EF Core

- `IgnoreQueryFilters()` always needs a comment explaining why.
- Wrap batch work in a transaction so a failure cannot half-commit.
- Schema-qualified lookups must filter on **both** `TABLE_SCHEMA` and `TABLE_NAME`.
- Create parameters provider-agnostically via `DbCommand.CreateParameter()`.
- EF Core 10: the `HasConversion` generic overload changed behaviour — retest after any EF bump. Oracle (`Oracle.EntityFrameworkCore 10.23.x`) runs on its own release cadence; exercise Oracle codepaths separately.

## Hot paths

Cache `MethodInfo` / `PropertyInfo`; never call `GetMethod()` / `GetProperty()` per request. `AnalysisQueryEngine` caches its four `ExecuteDynamic*` lookups and the `ApplyFilters` `Contains` lookup. New reflection code gets a static cache plus a comment stating the rationale.

## Logging

- Serilog structured parameters, never interpolation: `Log.Information("User {UserId} logged in", userId)`.
- Resolve `ILogger` from DI — not `Log.Logger` in request-scoped code.
- Sanitize raw error text before persisting it (ETL dead-letter rows).
