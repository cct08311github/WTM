#nullable enable
using System;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    /// <summary>
    /// Issue #867: <see cref="RequestBindingPolicy.IsPathAllowed(object, string, string)"/> is
    /// the allowlist <c>BaseController.RedoUpdateModel</c>/<c>BaseApiController.RedoUpdateModel</c>
    /// now consult before writing a caller-supplied form/query key onto a VM via
    /// <see cref="PropertyHelper.SetPropertyValue"/>. These tests exercise the policy function
    /// directly (no HTTP pipeline) so each guard — the gateway-TYPE check, the static-member
    /// check, the ambiguous-resolution check, and the depth cap — can be pinned in isolation. The
    /// end-to-end proof that this actually stops the real <c>ConfigInfo.IsQuickDebug</c> exploit
    /// through a live <c>/_Framework/GetPagingData</c> request lives in
    /// <c>WalkingTec.Mvvm.Api.Test.RequestBindingScopeHttpTests867</c>.
    ///
    /// <para>
    /// <b>PR #884 cross-vendor review finding (round 1).</b> The version that first shipped
    /// checked <c>member.DeclaringType</c> plus a curated NAME set — a denylist, not a positive
    /// allowlist. A downstream VM could legally re-expose <c>Configs</c>/<c>GlobalData</c> under a
    /// name (or declaring class) the policy had never heard of and sail straight through. The
    /// "five shapes" tests below (<c>AliasProperty</c>/<c>NewShadowing</c>/
    /// <c>InterfaceTypedMember</c>/<c>IntermediateBaseClass</c>/<c>GenericTypeParameter</c>) each
    /// construct exactly one such bypass and assert it is now rejected — their absence from the
    /// original PR is why that version shipped with the hole. See <c>RequestBindingPolicy</c>'s
    /// own class doc comment for the fixed rule (reject by the TYPE each hop resolves to, not by
    /// name or declaring type).
    /// </para>
    ///
    /// <para>
    /// <b>Round 3: a declared type that IS itself a gateway but was missing from the list.</b>
    /// The "options family" tests below (<c>OptionsMonitorCurrentValue</c>/
    /// <c>OptionsSnapshotValue</c>/<c>OptionsValue</c>/<c>ServiceProvider</c>) cover a DIFFERENT
    /// failure mode than round 2's forwarding-setter finding: a plain, uncustomized getter typed
    /// <c>IOptionsMonitor&lt;ActionLogRetentionOptions&gt;</c> reached that options cache's own
    /// process-wide <c>CurrentValue</c> — no custom setter needed at all — because
    /// <c>IOptionsMonitor&lt;&gt;</c> was simply absent from <c>BannedGatewayTypes</c>. See
    /// <c>RequestBindingPolicy</c>'s own class doc comment ("Known, documented limitation") for
    /// why this is a structurally different, and structurally still-open, failure mode from
    /// round 2's.
    /// </para>
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
        // the banned gateway types and is declared on a type that is not BaseVM/BaseSearcher/
        // WTMContext — isolating the static-member guard from the gateway-type guard, which would
        // not otherwise fire here.
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
        public void IsPathAllowed_UnresolvableSingleSegmentKey_ReturnsFalse()
        {
            // PR #884 review round 2: a single-segment key that resolves to nothing IS a genuine
            // PropertyHelper.SetPropertyValue no-op (its final-segment lookup also finds nothing
            // and returns without writing) — so rejecting it here is more conservative than
            // strictly necessary, not a functional behavior change (nothing was ever going to be
            // written either way). Kept conservative rather than special-cased, because the
            // dangerous case (an intermediate segment failing to resolve, see the "Missing.*"
            // tests below) looks identical from this policy's point of view until you already know
            // the answer — better to have one uniform "unresolved anywhere ⇒ reject" rule than a
            // rule that has to first prove a failure is "the safe kind" before allowing it through.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "ThisPropertyDoesNotExistAnywhere"));
        }

        // ── zero-resolution at an INTERMEDIATE segment (PR #884 review round 2) ──

        [TestMethod]
        public void MissingIntermediateSegment_ActuallyWritesFinalSegmentOnVm_WhenPolicyIsIgnored()
        {
            // Not just "the policy says no" — proves the guard is load-bearing, per this review's
            // own repro. PropertyHelper.SetPropertyValue's intermediate loop (PropertyHelper.cs:
            // 523-551) `break`s on an unresolved middle segment WITHOUT resetting tempType/temp —
            // they stay at the ORIGINAL source (or the last hop that DID resolve) — and execution
            // falls through to resolve and WRITE the final segment against that frozen type
            // (PropertyHelper.cs:553-559). So "Missing.StaticSecret" against a FixtureVM: "Missing"
            // fails to resolve (nothing by that name on FixtureVM), the loop breaks with tempType
            // still FixtureVM's own type, and the final segment "StaticSecret" — which DOES exist
            // there — gets resolved and written. Confirmed here BEFORE asserting the policy blocks
            // it, so this test cannot pass by coincidence.
            var vm = new FixtureVM();
            PropertyHelper.SetPropertyValue(vm, "Missing.StaticSecret", "hostile-via-missing-prefix", null, true);
            Assert.AreEqual("hostile-via-missing-prefix", FixtureVM.StaticSecret,
                "Sanity check: PropertyHelper.SetPropertyValue really does write the FINAL segment " +
                "onto the ORIGINAL type when an INTERMEDIATE segment fails to resolve — this is the " +
                "primitive that let 'Missing.StaticSecret' bypass the static-member guard entirely " +
                "when this policy incorrectly treated any unresolved segment as a safe no-op.");

            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Missing.StaticSecret"),
                "#867/PR#884 review round 2: a nonexistent intermediate segment must not let the " +
                "final segment bypass every later guard (static, ambiguity, gateway-type) by being " +
                "evaluated against the frozen original type.");
        }

        [TestMethod]
        public void IsPathAllowed_MissingIntermediateSegmentThenStaticFinal_ReturnsFalse()
        {
            // The exact key the review named.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Missing.StaticSecret"));
        }

        [TestMethod]
        public void IsPathAllowed_MissingIntermediateSegmentThenAmbiguousFinal_ReturnsFalse()
        {
            // The exact shape the review named ("Missing.<ambiguous-name>"), using the same
            // AmbiguousShadowVM fixture the dedicated ambiguity test below uses. Zero-resolution
            // fails closed before the walk ever reaches "Label", so this is also, incidentally, a
            // second independent reason this specific key is rejected — belt and suspenders,
            // exactly what the review asked this policy to be.
            var vm = new AmbiguousShadowVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Missing.Label"));
        }

        // ── gateway-TYPE guard: the direct, undisguised paths ────────────────

        [TestMethod]
        public void IsPathAllowed_BareWtm_ReturnsFalse()
        {
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Wtm"),
                "#867: 'Wtm' resolves to type WTMContext — a banned gateway type.");
        }

        [TestMethod]
        public void IsPathAllowed_ConfigInfoDotIsQuickDebug_ReturnsFalse()
        {
            // The most direct alias: BaseVM.ConfigInfo forwards straight to Wtm.ConfigInfo,
            // reachable without ever naming "Wtm".
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "ConfigInfo.IsQuickDebug"),
                "#867: 'ConfigInfo' resolves to type Configs — must be rejected at the first hop.");
        }

        [TestMethod]
        public void IsPathAllowed_WtmDotConfigInfoDotIsQuickDebug_ReturnsFalse()
        {
            // 3 segments — within MaxDepth, so this is rejected by the gateway-type check
            // specifically, not by the depth cap. Isolates that guard from the depth cap for
            // mutation-testing purposes.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Wtm.ConfigInfo.IsQuickDebug"),
                "#867: 'Wtm' resolves to type WTMContext — must be rejected at the first hop, " +
                "well within the 3-segment depth cap.");
        }

        [TestMethod]
        public void IsPathAllowed_AliasedSearcherDotWtmDotConfigInfoDotIsQuickDebug_ReturnsFalse()
        {
            // "Searcher" itself is not a banned type (FixtureSearcher is not one of
            // BannedGatewayTypes), so this proves the check fires on whichever hop actually
            // resolves to a banned type — here, the SECOND hop, where "Wtm" resolves against
            // FixtureSearcher's own Wtm property (type WTMContext).
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Searcher.Wtm.ConfigInfo.IsQuickDebug"),
                "#867: the gateway-type check must catch 'Wtm' (type WTMContext) as the SECOND " +
                "hop, proving the check is not scoped to only the key's first segment.");
        }

        [TestMethod]
        public void IsPathAllowed_LoginUserInfoDotITCode_ReturnsFalse()
        {
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "LoginUserInfo.ITCode"),
                "#867: 'LoginUserInfo' resolves to type LoginUserInfo — a per-request identity gateway.");
        }

        [TestMethod]
        public void IsPathAllowed_DcDotSomething_ReturnsFalse()
        {
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "DC.CurrentUserCode"),
                "#867: 'DC' resolves to type IDataContext — the data-context gateway.");
        }

        // ── the five bypass shapes PR #884's review found (each its own fixture) ─

        // Shape 1: alias property — a downstream VM exposes the exact same singleton under a
        // name the old declaring-type/name check had never heard of.
        private class AliasVM : BaseVM
        {
            public Configs? Settings => base.ConfigInfo;
        }

        [TestMethod]
        public void IsPathAllowed_AliasProperty_ReturnsFalse()
        {
            var vm = new AliasVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Settings.IsQuickDebug"),
                "#867/PR#884 review shape 1 (alias property): 'Settings' is declared on AliasVM, " +
                "not BaseVM, and named nothing the old policy recognised — but it resolves to " +
                "type Configs, so the gateway-TYPE check must still reject it.");
        }

        // Shape 2: new-shadowing — a downstream VM re-declares the SAME name the base class uses,
        // hiding (not overriding) BaseVM.ConfigInfo.
        private class ShadowVM : BaseVM
        {
            public new Configs? ConfigInfo => base.ConfigInfo;
        }

        [TestMethod]
        public void IsPathAllowed_NewShadowedConfigInfo_ReturnsFalse()
        {
            // Verified empirically (not assumed): Type.GetMember("ConfigInfo") on ShadowVM
            // returns exactly ONE member — the derived, hiding property — not two. .NET's
            // reflection resolves simple same-kind (property-hides-property) "new" hiding down to
            // the single most-derived member before this policy ever sees it, so this case is
            // caught by the gateway-TYPE check alone (the hiding property's own type is Configs),
            // not by the ambiguous-resolution guard — see
            // IsPathAllowed_AmbiguousNewShadowedNonGatewayProperty_ReturnsFalse below for the
            // shape that DOES produce >1 members (hiding across member KINDS, e.g. a field hidden
            // by a property).
            var vm = new ShadowVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "ConfigInfo.IsQuickDebug"),
                "#867/PR#884 review shape 2 (new-shadowing): 'ConfigInfo' declared with 'new' on " +
                "ShadowVM resolves (as the sole, most-derived member) to type Configs, a banned " +
                "gateway type — the gateway-TYPE check must reject it.");
        }

        // Shape 3: interface-typed member — a downstream VM exposes an alias whose DECLARED type
        // is an interface implemented by a banned gateway type, not the concrete type directly.
        // (A LITERAL C# "explicit interface implementation" — Configs IHasConfig.X => ... — is
        // not independently reachable via Type.GetMember(name) at all on the concrete type; that
        // was confirmed both by this review and independently here, so it is not a distinct
        // bypass vector through this exact mechanism. The shape that DOES need covering is an
        // ordinary public member whose declared return type happens to be one of the banned
        // interfaces — IDataContext, ISessionService, IModelStateService, IDistributedCache,
        // IStringLocalizer, IUIService — which IS reachable and must be rejected.)
        private class InterfaceTypedVM : BaseVM
        {
            public IDataContext? Ctx => base.DC;
        }

        [TestMethod]
        public void IsPathAllowed_InterfaceTypedGatewayAlias_ReturnsFalse()
        {
            var vm = new InterfaceTypedVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Ctx.CurrentUserCode"),
                "#867/PR#884 review shape 3 (interface-typed member): 'Ctx' is declared on " +
                "InterfaceTypedVM and resolves to the INTERFACE type IDataContext (not a concrete " +
                "class) — the gateway-type check must still reject it via IsAssignableFrom, not " +
                "exact-type equality.");
        }

        // Shape 4: intermediate base class — the alias is declared on a class BETWEEN BaseVM and
        // the concrete VM, so a check scoped only to "BaseVM or the concrete type" would miss it.
        private class IntermediateBaseVM : BaseVM
        {
            public Configs? InheritedSettings => base.ConfigInfo;
        }

        private class ConcreteFromIntermediateVM : IntermediateBaseVM
        {
            // Intentionally empty — the alias lives on IntermediateBaseVM, neither on BaseVM
            // itself nor on this concrete leaf type.
        }

        [TestMethod]
        public void IsPathAllowed_AliasOnIntermediateBaseClass_ReturnsFalse()
        {
            var vm = new ConcreteFromIntermediateVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "InheritedSettings.IsQuickDebug"),
                "#867/PR#884 review shape 4 (intermediate base class): 'InheritedSettings' is " +
                "declared on IntermediateBaseVM — neither BaseVM nor the concrete leaf type — but " +
                "resolves to type Configs, so the gateway-type check must still reject it " +
                "regardless of which class in the hierarchy declares it.");
        }

        // Shape 5: generic type parameter — a downstream VM closes a generic base's type
        // parameter over one of the banned gateway types.
        private class GenericGatewayBase<TGateway> : BaseVM where TGateway : class
        {
            public TGateway? Gateway { get; set; }
        }

        private class ConcreteGenericVM : GenericGatewayBase<Configs>
        {
        }

        [TestMethod]
        public void IsPathAllowed_GenericTypeParameterClosedOverConfigs_ReturnsFalse()
        {
            var vm = new ConcreteGenericVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Gateway.IsQuickDebug"),
                "#867/PR#884 review shape 5 (generic type parameter): 'Gateway' is declared on the " +
                "OPEN generic GenericGatewayBase<TGateway>, but ConcreteGenericVM closes TGateway " +
                "over Configs — member.GetMemberType() resolves the SUBSTITUTED closed type " +
                "(Configs), not the open parameter, so the gateway-type check must still reject it.");
        }

        // ── options family / IServiceProvider (PR #884 review round 3) ──────

        // Sanity check on the .NET reflection API itself, BEFORE trusting IsBannedGatewayType's
        // open-generic branch to matter at all: Type.IsAssignableFrom does NOT relate an open
        // generic type DEFINITION to any of its closed constructions. Naively adding
        // typeof(IOptionsMonitor<>) to BannedGatewayTypes and leaving the existing
        // IsAssignableFrom-only loop unchanged would silently do nothing — this is exactly what
        // the review warned "do not assume the open generic works" about, and it was verified
        // empirically (not assumed) before IsOrImplementsOpenGenericDefinition was written.
        [TestMethod]
        public void OpenGenericTypeAssignability_ConfirmsIsAssignableFromDoesNotRelateOpenToClosedGenerics()
        {
            var openGeneric = typeof(IOptionsMonitor<>);
            var closedGeneric = typeof(IOptionsMonitor<ActionLogRetentionOptions>);

            Assert.IsFalse(openGeneric.IsAssignableFrom(closedGeneric),
                "Sanity check on the .NET reflection API itself: Type.IsAssignableFrom must NOT " +
                "relate an open generic type definition to a closed construction of it — if this " +
                "ever starts returning true (a .NET runtime behavior change), " +
                "IsOrImplementsOpenGenericDefinition becomes redundant but still correct, so this " +
                "assertion failing would mean the runtime changed, not that the policy broke.");
        }

        // A downstream VM exposing four different gateway types via plain, uncustomized getters —
        // no custom setter needed for any of them. The exact shape the round-3 review named:
        // "public IOptionsMonitor<ActionLogRetentionOptions> Retention => Wtm!.ServiceProvider!
        // .GetRequiredService<...>();" reaches IOptionsMonitor<T>'s own process-wide cached
        // CurrentValue through a 3-segment key that, before this round, never touched any type on
        // BannedGatewayTypes at all.
        private class OptionsFamilyVM : BaseVM
        {
            public IOptionsMonitor<ActionLogRetentionOptions>? Retention { get; set; }
            public IOptionsSnapshot<ActionLogRetentionOptions>? Snapshot { get; set; }
            public IOptions<ActionLogRetentionOptions>? Opt { get; set; }
            public IServiceProvider? Sp { get; set; }
        }

        [TestMethod]
        public void IsPathAllowed_OptionsMonitorCurrentValue_ReturnsFalse()
        {
            // The issue's own round-3 exploit shape (ActionLogRetentionOptions.NormalDays — site-
            // wide ActionLog-retention destruction via one form field): "Retention" resolves to
            // IOptionsMonitor<ActionLogRetentionOptions>, a closed construction of the open
            // generic IOptionsMonitor<>, matched via IsOrImplementsOpenGenericDefinition.
            var vm = new OptionsFamilyVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Retention.CurrentValue.NormalDays"),
                "#867/PR#884 review round 3: 'Retention' resolves to " +
                "IOptionsMonitor<ActionLogRetentionOptions> — a closed construction of the open " +
                "generic IOptionsMonitor<> — and must be rejected via " +
                "IsOrImplementsOpenGenericDefinition, since Type.IsAssignableFrom cannot relate " +
                "the open generic definition to this closed form.");
        }

        [TestMethod]
        public void IsPathAllowed_OptionsSnapshotValue_ReturnsFalse()
        {
            var vm = new OptionsFamilyVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Snapshot.Value.NormalDays"),
                "#867/PR#884 review round 3: 'Snapshot' resolves to " +
                "IOptionsSnapshot<ActionLogRetentionOptions> — must be rejected the same way as " +
                "IOptionsMonitor<>.");
        }

        [TestMethod]
        public void IsPathAllowed_OptionsValue_ReturnsFalse()
        {
            var vm = new OptionsFamilyVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Opt.Value.NormalDays"),
                "#867/PR#884 review round 3: 'Opt' resolves to IOptions<ActionLogRetentionOptions> " +
                "— must be rejected the same way as IOptionsMonitor<>/IOptionsSnapshot<>.");
        }

        [TestMethod]
        public void IsPathAllowed_ServiceProvider_ReturnsFalse()
        {
            // IServiceProvider is a gateway to the entire DI container — listed for the same
            // reason as WTMContext itself. Not generic, so this exercises the ordinary
            // IsAssignableFrom branch, not IsOrImplementsOpenGenericDefinition — and therefore
            // also serves as the positive control that isolates the open-generic mutant below
            // (neutralizing IsOrImplementsOpenGenericDefinition must not affect this test).
            var vm = new OptionsFamilyVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Sp"),
                "#867/PR#884 review round 3: 'Sp' resolves to type IServiceProvider — a banned " +
                "gateway type.");
        }

        // ── ambiguous resolution: fail closed, independent of gateway typing ──

        // Verified empirically that this specific shape — hiding across member KINDS, a base
        // FIELD hidden by a derived PROPERTY of the same name — is what actually makes
        // Type.GetMember(name) return more than one result (2, here). Same-kind hiding
        // (property-hides-property, as in ShadowVM above) does NOT: .NET's reflection resolves
        // that down to the single most-derived member before this policy ever sees it. Neither
        // "Label" candidate below resolves to a banned gateway type (both are plain strings),
        // isolating the ambiguous-resolution guard from the gateway-type guard.
        private class AmbiguousBaseVM : BaseVM
        {
            public string? Label = "base field";
        }

        private class AmbiguousShadowVM : AmbiguousBaseVM
        {
            public new string? Label { get; set; } = "derived property";
        }

        [TestMethod]
        public void IsPathAllowed_FieldHiddenByPropertyOfSameName_ReturnsFalse()
        {
            // Confirms the premise before asserting on it: PropertyHelper.SetPropertyValue's own
            // traversal unconditionally takes GetMember(...)[0], so whichever member metadata
            // ordering puts first is what a real write would use — not a security boundary either
            // of them should rely on.
            var rawMembers = typeof(AmbiguousShadowVM).GetMember("Label");
            Assert.AreEqual(2, rawMembers.Length,
                "Sanity check: this fixture must genuinely produce an ambiguous GetMember(\"Label\") " +
                "result (a base field hidden by a derived property) — otherwise this test would not " +
                "be exercising the ambiguity guard at all.");

            var vm = new AmbiguousShadowVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "Label"),
                "#867/PR#884 review point 6: Type.GetMember can return more than one member for " +
                "the same name (a field hidden by a differently-kinded property is the verified " +
                "concrete case) — this policy must fail closed on ambiguity rather than trust " +
                "members[0] to be the safe one, even when neither candidate individually resolves " +
                "to a banned gateway type.");
        }

        // ── static-member guard (isolated from the gateway-type guard) ──────

        [TestMethod]
        public void IsPathAllowed_PublicStaticField_ReturnsFalse()
        {
            // "StaticSecret" does not resolve to a banned gateway type, and FixtureVM is not
            // BaseVM/BaseSearcher/WTMContext — this isolates the static-member guard from the
            // gateway-type guard, which would not otherwise fire here.
            var vm = new FixtureVM();
            Assert.IsFalse(RequestBindingPolicy.IsPathAllowed(vm, "StaticSecret"),
                "#867: Type.GetMember defaults to Public|Instance|Static, so a public static " +
                "field is reachable through an instance path and must be rejected independently " +
                "of the gateway-type check.");
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
            // the other guards. Still needed after the gateway-TYPE rewrite: the type check closes
            // the "reachable alias" bypass, it does not bound how deep a chain of ordinary,
            // non-gateway-typed properties can run before this policy has to give up walking it —
            // see RequestBindingPolicy's own class doc comment.
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
            // downstream subclass), and their type (int/long) is not a banned gateway type —
            // confirms BannedGatewayTypes is curated by TYPE, not "everything declared on
            // BaseSearcher."
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
