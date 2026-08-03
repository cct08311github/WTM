#nullable enable
using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// Issue #1007 — <see cref="WTMContext.SetCurrentTenant(string?)"/>'s narrowed admission
    /// rule (<c>IsTenantSwitchPermitted</c>) and its kill switch
    /// (<see cref="Configs.UseLegacyTenantSwitchAuthorization"/>).
    /// <para>
    /// Every branch inside <c>IsTenantSwitchPermitted</c> is a single, independently-deletable
    /// statement. Where a row below has a real "delete this one line and this exact test flips"
    /// proof, the test's own doc comment names the line; four rows (4, 6, 11's Allow-half is
    /// covered structurally by the call-count assertion, 14) are honestly documented as having
    /// no single-line deletion proof rather than a manufactured one — see design round 6 §8.
    /// </para>
    /// <para>
    /// <b>What this file does NOT cover</b> — <see cref="IWtmTenantSwitchPolicy"/> resolution
    /// here goes through <see cref="WTMContext.SetServiceProvider"/>, which sets the
    /// <c>_serviceProvider</c> field directly and bypasses the real
    /// <c>_serviceProvider ?? _httpContext?.RequestServices</c> production fallback
    /// (<c>WTMContext.cs:35</c>). A test that exercises that real fallback through genuine
    /// request-scoped DI over a real, routed HTTP request lives in
    /// <c>WalkingTec.Mvvm.Api.Test.TenantSwitchPolicySeamTests1007</c> — see that file for why a
    /// second test location was needed (production-readiness.md, #1007 section).
    /// </para>
    /// </summary>
    [TestClass]
    public class SetCurrentTenantAdmissionTests1007
    {
        // ─── shared helpers ─────────────────────────────────────────────

        private static WTMContext CreateHostWtm(string itcode = "host1")
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = itcode, TenantCode = null };
            return wtm;
        }

        private static WTMContext CreateTenantWtm(string tenantCode, string itcode = "tenantuser1")
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = itcode, TenantCode = tenantCode };
            return wtm;
        }

        private static void SetAllTenant(WTMContext wtm, List<FrameworkTenant> tenants) =>
            wtm.GlobaInfo!.SetTenantGetFunc(() => tenants);

        private static Mock<IWtmTenantSwitchPolicy> WirePolicy(WTMContext wtm, WtmAuthorizationDecision decision)
        {
            var policy = new Mock<IWtmTenantSwitchPolicy>();
            policy.Setup(x => x.CanSwitchTenant(
                    It.IsAny<WTMContext>(), It.IsAny<LoginUserInfo>(), It.IsAny<FrameworkTenant>(), It.IsAny<string>()))
                .Returns(decision);
            WireServiceProvider(wtm, policy.Object);
            return policy;
        }

        private static void WireServiceProvider(WTMContext wtm, IWtmTenantSwitchPolicy policy)
        {
            var sp = new Mock<IServiceProvider>();
            sp.Setup(x => x.GetService(typeof(IWtmTenantSwitchPolicy))).Returns(policy);
            wtm.SetServiceProvider(sp.Object);
        }

        /// <summary>
        /// Reads the SAME cache key <c>WTMContext.User.cs</c>'s <c>LoginUserInfo</c> setter
        /// writes to (<c>WTMContext.User.cs:158</c>) — via the mock's real
        /// <c>MemoryDistributedCache</c> instance (<c>MockWtmContext.cs:51</c>), not the
        /// in-process <c>_loginUserInfo</c> field — so a passing assertion proves the cache
        /// write actually happened, not merely that the in-memory object was mutated.
        /// </summary>
        private static LoginUserInfo? ReadBackFromCache(WTMContext wtm, string itcode, string? homeTenantCode)
        {
            var key = GlobalConstants.CacheKey.UserInfo + ":" + itcode + "$`$" + homeTenantCode;
            return wtm.Cache?.Get<LoginUserInfo>(key);
        }

        // ─── row 1 / 1b: host × unresolvable code (L-notfound) ─────────

        [TestMethod]
        public void Row1_Host_NonExistentCode_ReturnsFalse()
        {
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>());

            var result = wtm.SetCurrentTenant("ghost-tenant");

            Assert.IsFalse(result, "#1007 row 1: a host requesting an unresolvable tenant code must be refused.");
            Assert.IsNull(wtm.LoginUserInfo!.CurrentTenant, "#1007 row 1: a refused switch must not mutate CurrentTenant.");
        }

        [TestMethod]
        public void Row1b_Host_EmptyStringDirectToCore_ReturnsFalse()
        {
            // Core performs no "" -> null translation itself (that is the four HTTP entry
            // points' job, e.g. _FrameworkController.cs:1817's `tenant == "" ? null : tenant`).
            // Calling Core directly with a literal "" therefore exercises the same L-notfound
            // path as row 1 whenever no FrameworkTenant row has TCode == "".
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>());

            var result = wtm.SetCurrentTenant("");

            Assert.IsFalse(result, "#1007 row 1b: an empty-string request with no matching TCode==\"\" row must be refused.");
        }

        // ─── row 2: host × unique listed tenant (L-host) ────────────────

        [TestMethod]
        public void Row2_Host_ListedUniqueTenant_ReturnsTrue()
        {
            // Deletion proof: delete `return true;` from the L-host branch
            // (`if (user.TenantCode == null) return true;`) and this input falls through to
            // L-child's `descriptor.TenantCode == user.TenantCode` -> "parentA" == null -> false
            // -> the trailing `return false;` -> this test goes red.
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "childX", TenantCode = "parentA" },
            });

            var result = wtm.SetCurrentTenant("childX");

            Assert.IsTrue(result, "#1007 row 2: a host may switch into any uniquely-resolved listed tenant (depth unrestricted).");
            Assert.AreEqual("childX", wtm.LoginUserInfo!.CurrentTenant);
            var cached = ReadBackFromCache(wtm, "host1", null);
            Assert.IsNotNull(cached, "#1007 row 2: an admitted switch must update the distributed cache entry.");
            Assert.AreEqual("childX", cached!.CurrentTenant);
        }

        // ─── row 3: tenant × direct child (L-child) ──────────────────────

        [TestMethod]
        public void Row3_Tenant_DirectChild_ReturnsTrue()
        {
            // Deletion proof: delete L-child's `return true;` and this falls through to the
            // trailing `return false;`.
            var wtm = CreateTenantWtm("home");
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "childA", TenantCode = "home" },
            });

            var result = wtm.SetCurrentTenant("childA");

            Assert.IsTrue(result, "#1007 row 3: a tenant-scoped caller may switch into a direct child tenant.");
            Assert.AreEqual("childA", wtm.LoginUserInfo!.CurrentTenant);
        }

        // ─── row 4: tenant × grandchild, unique (L-child fails) ──────────

        [TestMethod]
        public void Row4_Tenant_Grandchild_ReturnsFalse()
        {
            // No single-line deletion proof exists for this row (design round 6 §8, row 4):
            // narrowing G1's whole guard is the only way to flip a Deny row to Allow. M3
            // (test/mutants/entries/1007-childscope-widen.json) covers this row's branch
            // precision by widening L-child's condition instead of deleting a line.
            var wtm = CreateTenantWtm("home");
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "parentA", TenantCode = "home" },
                new FrameworkTenant { TCode = "grandX", TenantCode = "parentA" },
            });

            var result = wtm.SetCurrentTenant("grandX");

            Assert.IsFalse(result, "#1007 row 4: a tenant-scoped caller may not switch into a grandchild tenant by default.");
            // CurrentTenant falls back to TenantCode ("home") when no override has ever been
            // set (LoginUserInfo.CurrentTenant's getter: `_currentTenant ?? TenantCode`) -- a
            // refused switch must leave it at that unmutated default, not null.
            Assert.AreEqual("home", wtm.LoginUserInfo!.CurrentTenant, "#1007 row 4: a refused switch must not mutate CurrentTenant.");
        }

        // ─── row 5: tenant × own home code, not in snapshot (L-home) ────

        [TestMethod]
        public void Row5_Tenant_OwnHomeCodeNotInSnapshot_ReturnsTrue()
        {
            // Deletion proof: delete L-home's `return true;` and this input falls through to
            // the AllTenant lookup, finds nothing (the snapshot is empty), and L-notfound
            // returns false.
            var wtm = CreateTenantWtm("home");
            SetAllTenant(wtm, new List<FrameworkTenant>()); // "home" itself is not listed at all

            var result = wtm.SetCurrentTenant("home");

            Assert.IsTrue(result, "#1007 row 5: a caller's own home tenant code is always admitted, even absent from AllTenant (documented limitation: not resolved).");
        }

        // ─── row 6: null request, both directions ────────────────────────

        [TestMethod]
        public void Row6a_Host_NullRequest_ReturnsTrue()
        {
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>());

            var result = wtm.SetCurrentTenant(null);

            Assert.IsTrue(result, "#1007 row 6 (host half): null (return to home) is always admitted for a host caller.");
        }

        [TestMethod]
        public void Row6b_Tenant_NullRequest_ReturnsFalse()
        {
            var wtm = CreateTenantWtm("home");
            SetAllTenant(wtm, new List<FrameworkTenant>());

            var result = wtm.SetCurrentTenant(null);

            Assert.IsFalse(result, "#1007 row 6 (tenant half): a tenant-scoped caller cannot use null to become a host.");
            // Neither half of row 6 has a single-line deletion proof on its own (design round 6
            // §8, row 6): deleting L-null still leaves the host half true via L-home's
            // `null == null`, and still leaves the tenant half false via L-notfound. The
            // deletion proof for L-null specifically is row 7 below.
        }

        // ─── row 7: L-null's own deletion proof (malformed provider row) ─

        [TestMethod]
        public void Row7_Tenant_NullRequest_MalformedNullTCodeRow_ReturnsFalse()
        {
            // Deletion proof (THE proof for L-null): delete `return user.TenantCode == null;`
            // from the L-null branch. Then for this exact input (tenant caller, req == null):
            // `req == user.TenantCode` -> null == "home" -> false (L-home doesn't fire);
            // snapshot lookup: `x.TCode == req` -> `null == null` -> TRUE for the malformed row
            // below -> unique match -> policy Inherit (unregistered) -> user.TenantCode != null,
            // skip L-host -> descriptor.TenantCode ("home") == user.TenantCode ("home") -> TRUE.
            // Deleting the L-null line flips this exact test from false to true.
            var wtm = CreateTenantWtm("home");
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                // SetTenantGetFunc is a public API (GlobalData.cs:60); nothing stops a custom
                // implementation from yielding a TCode == null row despite the property's
                // non-nullable annotation (design round 6 §5 point 2).
                new FrameworkTenant { TCode = null!, TenantCode = "home" },
            });

            var result = wtm.SetCurrentTenant(null);

            Assert.IsFalse(result, "#1007 row 7: a malformed TCode==null provider row must not let a null request through — L-null must return before the snapshot is ever read.");
        }

        // ─── row 8 / 8b: ambiguous (L-ambiguous) ─────────────────────────

        [TestMethod]
        public void Row8_Host_DuplicateTCode_ReturnsFalse()
        {
            // Deletion proof: delete L-ambiguous's `return false;` and FirstOrDefault() takes
            // the first of the two matches -> policy Inherit -> L-host true.
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "dup", TenantCode = "p1" },
                new FrameworkTenant { TCode = "dup", TenantCode = "p2" },
            });

            var result = wtm.SetCurrentTenant("dup");

            Assert.IsFalse(result, "#1007 row 8: a duplicated TCode must refuse the switch outright, regardless of caller type.");
        }

        [TestMethod]
        public void Row8b_Tenant_DuplicateChildCode_BothParentHome_ReturnsFalse()
        {
            // Both rows would individually satisfy L-child if resolved alone -- the point of
            // this row is that ambiguity refuses BEFORE that question is even asked, regardless
            // of whether every candidate row happens to be the caller's own child.
            var wtm = CreateTenantWtm("home");
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "dup2", TenantCode = "home" },
                new FrameworkTenant { TCode = "dup2", TenantCode = "home" },
            });

            var result = wtm.SetCurrentTenant("dup2");

            Assert.IsFalse(result, "#1007 row 8b: a duplicated child TCode must refuse even when every candidate row's parent is the caller's own tenant.");
        }

        // ─── row 9 / 10: policy overrides the structural default ────────

        [TestMethod]
        public void Row9_PolicyDeny_Host_ListedUnique_ReturnsFalse()
        {
            // Deletion proof: delete L-deny's `return false;` and the Deny decision falls
            // through to L-host's true.
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "childY", TenantCode = "whatever" },
            });
            WirePolicy(wtm, WtmAuthorizationDecision.Deny);

            var result = wtm.SetCurrentTenant("childY");

            Assert.IsFalse(result, "#1007 row 9: a registered policy's Deny must override the host's otherwise-unrestricted default.");
        }

        [TestMethod]
        public void Row10_PolicyAllow_Tenant_Sibling_ReturnsTrue()
        {
            // Deletion proof: delete L-allow's `return true;` and the Allow decision falls
            // through to L-host (skipped, TenantCode != null) then L-child
            // ("otherParent" == "home" -> false) -> trailing false.
            var wtm = CreateTenantWtm("home");
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "sib1", TenantCode = "otherParent" },
            });
            WirePolicy(wtm, WtmAuthorizationDecision.Allow);

            var result = wtm.SetCurrentTenant("sib1");

            Assert.IsTrue(result, "#1007 row 10: a registered policy's Allow must override the tenant-scoped caller's otherwise-restricted default (sibling tenant).");
        }

        // ─── row 11: policy is structurally never consulted for NotFound/Ambiguous ─

        [TestMethod]
        public void Row11a_PolicyAllowStub_NonExistentCode_ReturnsFalse_PolicyNeverCalled()
        {
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>());
            var policy = WirePolicy(wtm, WtmAuthorizationDecision.Allow);

            var result = wtm.SetCurrentTenant("ghost");

            Assert.IsFalse(result, "#1007 row 11a: L-notfound must refuse before the policy is ever asked, even one that would say Allow.");
            policy.Verify(
                x => x.CanSwitchTenant(It.IsAny<WTMContext>(), It.IsAny<LoginUserInfo>(), It.IsAny<FrameworkTenant>(), It.IsAny<string>()),
                Times.Never,
                "#1007 row 11a: an unresolvable code must never reach IWtmTenantSwitchPolicy.CanSwitchTenant.");
        }

        [TestMethod]
        public void Row11b_PolicyAllowStub_DuplicateCode_ReturnsFalse_PolicyNeverCalled()
        {
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "dup3", TenantCode = "p1" },
                new FrameworkTenant { TCode = "dup3", TenantCode = "p2" },
            });
            var policy = WirePolicy(wtm, WtmAuthorizationDecision.Allow);

            var result = wtm.SetCurrentTenant("dup3");

            Assert.IsFalse(result, "#1007 row 11b: an ambiguous code must refuse before the policy is ever asked, even one that would say Allow.");
            policy.Verify(
                x => x.CanSwitchTenant(It.IsAny<WTMContext>(), It.IsAny<LoginUserInfo>(), It.IsAny<FrameworkTenant>(), It.IsAny<string>()),
                Times.Never,
                "#1007 row 11b: an ambiguous code must never reach IWtmTenantSwitchPolicy.CanSwitchTenant.");
        }

        // ─── row 12 / 13: kill switch restores the legacy body verbatim ──

        [TestMethod]
        public void Row12_KillSwitchOn_Host_NonExistentCode_ReturnsTrue_LegacyBehaviorRestored()
        {
            var wtm = CreateHostWtm();
            wtm.ConfigInfo!.UseLegacyTenantSwitchAuthorization = true;
            SetAllTenant(wtm, new List<FrameworkTenant>());

            var result = wtm.SetCurrentTenant("ghost-under-legacy");

            Assert.IsTrue(result, "#1007 row 12: with the kill switch on, a host may switch into any code, including one absent from AllTenant -- pre-#1007 behavior.");
        }

        [TestMethod]
        public void Row13_KillSwitchOn_Tenant_Grandchild_ReturnsFalse_PinsLegacyIsNotDisableAllChecks()
        {
            // Row 13 pins that the kill switch restores the OLD rule, not "no rule at all": a
            // tenant-scoped caller still cannot reach a grandchild tenant under the legacy body
            // either. This has a real deletion proof (design round 6 §8 row 13, corrected by
            // named-change NC5): delete the legacy body's own guard line --
            //   if (LoginUserInfo?.TenantCode == null || LoginUserInfo?.TenantCode == tenant ||
            //       GlobaInfo?.AllTenant?.Any(x => x.TCode == tenant && x.TenantCode == LoginUserInfo?.TenantCode) == true)
            // -- and the remaining `{ ... }` block is a legal standalone C# block that still
            // compiles and now runs unconditionally, admitting every switch (including this
            // grandchild request) -- this exact test goes from green to red.
            var wtm = CreateTenantWtm("home");
            wtm.ConfigInfo!.UseLegacyTenantSwitchAuthorization = true;
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "parentA", TenantCode = "home" },
                new FrameworkTenant { TCode = "grandX", TenantCode = "parentA" },
            });

            var result = wtm.SetCurrentTenant("grandX");

            Assert.IsFalse(result, "#1007 row 13: the kill switch restores the OLD admission rule, not an unconditional allow -- a grandchild switch is refused under the legacy body too.");
        }

        // ─── row 14: policy exceptions propagate, never collapse to Deny ─

        [TestMethod]
        public void Row14_PolicyThrows_Host_ListedUnique_ExceptionPropagates()
        {
            // Pinning test: no deletion-based proof exists here (there is no catch to delete —
            // design round 6 §8 row 14). This exists to stop a future change from adding a
            // catch-and-deny around the policy call, which would silently collapse a genuine
            // error into the same observable outcome as a legitimate refusal.
            var wtm = CreateHostWtm();
            SetAllTenant(wtm, new List<FrameworkTenant>
            {
                new FrameworkTenant { TCode = "childZ", TenantCode = "x" },
            });
            var policy = new Mock<IWtmTenantSwitchPolicy>();
            policy.Setup(x => x.CanSwitchTenant(
                    It.IsAny<WTMContext>(), It.IsAny<LoginUserInfo>(), It.IsAny<FrameworkTenant>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("#1007 row 14 probe"));
            WireServiceProvider(wtm, policy.Object);

            var ex = Assert.ThrowsException<InvalidOperationException>(() => wtm.SetCurrentTenant("childZ"));
            Assert.AreEqual("#1007 row 14 probe", ex.Message,
                "#1007 row 14: a thrown policy exception must propagate to SetCurrentTenant's caller, unmodified and uncaught.");
        }
    }
}
