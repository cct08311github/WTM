using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #855 (mutation-gate integrity) fixture. This class exists ONLY to give
/// test/mutants/entries/_selftest-baseline-not-green-invalid.json and
/// _selftest-undeclared-trx-failure-invalid.json a test method that is not "Passed" when
/// explicitly selected by name, without ever affecting an ordinary full `dotnet test` run
/// or any real mutant's verdict.
///
/// <see cref="AlwaysFails_MutationGateBaselineSelftestFixture"/> is marked
/// <see cref="IgnoreAttribute"/>, which MSTest/VSTest honours even under an explicit
/// <c>--filter FullyQualifiedName=...</c> selection naming it directly: the test is
/// reported "Skipped" (never "Passed", and its body is never executed), both in a normal
/// unfiltered `dotnet test` pass (where it is invisible -- Skipped tests do not fail a
/// run) and when run_mutant.py's own <c>--filter</c> targets it explicitly. That "Skipped
/// != Passed" outcome is exactly what test/mutants/run_mutant.py's baseline check
/// (issue #855 defect 3) and its undeclared-TRX-failure check (issue #855 defect 3,
/// second half) need in order to demonstrate rejecting, respectively: a red test that is
/// not green before any mutant patch is applied, and an undeclared test elsewhere in the
/// same TRX that also did not pass. Never remove the <see cref="IgnoreAttribute"/> --
/// doing so would make this method actually execute (and fail) as part of every ordinary
/// test run.
/// </summary>
[TestClass]
public class MutationGateSelftestFixtures
{
    [TestMethod]
    [Ignore("Issue #855: intentionally always-failing fixture for the mutation-gate " +
        "runner's own selftests (baseline-not-green, undeclared-TRX-failure). Must " +
        "never run as part of a normal test pass -- do not remove this attribute.")]
    public void AlwaysFails_MutationGateBaselineSelftestFixture()
    {
        Assert.Fail(
            "#855: this test must never actually execute under a normal `dotnet test` " +
            "run. If you see this failure, the [Ignore] attribute above was removed by " +
            "mistake -- restore it.");
    }
}
