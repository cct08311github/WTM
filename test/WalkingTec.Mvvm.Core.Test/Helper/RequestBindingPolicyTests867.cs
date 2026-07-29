#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    /// <summary>
    /// Issue #867: <see cref="RequestBindingPolicy.IsPathAllowed(object, string, string)"/> is
    /// the allowlist <c>BaseController.RedoUpdateModel</c>/<c>BaseApiController.RedoUpdateModel</c>
    /// now consult before writing a caller-supplied form/query key onto a VM via
    /// <see cref="PropertyHelper.SetPropertyValue"/>. These tests exercise the policy function
    /// directly (no HTTP pipeline) so each guard — the curated gateway-name/declaring-type check,
    /// the static-member check, and the depth cap — can be pinned in isolation. The end-to-end
    /// proof that this actually stops the real <c>ConfigInfo.IsQuickDebug</c> exploit through a
    /// live <c>/_Framework/GetPagingData</c> request lives in
    /// <c>WalkingTec.Mvvm.Api.Test.RequestBindingScopeHttpTests867</c>.
    /// </summary>
    [TestClass]
    public class RequestBindingPolicyTests867
    {
        // A downstream Searcher subclass, the same shape as a real demo StudentSearcher: adds
        // its own filter field (ZipCode) on top of BaseSearcher's designed binding surface
        // (Page/Limit/SortInfo/...).
        private class FixtureSearcher : BaseSearcher
        {
            public string? ZipCode { get; set; }
        }

        // A downstream BaseVM subclass, the same shape as a real ListVM: adds its own Searcher
        // property plus, for the static-member test, a public static field that is NOT one of
        // the curated gateway names — isolating the static-member guard from the
        // gateway-name/declaring-type guard (both of which fire on "Wtm").
        private class FixtureVM : BaseVM
        {
            public static string StaticSecret = "unchanged";
            public FixtureSearcher Searcher { get; set; } = new();
        }

        [TestCleanup]
        public void ResetStaticFixtureState()
        {
            // FixtureVM.StaticSecret is process-wide static state, same shape as the real
            // WTMContext.ReloadUserFunc this guard exists to protect — reset it after every test
            // so mutation tests (which deliberately try to overwrite it) cannot leak into
            // unrelated tests run later in the same process.
            FixtureVM.StaticSecret = "unchanged";
        }

        // ── null / empty guards ──────────────────────────────────────────────

        [TestMethod]
        public void IsPathAllowed_NullSource_ReturnsTrue()
        {
            // Nothing to check against a null source; SetPropertyValue itself no-ops on null too.
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed((object?)null, "ConfigInfo.IsQuickDebug"));
        }

        [TestMethod]
        public void IsPathAllowed_NullOrEmptyProperty_ReturnsTrue()
        {
            var vm = new FixtureVM();
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, null));
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, ""));
        }

        [TestMethod]
        public void IsPathAllowed_UnresolvableKey_ReturnsTrue()
        {
            // No member named this exists anywhere on the path — PropertyHelper.SetPropertyValue
            // would break/no-op too, so there is nothing dangerous to reject.
            var vm = new FixtureVM();
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "ThisPropertyDoesNotExistAnywhere"));
        }

        // ── gateway-name / declaring-type guard ──────────────────────────────

        [TestMethod]
        public void IsPathAllowed_BareWtm_ReturnsFalse()
        {
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Wtm"),
                "#867: 'Wtm' is declared on BaseVM and is the gateway to the whole WTMContext graph.");
        }

        [TestMethod]
        public void IsPathAllowed_ConfigInfoDotIsQuickDebug_ReturnsFalse()
        {
            // The most direct alias: BaseVM.ConfigInfo forwards straight to Wtm.ConfigInfo,
            // reachable without ever naming "Wtm".
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "ConfigInfo.IsQuickDebug"),
                "#867: 'ConfigInfo' is declared on BaseVM — must be rejected at the first hop.");
        }

        [TestMethod]
        public void IsPathAllowed_WtmDotConfigInfoDotIsQuickDebug_ReturnsFalse()
        {
            // 3 segments — within MaxDepth, so this is rejected by the declaring-type/gateway
            // check specifically, not by the depth cap. Isolates that guard from the depth cap
            // for mutation-testing purposes.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Wtm.ConfigInfo.IsQuickDebug"),
                "#867: 'Wtm' is declared on BaseVM — must be rejected at the first hop, well " +
                "within the 3-segment depth cap.");
        }

        [TestMethod]
        public void IsPathAllowed_AliasedSearcherDotWtmDotConfigInfoDotIsQuickDebug_ReturnsFalse()
        {
            // The issue's own alias example: "Searcher" itself is NOT a banned name (it is
            // declared on FixtureVM, not on BaseVM/BaseSearcher), so a name blocklist keyed on
            // the key's literal first segment would miss this entirely. The declaring-type check
            // catches it at the SECOND hop instead, where "Wtm" resolves against FixtureSearcher
            // and is found declared on BaseSearcher.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.Wtm.ConfigInfo.IsQuickDebug"),
                "#867: the declaring-type check must catch 'Wtm' as the SECOND hop, proving it " +
                "is not a literal-first-segment name blocklist.");
        }

        [TestMethod]
        public void IsPathAllowed_LoginUserInfoDotITCode_ReturnsFalse()
        {
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "LoginUserInfo.ITCode"),
                "#867: 'LoginUserInfo' is declared on BaseVM and is a per-request identity gateway.");
        }

        [TestMethod]
        public void IsPathAllowed_DcDotSomething_ReturnsFalse()
        {
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "DC.CurrentUserCode"),
                "#867: 'DC' is declared on BaseVM and is the data-context gateway.");
        }

        // ── static-member guard (isolated from the gateway-name guard) ──────

        [TestMethod]
        public void IsPathAllowed_PublicStaticField_ReturnsFalse()
        {
            // "StaticSecret" is not one of the curated gateway names, and FixtureVM is not
            // BaseVM/BaseSearcher/WTMContext — this isolates the static-member guard from the
            // gateway-name/declaring-type guard, which would not otherwise fire here.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "StaticSecret"),
                "#867: Type.GetMember defaults to Public|Instance|Static, so a public static " +
                "field is reachable through an instance path and must be rejected independently " +
                "of the gateway-name check.");
        }

        [TestMethod]
        public void IsStaticMemberGuard_ActuallyPreventsTheOverwrite_WhenPolicyIsHonoured()
        {
            // Not just "the policy says no" — proves the guard is load-bearing: if a caller
            // ignores IsPathAllowed and writes anyway, the static field DOES change (this is
            // exactly what RedoUpdateModel would have done pre-#867). This is what makes the
            // guard's absence a real, observable defect rather than a cosmetic one.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "StaticSecret"));

            PropertyHelper.SetPropertyValue(vm, "StaticSecret", "hostile", null, true);
            Assert.AreEqual("hostile", FixtureVM.StaticSecret,
                "Sanity check: PropertyHelper.SetPropertyValue really does reach a public static " +
                "field through an instance path when nothing gates it — this is the primitive " +
                "RequestBindingPolicy.IsPathAllowed exists to let callers refuse to act on.");
        }

        // ── depth cap ─────────────────────────────────────────────────────

        [TestMethod]
        public void IsPathAllowed_FourSegmentPath_ReturnsFalse_EvenWhenEverySegmentIsHarmless()
        {
            // Every individual segment here is a legitimate, non-gateway, non-static member —
            // only the depth (4 > MaxDepth of 3) makes this rejected. Isolates the depth cap from
            // the other two guards.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.SortInfo.Property.Length"),
                "#867: depth cap of 3 must reject a 4-segment path even when every individual " +
                "segment would otherwise be allowed.");
        }

        // ── positive controls — the framework's own designed binding surface ─

        [TestMethod]
        public void IsPathAllowed_SearcherDotCustomFilterField_ReturnsTrue()
        {
            var vm = new FixtureVM();
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.ZipCode"),
                "#867: a Searcher subclass's own filter field must remain bindable — this is " +
                "the framework's designed use of RedoUpdateModel.");
        }

        [TestMethod]
        public void IsPathAllowed_SearcherDotPage_ReturnsTrue()
        {
            // Page/Limit/Count/PageCount are declared directly on BaseSearcher itself (not a
            // downstream subclass) — confirms the gateway-name list is curated, not "everything
            // declared on BaseSearcher."
            var vm = new FixtureVM();
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.Page"));
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.Limit"));
        }

        [TestMethod]
        public void IsPathAllowed_SearcherDotSortInfoDotProperty_ReturnsTrue()
        {
            // The deepest verified-legitimate payload (3 segments, exactly at MaxDepth) — proves
            // the depth cap is calibrated correctly, not just "small enough to never matter."
            var vm = new FixtureVM();
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.SortInfo.Property"));
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.SortInfo.Direction"));
        }

        [TestMethod]
        public void IsPathAllowed_VmLocalNonGatewayProperty_ReturnsTrue()
        {
            // BaseVM.Remark/ActionName are ordinary VM-local scalar state, not gateways — must
            // stay bindable.
            var vm = new FixtureVM();
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "Remark"));
            Assert.IsTrue(RequestBindingPolicy.IsPathAllowed(vm, "ActionName"));
        }
    }
}
