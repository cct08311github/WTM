---
paths:
  - "test/**"
  - "**/test/**"
  - "**/*Test.cs"
  - "**/*Tests.cs"
  - "**/*.Test.csproj"
  - "**/*.Tests.csproj"
  - "**/*.test.js"
---

# Testing (WTM)

Tests live under `test/` — **singular**, not `tests/` — plus `src/WalkingTec.Mvvm.Mvc.Tests`. Run `ls test/` for the current set; it changes.

## Framework: MSTest, not xUnit

`[TestClass]` / `[TestMethod]` with **MSTest** + Moq. `Directory.Packages.props` contains no xunit or nunit entry and none may be added — this overrides the general "prefer xUnit" convention. FluentAssertions is available but not used everywhere; match the file you are editing.

## Isolation & mocks

- `MockController.CreateController<T>(dataContext, usercode)` / `CreateApi<T>(...)` in `Test.Mock` builds the controller with a `MockWtmContext` and wired `HttpContext` / `Session` / `ModelState`.
- Each test class generates its own seed: `new DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory)`. Never share a seed across classes.
- Scenarios needing framework tables (users, roles, menus) use `Demo.DataContext` with a unique seed.
- **`Wtm.CreateDC("default")` cannot be mocked.** Endpoints that call it (e.g. `FrameworkMenuListVM2.GetSearchQuery()`) need connection-string configuration `MockController` cannot supply — exclude them from unit tests and cover them in integration tests. Do not burn a session trying to make that path mockable.

## EF InMemory limits

The InMemory provider cannot translate `ExecuteUpdate`, `ExecuteDelete`, or correlated sub-queries: code that works in production fails the test with "LINQ expression could not be translated". Those tests need a SQLite shared-memory fixture — skill `efcore-sqlite-shared-memory-test` has the pattern.

**InMemory does not enforce foreign keys, so it turns a production failure into a green test.** A test that writes an FK value no real database would accept — `Guid.Empty`, an orphan id, a soft-deleted principal — and then asserts it "persisted" is a false assurance: on SQL Server / SQLite-with-FK / Oracle the same code raises a constraint violation and rolls back the whole transaction. This shipped once: a #815 fix cleared an unauthorized `ISubFile.FileId` to `Guid.Empty` and its InMemory tests asserted that value persisted, hiding the fact that the "controlled rejection" was actually a transaction rollback in production (caught by cross-vendor review, not by the same-vendor rounds). **Anything asserting the behaviour of an invalid or absent FK needs the SQLite fixture with FK enforcement on, not InMemory.** Same rule for uniqueness and cascade-delete semantics.

## JS and e2e

- `cd test/WalkingTec.Mvvm.Js.Tests && npm test` — Jest + jsdom over `framework_layui.js` / `framework_analysis.js`. Its `package.json` sets `rootDir: "../.."`, so every path in the jest config is **repo-root-relative**, not test-dir-relative.
- `cd test/e2e && pip install -r requirements.txt && python wtm_e2e_tests.py` (`--list` prints `TC_REGISTRY` without connecting). ETL integration has `test/docker-compose.etl-test.yml`.

## What needs a test here

- New VM logic and Analysis features: unit tests. New controller endpoints: happy path **plus** an auth test.
- ETL connectors: test against mock data sources.
- Performance changes: add or update a benchmark and record before/after numbers — `dotnet run -c Release --project test/WalkingTec.Mvvm.Benchmarks`.
