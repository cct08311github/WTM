# WalkingTec.Mvvm.Integration.Test

Integration tests that hit a **real SQL Server** — not SQLite in-memory, not
MSTest mocks. Covers EF Core migrations, multi-tenant query filters, and
`DataContext` behavior that can only be validated against a real relational
database.

## Running locally

### Option 1 — Docker (recommended)

```bash
# From the repo root:
docker compose up -d mssql

# Wait ~15 s for SQL to finish cold start, then run:
dotnet test test/WalkingTec.Mvvm.Integration.Test -c Release
```

Tear down when done:

```bash
docker compose down -v
```

### Option 2 — Point to an existing SQL Server

```bash
export WTM_TEST_MSSQL='Server=host,1433;Database=WtmInt;User Id=sa;Password=...;TrustServerCertificate=True'
dotnet test test/WalkingTec.Mvvm.Integration.Test -c Release
```

The connection string is parsed by
`SqlConnectionStringBuilder`; `InitialCatalog` is overwritten per-test with a
unique DB name, so set any placeholder for `Database=`.

### Option 3 — Skip entirely

Run the whole solution without Integration.Test:

```bash
dotnet test WalkingTec.Mvvm.sln -c Release --filter "TestCategory!=Integration"
```

Or run just the CI-scope solution filter:

```bash
dotnet test ci.slnf -c Release
```

`ci.slnf` intentionally excludes `WalkingTec.Mvvm.Integration.Test`; the
main `ci-build.yml` workflow uses this filter.

## What happens without SQL

When SQL Server is not reachable, each test calls
`IntegrationTestBase.EnsureSqlServerAvailable()` which throws
`AssertInconclusiveException` with a setup guide.

Tests appear as **yellow Inconclusive**, not red Failed — matching the actual
semantic (environment precondition not met, not code broken). The probe is
cached via `Lazy<T>` so the 2 s connect timeout is only paid once per test
run.

## CI

The `Integration Tests (MSSQL)` workflow
(`.github/workflows/integration-test.yml`) runs on push to `dotnet10` when
`src/**` or this test project changes. It spins up a SQL Server 2022
service container with identical credentials to `docker-compose.yml`.

## Test classes

| File | Scope |
|------|-------|
| `CrudTests.cs` | `BaseCRUDVM` add/update/delete round-trip via SQL |
| `DataContextTests.cs` | `DataContext.ConnectAndMigrate` + engine detection |
| `FrameworkContextTests.cs` | Model building + framework entity schema |
| `MultiTenantTests.cs` | Global query filters via `ITenant` |
| `VmTests.cs` | `BasePagedListVM` paging + `BaseCRUDVM` round-trip |

## Notes

- Each test owns its own database name (`WtmIntTest_{TestClassName}`). No
  cross-test interference; parallel execution is safe.
- `EnsureDeleted()` + `EnsureCreated()` per test — not migrations. Fast but
  means schema drift from migration files is not validated here.
- `Microsoft.Data.SqlClient` is a transitive dependency of
  `Microsoft.EntityFrameworkCore.SqlServer`; the csproj pins the version to
  match CI.
