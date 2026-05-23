#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Auth;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    [TestClass]
    public class ClaimComparerTests
    {
        // ─── Constructor ──────────────────────────────────────────────────────

        [TestMethod]
        public void Constructor_Default_CreatesWithDefaultOptions()
        {
            var comparer = new ClaimComparer();
            comparer.Should().NotBeNull();
        }

        [TestMethod]
        public void Constructor_WithOptions_AcceptsOptions()
        {
            var options = new ClaimComparer.Options { IgnoreIssuer = true, IgnoreValueCase = true };
            var comparer = new ClaimComparer(options);
            comparer.Should().NotBeNull();
        }

        [TestMethod]
        public void Constructor_NullOptions_Throws()
        {
            Action act = () => new ClaimComparer(null!);
            act.Should().Throw<ArgumentNullException>();
        }

        // ─── Equals: null handling ─────────────────────────────────────────────

        [TestMethod]
        public void Equals_BothNull_ReturnsTrue()
        {
            var comparer = new ClaimComparer();
            comparer.Equals(null, null).Should().BeTrue();
        }

        [TestMethod]
        public void Equals_XNull_ReturnsFalse()
        {
            var comparer = new ClaimComparer();
            var claim = new Claim("type", "value");
            comparer.Equals(null, claim).Should().BeFalse();
        }

        [TestMethod]
        public void Equals_YNull_ReturnsFalse()
        {
            var comparer = new ClaimComparer();
            var claim = new Claim("type", "value");
            comparer.Equals(claim, null).Should().BeFalse();
        }

        // ─── Equals: default options (case-sensitive values, include issuer) ──

        [TestMethod]
        public void Equals_SameTypeSameValueSameIssuer_ReturnsTrue()
        {
            var comparer = new ClaimComparer();
            var x = new Claim("type", "value", ClaimValueTypes.String, "issuer");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuer");
            comparer.Equals(x, y).Should().BeTrue();
        }

        [TestMethod]
        public void Equals_DifferentType_ReturnsFalse()
        {
            var comparer = new ClaimComparer();
            var x = new Claim("typeA", "value");
            var y = new Claim("typeB", "value");
            comparer.Equals(x, y).Should().BeFalse();
        }

        [TestMethod]
        public void Equals_TypeIsCaseInsensitive()
        {
            var comparer = new ClaimComparer();
            var x = new Claim("TYPE", "value");
            var y = new Claim("type", "value");
            // Type is compared OrdinalIgnoreCase
            comparer.Equals(x, y).Should().BeTrue();
        }

        [TestMethod]
        public void Equals_DifferentValue_ReturnsFalse()
        {
            var comparer = new ClaimComparer();
            var x = new Claim("type", "valueA");
            var y = new Claim("type", "valueB");
            comparer.Equals(x, y).Should().BeFalse();
        }

        [TestMethod]
        public void Equals_ValueCaseSensitiveByDefault()
        {
            var comparer = new ClaimComparer();
            var x = new Claim("type", "Value");
            var y = new Claim("type", "value");
            comparer.Equals(x, y).Should().BeFalse();
        }

        [TestMethod]
        public void Equals_DifferentIssuer_ReturnsFalse()
        {
            var comparer = new ClaimComparer();
            var x = new Claim("type", "value", ClaimValueTypes.String, "issuerA");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuerB");
            comparer.Equals(x, y).Should().BeFalse();
        }

        // ─── Equals: IgnoreIssuer option ──────────────────────────────────────

        [TestMethod]
        public void Equals_IgnoreIssuer_DifferentIssuers_ReturnsTrue()
        {
            var comparer = new ClaimComparer(new ClaimComparer.Options { IgnoreIssuer = true });
            var x = new Claim("type", "value", ClaimValueTypes.String, "issuerA");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuerB");
            comparer.Equals(x, y).Should().BeTrue();
        }

        // ─── Equals: IgnoreValueCase option ──────────────────────────────────

        [TestMethod]
        public void Equals_IgnoreValueCase_DifferentCases_ReturnsTrue()
        {
            var comparer = new ClaimComparer(new ClaimComparer.Options { IgnoreValueCase = true });
            var x = new Claim("type", "Value", ClaimValueTypes.String, "issuer");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuer");
            comparer.Equals(x, y).Should().BeTrue();
        }

        [TestMethod]
        public void Equals_IgnoreValueCase_IssuerCaseInsensitive()
        {
            var comparer = new ClaimComparer(new ClaimComparer.Options { IgnoreValueCase = true });
            var x = new Claim("type", "value", ClaimValueTypes.String, "ISSUER");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuer");
            comparer.Equals(x, y).Should().BeTrue();
        }

        // ─── Equals: both options together ────────────────────────────────────

        [TestMethod]
        public void Equals_IgnoreIssuerAndIgnoreValueCase_FlexibleMatch()
        {
            var comparer = new ClaimComparer(new ClaimComparer.Options
            {
                IgnoreIssuer = true,
                IgnoreValueCase = true
            });
            var x = new Claim("TYPE", "VALUE", ClaimValueTypes.String, "issuerA");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuerB");
            comparer.Equals(x, y).Should().BeTrue();
        }

        // ─── GetHashCode ──────────────────────────────────────────────────────

        [TestMethod]
        public void GetHashCode_NullClaim_ReturnsZero()
        {
            var comparer = new ClaimComparer();
            comparer.GetHashCode(null!).Should().Be(0);
        }

        [TestMethod]
        public void GetHashCode_EqualClaims_SameHashCode()
        {
            var comparer = new ClaimComparer();
            var x = new Claim("type", "value", ClaimValueTypes.String, "issuer");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuer");
            comparer.GetHashCode(x).Should().Be(comparer.GetHashCode(y));
        }

        [TestMethod]
        public void GetHashCode_IgnoreIssuer_SameHashForDifferentIssuers()
        {
            var comparer = new ClaimComparer(new ClaimComparer.Options { IgnoreIssuer = true });
            var x = new Claim("type", "value", ClaimValueTypes.String, "issuerA");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuerB");
            comparer.GetHashCode(x).Should().Be(comparer.GetHashCode(y));
        }

        [TestMethod]
        public void GetHashCode_IgnoreValueCase_SameHashForDifferentCases()
        {
            var comparer = new ClaimComparer(new ClaimComparer.Options { IgnoreValueCase = true });
            var x = new Claim("type", "VALUE", ClaimValueTypes.String, "ISSUER");
            var y = new Claim("type", "value", ClaimValueTypes.String, "issuer");
            comparer.GetHashCode(x).Should().Be(comparer.GetHashCode(y));
        }

        [TestMethod]
        public void GetHashCode_IsNotNegativeByConvention_CanBeUsedInHashSet()
        {
            var comparer = new ClaimComparer();
            var claims = new HashSet<Claim>(comparer)
            {
                new Claim("role", "admin"),
                new Claim("role", "admin"), // duplicate
                new Claim("role", "user"),
            };
            claims.Should().HaveCount(2);
        }

        // ─── Integration: used as IEqualityComparer ───────────────────────────

        [TestMethod]
        public void AsEqualityComparer_Distinct_RemovesDuplicates()
        {
            var comparer = new ClaimComparer();
            var list = new List<Claim>
            {
                new Claim("type", "value"),
                new Claim("type", "value"),
                new Claim("type", "other"),
            };
            var distinct = list.Distinct(comparer).ToList();
            distinct.Should().HaveCount(2);
        }

        // ─── Options class ────────────────────────────────────────────────────

        [TestMethod]
        public void Options_DefaultValues_AreCorrect()
        {
            var options = new ClaimComparer.Options();
            options.IgnoreIssuer.Should().BeFalse();
            options.IgnoreValueCase.Should().BeFalse();
        }

        [TestMethod]
        public void Options_SetProperties_Persist()
        {
            var options = new ClaimComparer.Options { IgnoreIssuer = true, IgnoreValueCase = true };
            options.IgnoreIssuer.Should().BeTrue();
            options.IgnoreValueCase.Should().BeTrue();
        }
    }
}
