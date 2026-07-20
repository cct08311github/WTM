#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Regression coverage for issue #761: <c>FrameworkRefreshTokens</c> had no indexes,
    /// so <c>TokenService.RefreshTokenAsync</c>'s <c>Token</c> lookup and the #757
    /// retention sweeps (filter/order by <c>ExpiresUtc</c> / <c>RevokedUtc</c>) were all
    /// full table scans.
    /// <para>
    /// Asserts the three EF Core model indexes declared via <c>[Index]</c> attributes on
    /// <see cref="RefreshTokenEntity"/> (see that class's XML doc for the design rationale)
    /// are actually present in the compiled EF model. These tests inspect
    /// <c>DbContext.Model</c> directly, so they fail if any index is removed, renamed to a
    /// different property set, or its uniqueness/column order changes — independent of the
    /// database provider (EF InMemory is used here purely to build the model; no rows are
    /// persisted).
    /// </para>
    /// </summary>
    [TestClass]
    public class RefreshTokenIndexTests
    {
        private static WalkingTec.Mvvm.Core.Test.DataContext CreateContext() =>
            new(Guid.NewGuid().ToString("N"), DBTypeEnum.Memory);

        [TestMethod]
        public void RefreshTokenEntity_HasIndexOnToken()
        {
            using var dc = CreateContext();
            var entityType = dc.Model.FindEntityType(typeof(RefreshTokenEntity));
            Assert.IsNotNull(entityType, "RefreshTokenEntity must be registered in the model.");

            bool hasTokenIndex = entityType!.GetIndexes()
                .Any(ix => ix.Properties.Count == 1
                           && ix.Properties[0].Name == nameof(RefreshTokenEntity.Token));

            Assert.IsTrue(hasTokenIndex,
                "RefreshTokenEntity must have an index on Token — TokenService.RefreshTokenAsync's " +
                "hot-path lookup (#761) would otherwise be a full table scan.");
        }

        [TestMethod]
        public void RefreshTokenEntity_TokenIndex_IsNotUnique()
        {
            // #761 design: non-unique by design — compatibility-first, so applying this
            // index to an existing production database can never fail an index build
            // against pre-existing duplicate Token rows. The hot-path lookup only needs
            // an index to serve it, not a uniqueness constraint.
            using var dc = CreateContext();
            var entityType = dc.Model.FindEntityType(typeof(RefreshTokenEntity))!;
            var tokenIndex = entityType.GetIndexes()
                .Single(ix => ix.Properties.Count == 1
                              && ix.Properties[0].Name == nameof(RefreshTokenEntity.Token));

            Assert.IsFalse(tokenIndex.IsUnique,
                "The Token index must remain non-unique (#761) — a unique constraint risks a " +
                "failed index build on upgrade against any pre-existing duplicate Token data.");
        }

        [TestMethod]
        public void RefreshTokenEntity_HasIndexOnExpiresUtc()
        {
            using var dc = CreateContext();
            var entityType = dc.Model.FindEntityType(typeof(RefreshTokenEntity))!;

            bool hasExpiresIndex = entityType.GetIndexes()
                .Any(ix => ix.Properties.Count == 1
                           && ix.Properties[0].Name == nameof(RefreshTokenEntity.ExpiresUtc));

            Assert.IsTrue(hasExpiresIndex,
                "RefreshTokenEntity must have an index on ExpiresUtc — " +
                "RefreshTokenRetentionService's expired-token sweep (#757/#761) filters and " +
                "orders by this column.");
        }

        [TestMethod]
        public void RefreshTokenEntity_HasCompositeIndexOnRevokedUtcThenExpiresUtc()
        {
            using var dc = CreateContext();
            var entityType = dc.Model.FindEntityType(typeof(RefreshTokenEntity))!;

            bool hasCompositeIndex = entityType.GetIndexes()
                .Any(ix => ix.Properties.Count == 2
                           && ix.Properties[0].Name == nameof(RefreshTokenEntity.RevokedUtc)
                           && ix.Properties[1].Name == nameof(RefreshTokenEntity.ExpiresUtc));

            Assert.IsTrue(hasCompositeIndex,
                "RefreshTokenEntity must have a composite index on (RevokedUtc, ExpiresUtc) " +
                "covering RefreshTokenRetentionService's revoked-token sweep predicate " +
                "(RevokedUtc != null && RevokedUtc < cutoff && ExpiresUtc < now), #761.");
        }

        [TestMethod]
        public void RefreshTokenEntity_HasExactlyThreeIndexes()
        {
            // Guards against silently collapsing/removing one of the three indexes
            // without any of the more specific assertions above catching it.
            using var dc = CreateContext();
            var entityType = dc.Model.FindEntityType(typeof(RefreshTokenEntity))!;

            Assert.AreEqual(3, entityType.GetIndexes().Count(),
                "RefreshTokenEntity is expected to declare exactly three indexes " +
                "(Token, ExpiresUtc, RevokedUtc+ExpiresUtc) per #761.");
        }
    }
}
