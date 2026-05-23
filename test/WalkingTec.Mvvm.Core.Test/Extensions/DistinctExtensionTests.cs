#nullable enable
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    /// <summary>
    /// Unit tests for <see cref="DistinctExtensions.Distinct{T,V}"/> — Issue #36.
    /// </summary>
    [TestClass]
    public class DistinctExtensionTests
    {
        // ─── Distinct<T, V> ────────────────────────────────────────────────────

        [TestMethod]
        public void Distinct_RemovesDuplicatesByKeySelector()
        {
            var items = new[] { "apple", "apricot", "banana", "blueberry", "cherry" };

            // Distinct by first character — keeps first occurrence per key
            var result = items.Distinct(s => s[0]).ToList();

            result.Should().HaveCount(3);
            result.Should().Contain("apple");
            result.Should().Contain("banana");
            result.Should().Contain("cherry");
            result.Should().NotContain("apricot");
            result.Should().NotContain("blueberry");
        }

        [TestMethod]
        public void Distinct_AllUnique_ReturnsSameCount()
        {
            var items = new[] { "a", "b", "c" };

            var result = items.Distinct(s => s).ToList();

            result.Should().HaveCount(3);
        }

        [TestMethod]
        public void Distinct_AllDuplicates_ReturnsOneElement()
        {
            var items = new[] { "x", "x", "x" };

            var result = items.Distinct(s => s).ToList();

            result.Should().ContainSingle().Which.Should().Be("x");
        }

        [TestMethod]
        public void Distinct_EmptySource_ReturnsEmpty()
        {
            var items = new string[0];

            var result = items.Distinct(s => s).ToList();

            result.Should().BeEmpty();
        }

        [TestMethod]
        public void Distinct_WithIntKey_WorksCorrectly()
        {
            var items = new List<(string Name, int Group)>
            {
                ("Alice",   1),
                ("Bob",     1),
                ("Charlie", 2),
                ("Dave",    2),
                ("Eve",     3),
            };

            var result = items.Distinct(x => x.Group).ToList();

            result.Should().HaveCount(3);
            result.Select(x => x.Group).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        }
    }
}
