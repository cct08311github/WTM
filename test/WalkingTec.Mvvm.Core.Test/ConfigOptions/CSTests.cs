#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.ConfigOptions
{
    /// <summary>
    /// Regression tests for #538: CS.Cis / CS.CisFull must publish a fully-populated
    /// static list atomically — never a partially-populated list observed mid-loop by
    /// a concurrent reader.
    /// </summary>
    [TestClass]
    public class CSTests
    {
        /// <summary>
        /// CisFull scans loaded assemblies for DbContext subclasses exposing a
        /// (string, DBTypeEnum) constructor. The test project's own DataContext
        /// (test/WalkingTec.Mvvm.Core.Test/DataContext.cs) declares exactly that
        /// constructor, so the populated list must be non-empty — a basic sanity
        /// check that scanning/population still works after the atomic-publish refactor.
        /// </summary>
        [TestMethod]
        public void CisFull_ReturnsFullyPopulatedNonEmptyList()
        {
            var result = CS.CisFull;

            result.Should().NotBeNull();
            result.Should().NotBeEmpty("CisFull should find at least the test project's DataContext(string, DBTypeEnum) constructor");
            result.Should().OnlyContain(ci => ci != null, "no null entries should ever be published");
        }

        /// <summary>
        /// Cis must never be null and must return the exact same list reference on
        /// repeated calls — proving the static field is populated exactly once and
        /// published atomically (the fix for #538), rather than being recomputed or
        /// observed half-built by a second caller.
        /// </summary>
        [TestMethod]
        public void Cis_RepeatedCalls_ReturnSameStableReference()
        {
            var first = CS.Cis;
            var second = CS.Cis;

            first.Should().NotBeNull();
            ReferenceEquals(first, second).Should().BeTrue(
                "the populated list must be published once and reused, not rebuilt per call");
        }

        /// <summary>
        /// Concurrent first-touch access to CisFull must never observe a partially
        /// populated list — every caller either sees the fully-populated result or
        /// (pre-fix) could have observed the empty list mid-loop. After the #538 fix,
        /// every concurrent caller observes a list of the same, final size.
        /// </summary>
        [TestMethod]
        public async Task CisFull_ConcurrentAccess_AllCallersObserveConsistentSize()
        {
            // Reflection-based population is idempotent and side-effect free to repeat,
            // so hammering it concurrently is safe and exercises the atomic-publish path.
            var tasks = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => CS.CisFull.Count))
                .ToArray();

            var counts = await Task.WhenAll(tasks);

            counts.Should().OnlyContain(c => c == counts[0],
                "every concurrent reader must observe the same fully-populated list size — never a partial one");
            counts[0].Should().BeGreaterThan(0);
        }
    }
}
