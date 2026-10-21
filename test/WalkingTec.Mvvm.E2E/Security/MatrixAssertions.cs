using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>The checks the permission matrix tests make on a finished <see cref="MatrixRun"/>.</summary>
    public static class MatrixAssertions
    {
        private const int Shown = 60;

        /// <summary>Every request of an identity got the outcome the table says. Known deviations pass (they are reported), anything else fails.</summary>
        public static void AssertIdentityMatches(MatrixRun run, string identity, TestContext context)
        {
            var cells = run.Cells.Where(c => c.Identity == identity).ToList();
            Assert.IsTrue(cells.Count > 0, $"No requests were made for the identity '{identity}'.");
            foreach (var group in cells.Where(c => c.State == CellStatus.KnownDeviation).GroupBy(c => c.DeviationNote))
            {
                context.WriteLine($"known deviation ({group.Count()} requests of '{identity}', e.g. {group.First().EntryKey}): {group.Key}");
            }
            var problems = cells.Where(c => c.State == CellStatus.Mismatch || c.State == CellStatus.Resolved)
                .Select(c => c.State == CellStatus.Resolved
                    ? $"{Name(c)}: now {c.Observed} as intended - remove its known deviation from the table"
                    : $"{Name(c)}: expected {c.Expected}, got {c.Observed} (HTTP {c.Status})")
                .ToList();
            if (problems.Count > 0)
            {
                Assert.Fail($"{problems.Count} of {cells.Count} requests of '{identity}' differ from the table of {run.Setup.Id}:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, problems.Take(Shown)) + (problems.Count > Shown ? Environment.NewLine + "..." : "")
                    + Environment.NewLine + "The full matrix is in the permission-matrix report; observed-entries.txt there has the observed outcomes in the format of the table.");
            }
        }

        private static string Name(Cell c) => (c.Variant == null ? "" : c.Variant + ": ") + c.EntryKey;

        /// <summary>Every action of the site has an entry: a new endpoint has to say what it is meant to do.</summary>
        public static void AssertEveryActionHasAnEntry(MatrixRun run)
        {
            if (run.MissingEntries.Count > 0)
            {
                Assert.Fail($"{run.MissingEntries.Count} actions of {run.Setup.Id} have no entry in {run.Setup.TableFile}. Add an entry with the intended outcome per identity (see FORK.md, permission matrix):{Environment.NewLine}"
                    + string.Join(Environment.NewLine, run.MissingEntries.Take(Shown)));
            }
        }

        /// <summary>Every entry has an action: removed endpoints do not leave expectations behind.</summary>
        public static void AssertNoStaleEntries(MatrixRun run)
        {
            if (run.StaleEntries.Count > 0)
            {
                Assert.Fail($"{run.StaleEntries.Count} entries of {run.Setup.TableFile} ({run.Setup.Configuration}) have no matching action in {run.Setup.Id}:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, run.StaleEntries.Take(Shown)));
            }
        }

        /// <summary>The table agrees with the rules the action declares (see <see cref="IntentRules"/>), so it does not merely repeat what was observed.</summary>
        public static void AssertTableFollowsDeclaredRules(MatrixRun run)
        {
            if (run.RuleViolations.Count > 0)
            {
                Assert.Fail($"{run.RuleViolations.Count} entries of {run.Setup.TableFile} contradict the rules the action declares. Fix the expectation, or record the observed outcome as a known deviation, or explain the exception in 'exempt':{Environment.NewLine}"
                    + string.Join(Environment.NewLine, run.RuleViolations.Take(Shown)));
            }
        }
    }
}
