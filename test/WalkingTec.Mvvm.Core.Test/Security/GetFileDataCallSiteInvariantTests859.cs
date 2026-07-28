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
    /// Issue #859 follow-up: <c>WtmDataBaseFileHandler.GetFileData</c> was fixed to call
    /// <c>IgnoreQueryFilters()</c> — trusting the id it is given rather than re-applying its own,
    /// independent tenant filter — specifically <i>because</i> the only call site in this repo,
    /// <c>WtmFileProvider.GetFile</c> (<c>WtmFileProvider.cs:180</c>, inside
    /// <c>if (rv != null &amp;&amp; withData == true)</c>), only ever reaches it AFTER its own
    /// query has already resolved — and, per <c>FileUploadOptions.EnforceTenantFileScope</c>,
    /// already tenant-scoped — the row being read. <c>WtmLocalFileHandler</c>/<c>WtmOssFileHandler</c>
    /// apply no second filter of their own, so <c>WtmDataBaseFileHandler</c> trusting the same way
    /// makes all three handlers consistent, not less safe.
    ///
    /// <para>
    /// That safety argument rests entirely on a property nothing in the type system enforces:
    /// that <c>GetFileData</c> is reached ONLY downstream of <c>WtmFileProvider.GetFile</c>'s
    /// authorization decision. This is true today (confirmed by the full-repo scan this test
    /// automates) but is a snapshot, not an invariant — the moment a second call site appears
    /// that takes a caller-controlled id and does not route through
    /// <c>WtmFileProvider.GetFile</c>'s flag-aware query first, <c>IgnoreQueryFilters()</c> inside
    /// <c>WtmDataBaseFileHandler.GetFileData</c> becomes exactly the unauthenticated cross-tenant
    /// read Issue #859 exists to close — reintroduced silently, with no compiler warning and no
    /// obvious link back to this PR's reasoning for anyone who adds it.
    /// </para>
    ///
    /// <para>
    /// This test pins the property as a regression guard: it scans every <c>.cs</c> file under
    /// <c>src/</c> and <c>demo/</c> for an INVOCATION of <c>GetFileData</c> (a literal
    /// <c>.GetFileData(</c> — this deliberately does not match a method declaration/signature
    /// line, which never has a leading <c>.</c>) and asserts every such invocation lives in one of
    /// two places: <see cref="WalkingTec.Mvvm.Core.Support.FileHandlers.WtmFileProvider"/> itself
    /// (the one caller this safety argument is built on), or one of the
    /// <c>IWtmFileHandler</c> implementations (<c>WtmDataBaseFileHandler</c>,
    /// <c>WtmLocalFileHandler</c>, <c>WtmOssFileHandler</c>, <c>WtmS3FileHandler</c>,
    /// <c>WtmFileHandlerBase</c>) — allow-listed in case a future handler legitimately delegates
    /// to another handler or to its own base implementation, which is a within-the-handler-family
    /// call, not a new external entry point.
    /// </para>
    ///
    /// <para>
    /// <b>Honest limitation</b> (same spirit as
    /// <see cref="ExecuteUpdateDeleteFileAttachmentInvariantTests"/>'s own remarks): this is a
    /// deliberately narrow, literal source-text scan — not a semantic/Roslyn analysis, and not a
    /// dataflow proof. It genuinely does catch the shape of alias the reviewer flagged as the
    /// obvious risk — <c>var h = CreateFileHandler(...); h.GetFileData(untrustedId);</c> two
    /// statements apart still contains the literal substring <c>.GetFileData(</c> at the actual
    /// call site regardless of what <c>h</c> was assigned from or how far back — because, unlike
    /// the ExecuteUpdate/ExecuteDelete invariant test, this scan does not need to correlate the
    /// call with an entity name mentioned in a preceding statement; the mere presence of the call
    /// in a non-allow-listed file is itself the violation. What a literal scan genuinely CANNOT
    /// catch: a bare method-group/delegate reference with no adjacent open-parenthesis at the
    /// point of interest (<c>var d = new Func&lt;IWtmFile, Stream?&gt;(fh.GetFileData); ...
    /// d.Invoke(untrustedFile);</c> — the eventual invocation is <c>d.Invoke(...)</c>, which does
    /// not contain the text <c>.GetFileData(</c> at all), or a reflection-based call
    /// (<c>typeof(WtmDataBaseFileHandler).GetMethod("GetFileData").Invoke(...)</c>). If either
    /// shows up in review, treat it with the same suspicion this test exists to raise for a
    /// plain call — this test failing to catch it is not evidence it is safe.
    /// </para>
    /// </summary>
    [TestClass]
    public class GetFileDataCallSiteInvariantTests859
    {
        private static readonly Regex GetFileDataInvocationPattern = new(
            @"\.GetFileData\s*\(",
            RegexOptions.Compiled);

        /// <summary>
        /// Repo-root-relative paths (forward-slash separated) allowed to contain a
        /// <c>.GetFileData(</c> invocation: the one caller this PR's safety argument depends on,
        /// plus every <c>IWtmFileHandler</c> implementation (in case one delegates to another, or
        /// to <c>base.GetFileData(...)</c>).
        /// </summary>
        private static readonly HashSet<string> AllowedCallSiteFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmFileProvider.cs",
            "src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmDataBaseFileHandler.cs",
            "src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmLocalFileHandler.cs",
            "src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmOssFileHandler.cs",
            "src/WalkingTec.Mvvm.Core/Support/FileHandlers/WtmFileHandlerBase.cs",
            "src/WalkingTec.Mvvm.FileHandlers.S3/WtmS3FileHandler.cs",
        };

        /// <summary>
        /// #859 invariant: no file under <c>src/</c> or <c>demo/</c>, other than the allow-listed
        /// caller/handler files above, may invoke <c>GetFileData</c> — see the class-level remarks
        /// for why this specific call is unusually dangerous to let a new caller reach directly.
        /// </summary>
        [TestMethod]
        public void NoCallSiteInvokesGetFileDataOutsideProviderOrHandlers()
        {
            var repoRoot = FindRepoRoot();
            var offenders = new List<string>();

            foreach (var scanDir in new[] { "src", "demo" })
            {
                var dir = Path.Combine(repoRoot, scanDir);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    // Skip build output / publish artifacts — never a real source-of-truth call
                    // site, and (per #859's own investigation) sometimes contain stale untracked
                    // copies of whole projects.
                    var normalized = file.Replace(Path.DirectorySeparatorChar, '/');
                    if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                        || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                        || normalized.Contains("/publish/", StringComparison.OrdinalIgnoreCase)
                        || normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    if (!GetFileDataInvocationPattern.IsMatch(text))
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (AllowedCallSiteFiles.Contains(relativePath))
                    {
                        continue;
                    }

                    foreach (Match match in GetFileDataInvocationPattern.Matches(text))
                    {
                        var line = text[..match.Index].Count(c => c == '\n') + 1;
                        offenders.Add($"{relativePath}:{line}");
                    }
                }
            }

            Assert.AreEqual(0, offenders.Count,
                "#859 invariant violated: a new GetFileData( call site appeared outside " +
                "WtmFileProvider and the IWtmFileHandler implementations. " +
                "WtmDataBaseFileHandler.GetFileData calls IgnoreQueryFilters() and is safe ONLY " +
                "because its one known caller, WtmFileProvider.GetFile, has already resolved " +
                "(and, per FileUploadOptions.EnforceTenantFileScope, already tenant-scoped) the " +
                "row before calling it. A new caller that takes a caller-controlled id and does " +
                "not go through WtmFileProvider.GetFile first would silently reintroduce the " +
                "unauthenticated cross-tenant file read Issue #859 closed. Either route the new " +
                "call through WtmFileProvider.GetFile, or make WtmDataBaseFileHandler.GetFileData " +
                "stop ignoring query filters and re-derive its own authorization decision. " +
                $"Offending call sites: {string.Join(", ", offenders)}");
        }

        /// <summary>
        /// Sanity check: the regex above must actually be capable of firing on a real invocation
        /// (including one written two statements apart, as a reviewer flagged as the obvious risk
        /// shape), or the invariant test would be vacuously green forever regardless of whether a
        /// new call site ever appears.
        /// </summary>
        [TestMethod]
        public void DetectionLogic_FindsADeliberateViolation_InFixtureText()
        {
            const string fixture = @"
                var h = someFactory.CreateFileHandler(saveMode, dc);
                var data = h.GetFileData(untrustedFile);
";
            Assert.IsTrue(GetFileDataInvocationPattern.IsMatch(fixture),
                "Sanity check: the regex must match a real GetFileData( invocation, even one " +
                "written on a variable assigned two statements earlier — otherwise the real test " +
                "above could never detect a genuine new call site.");
        }

        /// <summary>
        /// Sanity check: a plain method declaration/signature — never a leading <c>.</c> before
        /// <c>GetFileData</c> — must NOT match, or every <c>IWtmFileHandler</c> implementation's
        /// own method signature would falsely count as an "invocation" and the allow-list logic
        /// above would never be exercised meaningfully.
        /// </summary>
        [TestMethod]
        public void DetectionLogic_DoesNotMatchAPlainDeclaration()
        {
            const string fixture = @"
                public override Stream? GetFileData(IWtmFile file)
                {
";
            Assert.IsFalse(GetFileDataInvocationPattern.IsMatch(fixture),
                "Sanity check: a method declaration (no leading '.') must not match the " +
                "invocation pattern, or this test would flag every IWtmFileHandler " +
                "implementation's own signature as a false violation.");
        }

        private static string FindRepoRoot()
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
            return dir.FullName;
        }
    }
}
