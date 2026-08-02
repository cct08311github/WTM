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

## A fixture must not supply what production is supposed to supply

If a test builds its own version of the thing under test, it stays green when production breaks.

- **#899**: `TenantFilterInvariantTests` declared its own `DbSet` and added the query filter by hand. `ApplyWorkFlowModels` could stop wiring that filter entirely and the test never noticed.
- **#967**: the cohort-check fixture used Python's default `http.server` handler, which answers `HEAD` correctly. The real registries return `405`. The guard was green for months and failed on its first real use.

**A security fix that touches model or context wiring must register a mutant that neutralizes the *production* wiring**, with `red_test` pointing at the fixture-based test. If the fixture is compensating, the test stays green, the mutant SURVIVES, and the gate goes red. `wf899-applyworkflowmodels-processdefinition-tenantfilter-neutralize` and the `etl841`/`etl862` entries are the worked examples — copy their shape.

**Where this does not reach**: `run_mutant.py` builds and runs .NET test projects. It has no concept of `test/*.sh` or a Python fixture, so the shell/Python side of this rule is convention only, unenforced. #967 was found by CI failing on a real registry, not by the gate.

**Related check, on every run**: a test that cannot fail is a CI error — `scripts/check-e2e-test-integrity.py` (wired into `mutation-gate.yml`'s `changes` job) rejects a registered e2e TC with no `assert`, a handler that swallows `AssertionError` without a bare `raise`, `except AssertionError: raise TestSkipped(...)` laundering a FAIL into a SKIP, and a top-level `tc_*` that was never wired into `TC_REGISTRY`.

## A mutant's positive control must not touch the mutated decision path

A positive control exists to prove *everything except the mutated behaviour still works*. A `green_test` coupled to the line the patch mutates proves nothing — it is a second thing the mutation affects, not a control on it. This has shipped twice, in opposite failure modes:

- **#979** (`979-callapi-header-exception-leak-reintroduce`): the first `green_test` choice was the sibling "header name still logged" assertion — it asserts on output produced by the SAME `catch` block the mutant rewrites. Running `run_mutant.py` reported `VERDICT: POSITIVE_CONTROL_FAILED` — loud, and caught before the entry was ever committed. See `docs/production-readiness.md`'s `#979` section for the full trace.
- **#986** (`dcext824-derived-principal-neutralize`): the `green_test` called `IsFileAttachmentForeignKeyProperty` against a fixture whose only foreign key had a TPH-derived `FileAttachment` principal — the exact shape the mutant neutralizes recognition of. Under the mutant, the per-FK loop's `if (!IsFileAttachmentPrincipal(...)) continue;` gate now rejects that FK *before* the property-name comparison the test meant to exercise ever runs — so the test's `Assert.IsFalse` still passed, but by reaching `false` through a different branch than the unmutated code takes. **`run_mutant.py` reported `VERDICT: KILLED`/`GATE: PASS` — clean, because the *red* test still failed correctly.** The gate has no way to see that the green test's own proof was hollow; nothing was loud.

The second case is strictly worse: a coupled positive control that happens to still pass produces no signal at all, while one that fails loudly (#979) at least forces a fix before the entry ships. **Assume every new `green_test` is the #986 shape until you have traced it, not the #979 shape.**

**The check to run before registering an entry**, for every `(patch, green_test)` pair:

1. Read the patch and identify the exact condition or comparison it changes — not just the function name, the specific branch.
2. Read the green test and trace its call path to that condition. If the call graph never reaches the mutated line at all (a different code path, or the mutated function is never called for this input), it is decoupled — done.
3. If it does reach the mutated line, determine whether the mutation can change *that specific invocation's* return value. Two ways to prove it cannot:
   - **Short-circuit**: the surrounding boolean expression's earlier terms already resolve to skip evaluating the mutated term — `fileattachmentguard985-principal-key-check-neutralize` (`!IsCanonicalFileAttachmentPrincipalKey(...) && ... && false`): for the green test's model, `!IsCanonical...` is already `false`, so `&&` never evaluates the mutated `&& false` term at all.
   - **Invariant result**: the mutated and unmutated forms are provably equal for this specific input — `dcext824`'s fix (`IsFileAttachmentForeignKeyProperty_UnrelatedProperty_NonDerivedPrincipal_ReturnsFalse`): `typeof(FileAttachment) == typeof(FileAttachment)` and `typeof(FileAttachment).IsAssignableFrom(typeof(FileAttachment))` are both `true` for an exact-type principal, so the derived-vs-exact-type mutation cannot change this invocation's outcome.
4. If you cannot show either, the test is coupled — pick a different fixture/input, or a different existing test, until you can.
5. `run_mutant.py`'s own `VERDICT: KILLED`/`GATE: PASS` is necessary but **not sufficient** — it only proves the red test still fails and the green test still passes, not *why* the green test passes. Step 3's argument is the part `run_mutant.py` cannot check for you; write it into the entry's `description` so the next reader does not have to re-derive it.

## Verify a guard where it runs, not where you wrote it

Three checks shipped in one day that were sound in the authoring environment and broken in the execution environment: a fixture more permissive than the real API (#967), a CI job invoking a repo-relative script in a job that never checks out (#973), and a script importing PyYAML the runner does not have (#968). Every logic demonstration was valid; every one ran on the author's machine.

Before claiming a check works, ask **what it assumes about its environment that yours happens to satisfy** — and where a dependency can be removed to prove independence, remove it and re-run. `python3 -S` proved the last of those three genuinely does not need PyYAML.

**A missing precondition must exit distinctly, not traceback.** A `ModuleNotFoundError` is indistinguishable from a real analysis failure to anything reading exit codes. Use the repo convention: `0` clean, `1` violation found, `2` could not analyse.

## JS and e2e

- `cd test/WalkingTec.Mvvm.Js.Tests && npm test` — Jest + jsdom over `framework_layui.js` / `framework_analysis.js`. Its `package.json` sets `rootDir: "../.."`, so every path in the jest config is **repo-root-relative**, not test-dir-relative.
- `cd test/e2e && pip install -r requirements.txt && python wtm_e2e_tests.py` (`--list` prints `TC_REGISTRY` without connecting). ETL integration has `test/docker-compose.etl-test.yml`.

## What needs a test here

- New VM logic and Analysis features: unit tests. New controller endpoints: happy path **plus** an auth test.
- ETL connectors: test against mock data sources.
- Performance changes: add or update a benchmark and record before/after numbers — `dotnet run -c Release --project test/WalkingTec.Mvvm.Benchmarks`.
