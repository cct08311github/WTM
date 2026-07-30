#nullable enable
using System.Security.Cryptography;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Issue #931 item 2: every fixed JWT signing-key literal ever committed to this test
    /// tree is publicly readable on the GitHub mirror — <c>test/</c> is not excluded by
    /// <c>.sync/github-excludes.txt</c> — the same defect class the demo <c>appsettings.json</c>
    /// values were (#923). Cross-vendor review of #923 itself found four such literals still
    /// in the tree, three of them added BY #923's own commits; a repo-wide sweep for the same
    /// pattern (this repo's own security posture: "修完一律全庫掃同一個 pattern") found three
    /// more pre-existing ones. All seven are permanently in <c>JwtOption.KnownPublicKeys</c>
    /// (git history keeps them public regardless of what the tree currently contains) AND no
    /// longer used anywhere — tests that need "a valid strong key" share ONE value from here
    /// instead of each hardcoding their own literal.
    /// </summary>
    internal static class JwtTestKeys
    {
        /// <summary>
        /// A random >= 32-byte key, generated fresh every time the test assembly loads (a
        /// <c>static readonly</c> field initializer runs once per process). Never blocklistable
        /// because it is never committed to source — a different value every run, discarded
        /// when the process exits. Safe to share across every test in this assembly that just
        /// needs "some valid strong key" and does not care what the bytes are.
        /// </summary>
        public static readonly string StrongCustomKey =
            System.Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }
}
