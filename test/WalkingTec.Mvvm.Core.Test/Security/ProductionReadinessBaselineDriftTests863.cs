#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Issue #863: <c>docs/production-readiness.md</c> is CLAUDE.md's Red Line comparison
    /// baseline -- "commit message／PR body／CHANGELOG 的宣稱強度不得超過同一變更在
    /// docs/production-readiness.md 的條目". That only works if the baseline itself stays
    /// accurate. #863 found the opposite direction of drift from the usual "code overclaims,
    /// docs stay honest" pattern this repo has hit before: the doc's own
    /// "新揭露的缺口（未修，已立案）" ("newly-disclosed gaps -- unfixed, tracked") section still
    /// listed #721/#722/#696 two days after all three were closed, and one of the affected lines
    /// (the 場景適用矩陣's JWT-refresh-flow caveat) told adopters to go verify a problem that no
    /// longer existed.
    ///
    /// <para>
    /// <b>Mechanism choice:</b> the issue's own suggestion -- extract every <c>#N</c> reference
    /// from the doc, query the internal infrastructure API, fail if one listed as unfixed is closed -- is
    /// implemented here, not replaced. A live network call in a unit test is unusual, but the
    /// alternative (a periodically-refreshed local cache of issue states) only moves the same
    /// staleness problem one level down: the cache itself would need the exact same "did anyone
    /// remember to update this" discipline #863 exists because nobody had. A live check has
    /// exactly one failure mode (network/auth unavailable) and it is handled explicitly below
    /// (skip, not fail, not silently pass) rather than inherited invisibly. This repo's internal infrastructure
    /// instance also does not require authentication for a read on this specific, public repo's
    /// issue endpoints (verified 2026-07-29 with a bare unauthenticated GET returning 200 with
    /// the correct <c>state</c>), so a missing token narrows what this test can prove (it will
    /// still attach one if found, for rate-limit headroom and to keep working if the repo's
    /// visibility ever changes) but does not by itself make the check impossible the way a
    /// missing network path does.
    /// </para>
    ///
    /// <para>
    /// <b>Three tests, two different reasons to run without hitting the network:</b>
    /// <see cref="TheUnfixedGapsHeading_StillExistsInTheDoc"/> and
    /// <see cref="DetectionLogic_ExtractsIssueNumbers_FromFixtureSection"/> are pure text/regex
    /// checks against, respectively, the real file and an in-memory fixture -- they always run in
    /// CI and catch the doc's heading being silently renamed/removed (which would otherwise make
    /// the live check below vacuously find zero issues to verify and pass for the wrong reason)
    /// and catch the extraction regex itself breaking. Only
    /// <see cref="NoIssueListedAsUnfixed_InProductionReadinessDoc_IsActuallyClosed"/> touches the
    /// network, and it is the only one of the three that can legitimately go Inconclusive.
    /// </para>
    /// </summary>
    [TestClass]
    public class ProductionReadinessBaselineDriftTests863
    {
        private const string SectionHeadingMarker = "新揭露的缺口（未修，已立案）";
        // The doc's next bold-header line after the gaps section, as of the 2026-07 rewrite --
        // used only to bound the extraction window; if this text ever changes along with a
        // genuine section reorder, TheUnfixedGapsHeading_StillExistsInTheDoc (which only checks
        // SectionHeadingMarker) keeps passing while ExtractUnfixedGapsSection's fallback (see
        // below) still finds a bounded window, so this drifting is not a silent-blindspot risk.
        private const string SectionEndMarker = "未解的硬化 epic";
        // Matches only a bullet's OWN leading issue number ("- **#721...") -- not every #N
        // mention inside the section. The doc's bullets routinely cite OTHER issues in their body
        // text as provenance (e.g. the #721 bullet says "由本批次新增的 live e2e（#681 tc_32）才發現"),
        // and #681 being closed says nothing about whether #721 itself is still an open gap. An
        // unqualified `#(\d+)` scan empirically pulled in exactly this case (#681, closed, cited
        // inside the #721 bullet) as a false positive during this test's own development.
        private static readonly Regex IssueRefPattern = new(@"(?m)^-\s*\*\*#(\d+)", RegexOptions.Compiled);
        private const string GiteaApiBase = "https://internal.registry.invalid/api/v1";
        private const string RepoOwner = "chiu0831";
        private const string RepoName = "WTM";

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

        private static string ReadProductionReadinessDoc()
        {
            var path = Path.Combine(FindRepoRoot(), "docs", "production-readiness.md");
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"docs/production-readiness.md not found at '{path}'.");
            }
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Extracts the "新揭露的缺口（未修，已立案）" section's own text -- from the heading marker
        /// to the next bold-header line (falling back to end-of-document if that marker has moved
        /// or been renamed, rather than silently matching nothing) -- and returns every distinct
        /// <c>#N</c> issue number referenced inside it. Only issues named INSIDE this specific
        /// section count as "the doc claims this is unfixed"; the other ~35 issue references
        /// scattered through the rest of the file describe fixed/historical work and must not be
        /// swept into the same check (most of them are legitimately closed).
        /// </summary>
        internal static List<int> ExtractUnfixedGapsSectionIssueNumbers(string docText)
        {
            var headingIndex = docText.IndexOf(SectionHeadingMarker, StringComparison.Ordinal);
            if (headingIndex < 0)
            {
                return new List<int>();
            }
            var sectionStart = headingIndex + SectionHeadingMarker.Length;
            var endIndex = docText.IndexOf(SectionEndMarker, sectionStart, StringComparison.Ordinal);
            var section = endIndex > sectionStart
                ? docText[sectionStart..endIndex]
                : docText[sectionStart..]; // fallback: marker moved/renamed -- scan to EOF rather than match nothing.

            return IssueRefPattern.Matches(section)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }

        /// <summary>
        /// Structural guard, no network: if this heading text is ever renamed or the section
        /// removed without updating <see cref="SectionHeadingMarker"/> here, the live check below
        /// would silently find zero issue numbers and report "no violations" for the wrong reason
        /// -- indistinguishable from a genuinely clean doc. This test fails loudly instead.
        /// </summary>
        [TestMethod]
        public void TheUnfixedGapsHeading_StillExistsInTheDoc()
        {
            var docText = ReadProductionReadinessDoc();
            Assert.IsTrue(docText.Contains(SectionHeadingMarker, StringComparison.Ordinal),
                $"#863: docs/production-readiness.md no longer contains the heading " +
                $"'{SectionHeadingMarker}' this test's extraction logic depends on. If the section " +
                "was intentionally renamed or restructured, update SectionHeadingMarker (and " +
                "SectionEndMarker) in ProductionReadinessBaselineDriftTests863 to match -- do not " +
                "just delete this test, or the live drift check below silently stops checking " +
                "anything.");
        }

        /// <summary>
        /// Sanity check (no network): proves the extraction regex/section-bounding logic can
        /// actually find issue numbers, against a deliberate in-memory fixture -- mirrors
        /// ExecuteUpdateDeleteFileAttachmentInvariantTests.DetectionLogic_FindsADeliberateViolation_InFixtureText's
        /// rationale (a detector that can never fire is worse than no detector, because it looks
        /// like coverage).
        /// </summary>
        [TestMethod]
        public void DetectionLogic_ExtractsIssueNumbers_FromFixtureSection()
        {
            const string fixture = @"
## 安全姿態

**強化（已合併）**：
- some fixed thing referencing #100, not relevant here

**新揭露的缺口（未修，已立案）**：
- **#721（P0/P1，安全）**：some description, discovered by live e2e (#681 tc_32) -- #681 is a
  citation inside this bullet's own text, not a second unfixed-gap claim, and must NOT be picked up.
- **#722（layui）**：another description mentioning #721 again
- **#696（security/sync）**：a third one

**未解的硬化 epic**：
- **#470 / #567**：this must NOT be picked up, it is past the section end marker
";
            var found = ExtractUnfixedGapsSectionIssueNumbers(fixture);

            CollectionAssert.AreEqual(new[] { 696, 721, 722 }, found,
                $"#863: expected exactly {{696, 721, 722}} (deduped, sorted) from the fixture's " +
                $"unfixed-gaps section -- #681 (an in-body citation, not a bullet's own leading " +
                $"issue) and #470/#567 (past the section end marker) must both be excluded. " +
                $"Got: [{string.Join(", ", found)}].");
        }

        /// <summary>
        /// The live #863 enforcement: every issue number the doc's "未修，已立案" section names
        /// must actually still be open on internal infrastructure. Skips (Assert.Inconclusive -- visible in the test
        /// run output as its own outcome, distinct from Passed) rather than failing or silently
        /// passing when the API is unreachable, since neither "the network is down" nor "no token
        /// is configured" says anything about whether the doc has drifted.
        /// </summary>
        [TestMethod]
        public async Task NoIssueListedAsUnfixed_InProductionReadinessDoc_IsActuallyClosed()
        {
            var issueNumbers = ExtractUnfixedGapsSectionIssueNumbers(ReadProductionReadinessDoc());
            if (issueNumbers.Count == 0)
            {
                // TheUnfixedGapsHeading_StillExistsInTheDoc already guards against this being
                // caused by a renamed/missing heading; a genuinely empty section (every
                // previously-disclosed gap fixed, none new yet) is a legitimate state and there is
                // nothing to check against the API.
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var token = ResolveGiteaToken();
            if (!string.IsNullOrEmpty(token))
            {
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);
            }

            var closedButClaimedUnfixed = new List<string>();
            foreach (var issueNumber in issueNumbers)
            {
                string? state;
                try
                {
                    state = await FetchIssueStateAsync(http, issueNumber, CancellationToken.None);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
                {
                    Assert.Inconclusive(
                        $"#863: skipped -- could not reach the internal infrastructure API to verify issue #{issueNumber} " +
                        $"(no network path to {GiteaApiBase}, or the request timed out/was refused). " +
                        $"This is an environment limitation, not a passing or failing result for the " +
                        $"doc-drift check itself. Underlying error: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                if (state == null)
                {
                    Assert.Inconclusive(
                        $"#863: skipped -- internal infrastructure API request for issue #{issueNumber} did not return a " +
                        "usable 'state' field (auth required and no/invalid token configured, the API " +
                        "shape changed, or a non-success status was returned). This is an environment " +
                        "limitation, not a passing or failing result for the doc-drift check itself.");
                    return;
                }

                if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase))
                {
                    closedButClaimedUnfixed.Add($"#{issueNumber}");
                }
            }

            Assert.AreEqual(0, closedButClaimedUnfixed.Count,
                "#863: docs/production-readiness.md's '新揭露的缺口（未修，已立案）' section lists " +
                $"{string.Join(", ", closedButClaimedUnfixed)} as unfixed, but internal infrastructure reports " +
                $"{(closedButClaimedUnfixed.Count == 1 ? "it is" : "they are")} closed. This is the " +
                "Red Line's own comparison baseline drifting -- update the doc (remove/rewrite the " +
                "stale entry) so it matches reality.");
        }

        private static async Task<string?> FetchIssueStateAsync(HttpClient http, int issueNumber, CancellationToken ct)
        {
            var url = $"{GiteaApiBase}/repos/{RepoOwner}/{RepoName}/issues/{issueNumber}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.String)
            {
                return stateProp.GetString();
            }
            return null;
        }

        /// <summary>
        /// Prefers the REGISTRY_TOKEN environment variable (the portable, CI-friendly form -- a
        /// secret injected by the runner, not a path on disk that may not exist in that
        /// environment) and falls back to this maintainer's local convention (.local-token-file,
        /// documented in the gitea-issue-workflow skill) for a same-token experience when run by
        /// hand. Returns null (not throws) when neither is present -- the caller treats an absent
        /// token as "proceed unauthenticated", not as a reason to skip, since this repo's issue
        /// reads do not require one (see class remarks).
        /// </summary>
        private static string? ResolveGiteaToken()
        {
            var envToken = Environment.GetEnvironmentVariable("REGISTRY_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken))
            {
                return envToken.Trim();
            }
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var tokenFile = Path.Combine(home, ".gitea-token");
                if (File.Exists(tokenFile))
                {
                    var match = Regex.Match(File.ReadAllText(tokenFile), "[a-f0-9]{40}");
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
            }
            catch
            {
                // Best-effort convenience path only -- any failure here (permissions, missing
                // home dir in a container, etc.) just means proceeding unauthenticated.
            }
            return null;
        }
    }
}
