#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Issue #824 architecture note (recorded here, and in <c>.claude/rules/security-invariants.md</c>):
    /// <c>SaveChanges</c>/<c>SaveChangesAsync</c> are NOT "the one point all writes pass through" —
    /// EF Core's <c>ExecuteUpdate</c>/<c>ExecuteUpdateAsync</c>/<c>ExecuteDelete</c>/
    /// <c>ExecuteDeleteAsync</c> compile straight to a SQL UPDATE/DELETE against the database and
    /// bypass the change tracker — and therefore <c>SaveChanges</c> — entirely. Any future
    /// SaveChanges-boundary interceptor for FileAttachment FK writes (the architecture direction
    /// Issue #824 recommends but this PR does NOT implement — see the PR description for what
    /// remains out of scope) would silently miss an <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> call
    /// that touches <see cref="FileAttachment"/>.
    ///
    /// <para>
    /// Today's actual exposure from that gap is zero: a full-tree audit (2026-07-28, the audit
    /// this test codifies) found every <c>ExecuteUpdate*</c>/<c>ExecuteDelete*</c> call under
    /// <c>src/</c> belongs to ETL scheduling (<c>WalkingTec.Mvvm.Etl</c>), the WorkFlow engine's
    /// guarded state transitions (<c>WalkingTec.Mvvm.WorkFlow</c>), saved-query/dashboard-widget
    /// deletion (<c>_AnalysisController.cs</c>), or ChangeLog/ActionLog/RefreshToken retention and
    /// rotation (<c>WalkingTec.Mvvm.Core.Support</c>, <c>TokenService.cs</c>) — none of them query
    /// or mutate <see cref="FileAttachment"/>.
    /// </para>
    ///
    /// <para>
    /// Issue #824's own review comment on this exact gap offers two options, not an instruction:
    /// either additionally intercept the bulk API, or honestly limit the scope in the
    /// documentation. It does not mandate either one. This PR takes the second option — rather
    /// than build a second enforcement mechanism (an interceptor) to guard an exposure that does
    /// not exist today, it records the invariant as a regression guard instead: this test fails
    /// the moment anyone adds an <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> call whose enclosing
    /// LINQ query statement mentions <see cref="FileAttachment"/>, so the gap cannot silently
    /// reopen while no interceptor exists to close it structurally. This is a deliberately narrow,
    /// literal source-text scan (a "grep test") — not a semantic/Roslyn analysis. Choosing this
    /// option over building the interceptor was made for this PR, not dictated by the issue, and
    /// it does not close out Issue #824's own recommended fix (a SaveChanges-boundary guard, still
    /// outstanding — see the remarks above and the PR description).
    /// </para>
    ///
    /// <para>
    /// <b>#857 — known scope limit, stated honestly rather than left implicit:</b> the statement-
    /// boundary walk-back (<see cref="LastStatementBoundary"/>) only looks backward from the
    /// <c>.ExecuteUpdate</c>/<c>.ExecuteDelete</c> call to the nearest preceding <c>;</c> or
    /// <c>{</c> — i.e. it only catches <see cref="FileAttachment"/> and the bulk call appearing in
    /// the SAME source statement. A query assigned to a local first and mutated on a later,
    /// separate statement evades it entirely:
    /// <code>
    /// var files = DC.Set&lt;FileAttachment&gt;();
    /// files.ExecuteDeleteAsync();
    /// </code>
    /// contains no single statement whose text mentions both <c>FileAttachment</c> and
    /// <c>ExecuteDelete</c>, so this test would not flag it. This is not a false invariant — the
    /// test genuinely fails whenever a real violation is written the direct, same-statement way,
    /// and the sanity-check test below proves the detector actually fires — but it is a literal
    /// source-text scan, not a data/alias-flow analysis, and must not be read as a complete proof
    /// that no <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> call anywhere in <c>src/</c> can reach
    /// <see cref="FileAttachment"/> through an intermediate variable. See the same caveat recorded
    /// in <c>docs/production-readiness.md</c>.
    /// </para>
    /// </summary>
    [TestClass]
    public class ExecuteUpdateDeleteFileAttachmentInvariantTests
    {
        private static readonly Regex ExecuteUpdateOrDeletePattern = new(
            @"\.Execute(Update|Delete)(Async)?\s*\(",
            RegexOptions.Compiled);

        /// <summary>
        /// #824 invariant: no <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> call site under
        /// <c>src/</c> may belong to a LINQ statement that touches <see cref="FileAttachment"/>
        /// — see the class-level remarks for why (these calls bypass every SaveChanges-boundary
        /// FileAttachment FK gate, present or future).
        /// </summary>
        [TestMethod]
        public void NoExecuteUpdateOrExecuteDeleteStatementTouchesFileAttachment()
        {
            var srcDir = FindSrcDir();
            var offenders = new List<string>();

            foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match match in ExecuteUpdateOrDeletePattern.Matches(text))
                {
                    // Walk back to the start of the enclosing statement -- the nearest preceding
                    // ';' or block-opening '{' -- so a multi-line fluent LINQ chain (the normal
                    // shape for these calls; e.g. WorkflowEngine.Approve.cs) is captured whole,
                    // including its own Set<T>()/DbSet<T> property access that names the entity
                    // actually being queried.
                    var statementStart = LastStatementBoundary(text, match.Index);
                    var statement = text[statementStart..match.Index];

                    if (statement.Contains("FileAttachment", StringComparison.Ordinal))
                    {
                        var line = text[..match.Index].Count(c => c == '\n') + 1;
                        offenders.Add($"{Path.GetRelativePath(srcDir, file)}:{line}");
                    }
                }
            }

            Assert.AreEqual(0, offenders.Count,
                "#824 invariant violated: ExecuteUpdate/ExecuteDelete bypass SaveChanges and " +
                "therefore bypass every FileAttachment FK write-path gate (#815's " +
                "RejectUnresolvableFileAttachmentReferences, #824 Part 1's UpdateModelProperty " +
                "guard, and any future SaveChanges-boundary interceptor). A query statement " +
                "touching FileAttachment must never reach ExecuteUpdate/ExecuteDelete without " +
                "going through the same review as any other FileAttachment write-path sink -- " +
                "see .claude/rules/security-invariants.md. " +
                $"Offending call sites: {string.Join(", ", offenders)}");
        }

        /// <summary>
        /// Sanity check: the regex/statement-boundary walk-back above must actually be capable of
        /// firing, or the invariant test above would be vacuously green forever regardless of
        /// whether the invariant holds. Exercises both helpers directly against an in-memory
        /// fixture string containing a deliberate violation.
        /// </summary>
        [TestMethod]
        public void DetectionLogic_FindsADeliberateViolation_InFixtureText()
        {
            const string fixture = @"
                await DC.Set<FileAttachment>()
                    .Where(x => x.ID == id)
                    .ExecuteDeleteAsync(ct);
";
            var match = ExecuteUpdateOrDeletePattern.Match(fixture);
            Assert.IsTrue(match.Success, "Sanity check: the regex must match a real ExecuteDeleteAsync( call.");

            var statementStart = LastStatementBoundary(fixture, match.Index);
            var statement = fixture[statementStart..match.Index];
            Assert.IsTrue(statement.Contains("FileAttachment", StringComparison.Ordinal),
                "Sanity check: the statement-boundary walk-back must capture the preceding " +
                "Set<FileAttachment>() call, or the real test above would never be able to detect " +
                "a genuine violation.");
        }

        private static int LastStatementBoundary(string text, int beforeIndex)
        {
            var searchFrom = beforeIndex - 1;
            if (searchFrom < 0)
            {
                return 0;
            }
            var semi = text.LastIndexOf(';', searchFrom);
            var brace = text.LastIndexOf('{', searchFrom);
            var boundary = Math.Max(semi, brace);
            return boundary < 0 ? 0 : boundary + 1;
        }

        private static string FindSrcDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WalkingTec.Mvvm.sln")))
            {
                dir = dir.Parent;
            }
            if (dir == null)
            {
                throw new InvalidOperationException(
                    "Cannot locate repo root -- WalkingTec.Mvvm.sln not found in any parent directory");
            }
            var srcDir = Path.Combine(dir.FullName, "src");
            if (!Directory.Exists(srcDir))
            {
                throw new InvalidOperationException($"src directory not found: {srcDir}");
            }
            return srcDir;
        }
    }
}
