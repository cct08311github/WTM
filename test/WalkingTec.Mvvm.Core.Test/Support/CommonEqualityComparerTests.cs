#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Unit tests for <see cref="CommonEqualityComparer{T,V}"/> — Issue #36.
    /// Verifies that Equals delegates to the lambda and GetHashCode follows the spec.
    /// </summary>
    [TestClass]
    public class CommonEqualityComparerTests
    {
        // ─── Equals ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Equals_SameKeyValue_ReturnsTrue()
        {
            var comparer = new CommonEqualityComparer<string, int>(s => s.Length);

            comparer.Equals("abc", "xyz").Should().BeTrue("both have length 3");
        }

        [TestMethod]
        public void Equals_DifferentKeyValue_ReturnsFalse()
        {
            var comparer = new CommonEqualityComparer<string, int>(s => s.Length);

            comparer.Equals("ab", "xyz").Should().BeFalse();
        }

        [TestMethod]
        public void Equals_SameObject_ReturnsTrue()
        {
            var comparer = new CommonEqualityComparer<string, string>(s => s);
            const string val = "hello";

            comparer.Equals(val, val).Should().BeTrue();
        }

        [TestMethod]
        public void Equals_EqualStrings_ReturnsTrue()
        {
            var comparer = new CommonEqualityComparer<string, string>(s => s);

            comparer.Equals("hello", "hello").Should().BeTrue();
        }

        [TestMethod]
        public void Equals_DifferentStrings_ReturnsFalse()
        {
            var comparer = new CommonEqualityComparer<string, string>(s => s);

            comparer.Equals("hello", "world").Should().BeFalse();
        }

        // ─── GetHashCode ────────────────────────────────────────────────────────

        [TestMethod]
        public void GetHashCode_EqualKeys_ProduceSameHash()
        {
            var comparer = new CommonEqualityComparer<string, int>(s => s.Length);

            comparer.GetHashCode("abc").Should().Be(comparer.GetHashCode("xyz"),
                "same key value must produce the same hash");
        }

        [TestMethod]
        public void GetHashCode_DifferentKeys_TypicallyProduceDifferentHashes()
        {
            var comparer = new CommonEqualityComparer<string, int>(s => s.Length);

            // Not a strict requirement (hash collisions are allowed), but length 1 vs 100
            // should almost never collide with int-based hashing.
            comparer.GetHashCode("a").Should().NotBe(comparer.GetHashCode(new string('x', 100)));
        }

        [TestMethod]
        public void GetHashCode_ConsistentAcrossCalls()
        {
            var comparer = new CommonEqualityComparer<string, int>(s => s.Length);
            const string val = "test";

            var h1 = comparer.GetHashCode(val);
            var h2 = comparer.GetHashCode(val);

            h1.Should().Be(h2);
        }

        // ─── Lambda variety ────────────────────────────────────────────────────

        [TestMethod]
        public void Comparer_WithComplexType_WorksCorrectly()
        {
            var comparer = new CommonEqualityComparer<(int Id, string Name), int>(x => x.Id);

            comparer.Equals((1, "Alice"), (1, "Bob")).Should().BeTrue("same Id");
            comparer.Equals((1, "Alice"), (2, "Alice")).Should().BeFalse("different Id");
        }
    }
}
