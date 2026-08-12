#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Issue #867: <c>WalkingTec.Mvvm.Mvc.BaseController.RedoUpdateModel</c> (and its identical
    /// copy, <c>WalkingTec.Mvvm.Mvc.BaseApiController.RedoUpdateModel</c>) writes every
    /// caller-supplied form/query key onto a <see cref="BaseVM"/> via
    /// <see cref="PropertyHelper.SetPropertyValue(object, string, object, string, bool)"/>, which
    /// follows dotted paths using only a getter to traverse each intermediate hop — a
    /// <c>get</c>-only property is therefore NOT protection, because the write lands on the live
    /// object the getter returns, not on the property itself. Several of those intermediate hops
    /// (<see cref="BaseVM.Wtm"/>, <see cref="BaseVM.ConfigInfo"/>, <c>WTMContext.GlobaInfo</c>, …)
    /// resolve to process-wide DI singletons (<c>IOptionsMonitor&lt;Configs&gt;.CurrentValue</c>,
    /// <c>GlobalData</c>) — so an unguarded dotted-path write from ANY authenticated caller can
    /// flip an application-wide security setting (e.g. <c>Configs.IsQuickDebug</c>) for every
    /// user, until process restart.
    ///
    /// <para>
    /// <b>Why this rejects by the TYPE each hop resolves to, not by the property's name or where
    /// it is declared (PR #884 cross-vendor review finding, fixed before merge).</b> An earlier
    /// version of this policy checked <c>member.DeclaringType</c> plus a curated name set. That is
    /// a denylist wearing an allowlist's clothes: a downstream VM can legally re-expose the exact
    /// same singleton under any name it likes —
    /// <c>public Configs? Settings =&gt; base.ConfigInfo;</c> — and <c>Settings</c> is declared on
    /// the downstream VM, not <see cref="BaseVM"/>, so a declaring-type check never sees it.
    /// <c>new</c>-shadowing, an intermediate base class between <see cref="BaseVM"/> and the
    /// concrete VM, and a generic type parameter that happens to close over one of these types all
    /// defeat a declaring-type/name check the same way, because none of them change WHERE the
    /// dangerous value ultimately comes from. The property's <i>name</i> and <i>declaring type</i>
    /// are both attacker-influenced (any downstream <see cref="BaseVM"/>/<see cref="BaseSearcher"/>
    /// subclass not bounded by this repository can add either); the <i>type</i> the getter actually
    /// returns is not — <c>Settings</c> above is still typed <see cref="Configs"/> however it is
    /// declared, named, or reached. <see cref="PropertyHelper.SetPropertyValue"/>'s own traversal
    /// (<c>PropertyHelper.cs:523-551</c>) already resolves the next hop's type this same way — via
    /// <c>member.GetMemberType()</c>, not <c>member.DeclaringType</c> — so checking the resolved
    /// type at every hop against <see cref="BannedGatewayTypes"/> cannot be defeated by any of the
    /// renaming/hiding/re-declaring tricks that defeated the earlier declaring-type/name check: see
    /// <c>RequestBindingPolicyTests867</c>'s alias/shadowing/interface/intermediate-base/generic
    /// tests, each built to prove one specific such trick no longer works. <b>This proves only
    /// that those tricks are closed — it is not a claim that <see cref="BannedGatewayTypes"/>'s
    /// own coverage is complete.</b> See "Known, documented limitation" below for the two
    /// independent ways a type denylist itself can still be incomplete.
    /// </para>
    ///
    /// <para>
    /// <b>Why <see cref="BannedGatewayTypes"/> is a curated type list, not "every reference type."
    /// </b> <see cref="BaseSearcher"/> itself directly declares the framework's own designed
    /// binding surface — <c>Page</c>, <c>Limit</c>, <c>SortInfo</c>, and friends, exactly what a
    /// LayUI DataTable POST needs to reach via <c>Searcher.SortInfo.Property</c> (the deepest
    /// verified-legitimate payload) — and none of those types are gateways to a larger,
    /// shared/process-wide object graph, so they are not banned. Only the specific types that ARE
    /// such gateways (the framework context, its configuration, global framework data, the current
    /// login identity, the data context, and the per-request infrastructure services) are listed.
    /// </para>
    ///
    /// <para>
    /// <b>Static members.</b> <c>Type.GetMember(name)</c> defaults to
    /// <c>BindingFlags.Public | Instance | Static</c>, so a <c>public static</c> member (e.g.
    /// <c>WTMContext.ReloadUserFunc</c>) is reachable through an ordinary instance path even
    /// though it has nothing to do with the instance being traversed. Rejected independently of
    /// the type check, at every hop.
    /// </para>
    ///
    /// <para>
    /// <b>Ambiguous resolution.</b> <c>Type.GetMember(name)</c> can return more than one member
    /// for the same name. Verified empirically which shape actually causes that (not assumed):
    /// same-kind hiding — a property hidden by a <c>new</c> property of the same name — does
    /// NOT; .NET's reflection resolves that down to the single most-derived member before this
    /// policy (or <see cref="PropertyHelper"/>) ever sees it. Hiding ACROSS member kinds — a base
    /// class field hidden by a derived class property of the same name, or vice versa — does:
    /// confirmed via <c>typeof(...).GetMember(name).Length == 2</c> in
    /// <c>RequestBindingPolicyTests867.IsPathAllowed_FieldHiddenByPropertyOfSameName_ReturnsFalse</c>.
    /// <see cref="PropertyHelper"/>'s own traversal (<c>PropertyHelper.cs:528</c>,
    /// <c>PropertyHelper.cs:559</c>) unconditionally takes <c>members[0]</c> whenever this
    /// happens, so this policy's check and the actual write always agree with each other on WHICH
    /// member that is — but "whichever one metadata ordering happens to put first" is not a
    /// security boundary either of them should rely on. Any segment with more than one resolved
    /// member is rejected outright (fail closed) rather than trusting <c>[0]</c> to be the safe
    /// one.
    /// </para>
    ///
    /// <para>
    /// <b>Depth cap — kept, not made redundant by the type check.</b> The type check closes the
    /// "reachable alias" bypass; it does not bound how deep a chain of ordinary, non-gateway-typed
    /// properties can run before this policy has to give up walking it, and this repository cannot
    /// enumerate every type a downstream VM might ever expose. <c>Searcher.SortInfo.Property</c>
    /// (3 segments) is the deepest verified-legitimate payload this framework's own LayUI DataTable
    /// front end sends (regular grids post the unprefixed 2-segment <c>SortInfo.Property</c>; only
    /// Selector-mode grids add the <c>Searcher.</c> prefix — verified against
    /// <c>framework_layui.js</c>/<c>DataTableTagHelper.cs</c>), so paths longer than 3 segments are
    /// still rejected outright regardless of what they resolve to.
    /// </para>
    ///
    /// <para>
    /// <b>Known, documented limitation, widened after PR #884 review round 3.</b> A type denylist
    /// cannot enumerate every gateway a downstream VM may surface. It fails in two independent
    /// ways:
    /// </para>
    /// <para>
    /// <b>(a) An innocuous declared type whose setter's BODY writes into shared state anyway</b>
    /// (found in round 2). Example: <c>public List&lt;string&gt; SharedPublicUrls { get =&gt;
    /// Wtm!.GlobaInfo!.AllAccessUrls; set =&gt; Wtm!.GlobaInfo!.AllAccessUrls = value; }</c> —
    /// <c>SharedPublicUrls</c>'s resolved type is <c>List&lt;string&gt;</c>, not itself a banned
    /// type, so a single-segment key naming it passes this policy; its setter then overwrites
    /// <see cref="GlobalData"/>'s own <c>AllAccessUrls</c> directly. The setter's logic is opaque
    /// to <c>Type.GetMember</c>/<c>PropertyInfo.PropertyType</c> no matter how large
    /// <see cref="BannedGatewayTypes"/> grows — extending the list cannot close this failure mode,
    /// because the danger lives in code the list was never going to inspect.
    /// </para>
    /// <para>
    /// <b>(b) A declared type that IS itself a gateway but was not yet on the list</b> — this one
    /// needs no custom setter at all, a plain uncustomized getter is enough (found and its
    /// concrete instance closed in round 3). Example:
    /// <c>public IOptionsMonitor&lt;ActionLogRetentionOptions&gt; Retention =&gt;
    /// Wtm!.ServiceProvider!.GetRequiredService&lt;IOptionsMonitor&lt;ActionLogRetentionOptions&gt;&gt;();
    /// </c> — the 3-segment key <c>Retention.CurrentValue.NormalDays</c> walked as
    /// <c>IOptionsMonitor&lt;ActionLogRetentionOptions&gt;</c> → <c>ActionLogRetentionOptions</c>
    /// → <c>int</c>, none of which were on <see cref="BannedGatewayTypes"/> before round 3, and
    /// landed on <c>IOptionsMonitor&lt;T&gt;</c>'s own process-wide cached <c>CurrentValue</c> —
    /// site-wide ActionLog-retention destruction via one form field. <c>IOptionsMonitor&lt;&gt;</c>/
    /// <c>IOptionsSnapshot&lt;&gt;</c>/<c>IOptions&lt;&gt;</c>/<see cref="IServiceProvider"/> are
    /// now on the list — but this closes only THIS instance of failure mode (b), not the failure
    /// mode itself: a downstream VM, or a future dependency of this framework, can always
    /// introduce a new gateway type this list has not yet been told about. There is no known
    /// finite type list that provably enumerates every such gateway.
    /// </para>
    /// <para>
    /// No forwarding-property or gateway-typed alias of either shape exists anywhere in this
    /// repository today (confirmed by <c>git grep</c> across all three review rounds) — but
    /// nothing stops a downstream <see cref="BaseVM"/>/<see cref="BaseSearcher"/> subclass from
    /// adding one of either kind. This PR does not attempt to close either failure mode
    /// structurally: doing so needs either a real positive binding-contract (explicit per-VM
    /// annotation of which members <c>RedoUpdateModel</c> may write, a breaking change of a
    /// different magnitude) or making <c>Configs</c>/<see cref="GlobalData"/>/the DI container's
    /// own options cache immutable at the DI boundary after startup (the architectural fix Issue
    /// #867's own original analysis already identified and deliberately deferred, precisely so
    /// this narrower fix could ship first). See the CHANGELOG's #867 entry and Issue #889
    /// (widened in round 3 to cover both failure modes) for the same disclosure.
    /// </para>
    ///
    /// <para>
    /// <b>Issue #1080: rejections now carry a reason, not just true/false.</b> With
    /// <c>Configs.EnforceRequestBindingScope</c> defaulting to <see langword="true"/> since
    /// v10.22.0, every ordinary LayUI grid paging request rejects several framework transport
    /// keys that are not VM properties (<c>_DONOT_USE_CS</c>, <c>_DONOT_USE_VMNAME</c>,
    /// <c>__RequestVerificationToken</c>, <c>page</c>, <c>limit</c>, …) — and the two controller
    /// <c>RedoUpdateModel</c> methods used to log a Warning for every one of them, on every
    /// normal request. A security-relevant logger that fires on ordinary traffic trains people to
    /// ignore it. <see cref="Classify(object, string, string)"/>/
    /// <see cref="Classify(Type, string, string)"/> classify WHY a key is rejected into a
    /// <see cref="BindingRejectionReason"/>; <see cref="IsPathAllowed(object, string, string)"/>/
    /// <see cref="IsPathAllowed(Type, string, string)"/> are now pure boolean delegations to
    /// them, so the allow/deny decision and the reason it is computed from share exactly one
    /// walk — not two copies that can drift the way the declaring-type/name check above already
    /// did once. The two controllers log <c>Debug</c> instead of <c>Warning</c> for exactly one
    /// reason, <see cref="BindingRejectionReason.NoWritableTarget"/>, which is the only one where
    /// the FINAL segment is mechanically provable (from <c>PropertyHelper.cs:554-557</c>) to
    /// resolve to no member — see <see cref="BindingRejectionReason.NoWritableTarget"/>'s own doc
    /// comment (updated for Issue #1098) for why that is a true no-write guarantee for
    /// single-segment keys but not for multi-segment ones, where the intermediate loop can still
    /// instantiate an intermediate object as a side effect before the final segment is ever
    /// checked; every other reason — including a segment name that merely LOOKS like a harmless
    /// typo — stays exactly as loud as before. The set of rejected keys does not change: this is
    /// a logging-severity change, not a policy change.
    /// </para>
    /// </summary>
    public static partial class RequestBindingPolicy
    {
        /// <summary>
        /// <c>Searcher.SortInfo.Property</c>/<c>Searcher.SortInfo.Direction</c> (3 segments) is
        /// the deepest dotted path the framework's own LayUI DataTable front end legitimately
        /// sends through <c>RedoUpdateModel</c>. Anything deeper is rejected outright.
        /// </summary>
        public const int MaxDepth = 3;

        /// <summary>
        /// Types that are gateways into a larger, shared, or process-wide object graph rather than
        /// VM-local/Searcher-local state. A dotted-path segment whose resolved type IS one of
        /// these, or is assignable to one of these (covers a downstream subtype, and covers an
        /// interface-typed member whose declared type implements one of the interfaces below), is
        /// never part of the framework's designed request-binding surface — regardless of the
        /// segment's name or which class declares it. See the class doc comment for the PR #884
        /// review finding this replaced a declaring-type/name check to close.
        /// <para>
        /// <b>Open generic entries (PR #884 review, round 3):</b> <c>IOptionsMonitor&lt;&gt;</c>,
        /// <c>IOptionsSnapshot&lt;&gt;</c>, and <c>IOptions&lt;&gt;</c> are generic type
        /// DEFINITIONS, not closed types — a downstream VM exposing
        /// <c>IOptionsMonitor&lt;ActionLogRetentionOptions&gt;</c> as a plain, uncustomized getter
        /// (no setter needed at all) reaches <c>IOptionsMonitor&lt;T&gt;</c>'s own
        /// <c>CurrentValue</c> — a process-wide cached singleton for every options type, not just
        /// <see cref="Configs"/> — and none of <c>ActionLogRetentionOptions</c>'s own properties
        /// need to be on this list for that write to land on shared state. <see cref="Type.IsAssignableFrom"/>
        /// does NOT relate an open generic type definition to any of its closed constructions
        /// (verified empirically — <c>typeof(IOptionsMonitor&lt;&gt;).IsAssignableFrom(typeof(IOptionsMonitor&lt;Configs&gt;))</c>
        /// returns <see langword="false"/>), so these three entries are matched by
        /// <see cref="IsOrImplementsOpenGenericDefinition"/> instead of the plain
        /// <see cref="Type.IsAssignableFrom"/> loop the rest of this list uses — see
        /// <see cref="IsBannedGatewayType"/>. <see cref="IServiceProvider"/> is listed alongside
        /// them for the same reason as <see cref="WTMContext"/> itself: it is a gateway to the
        /// entire DI container (any registered service, not merely the options types explicitly
        /// listed here), and no VM-local/Searcher-local binding surface legitimately needs it —
        /// confirmed by <c>grep -rn "IOptionsMonitor&lt;\|IOptionsSnapshot&lt;\|IOptions&lt;\|IServiceProvider" src/ demo/ test/</c>
        /// finding no <see cref="BaseVM"/>/<see cref="BaseSearcher"/> subclass anywhere in this
        /// repository that exposes any of them.
        /// </para>
        /// </summary>
        private static readonly Type[] BannedGatewayTypes =
        {
            typeof(WTMContext),
            typeof(Configs),
            typeof(GlobalData),
            typeof(LoginUserInfo),
            typeof(IDataContext),
            typeof(ISessionService),
            typeof(IModelStateService),
            typeof(IDistributedCache),
            typeof(IStringLocalizer),
            typeof(IUIService),
            typeof(IServiceProvider),
            typeof(IOptionsMonitor<>),
            typeof(IOptionsSnapshot<>),
            typeof(IOptions<>),
        };

        [GeneratedRegex(@"\[[^\]]*\]")]
        private static partial Regex IndexerBracketRegex();

        /// <summary>
        /// Returns <c>false</c> when <paramref name="property"/> (a caller-supplied
        /// <c>RedoUpdateModel</c> form/query key, optionally dotted) must be rejected because
        /// resolving it against <paramref name="source"/>'s actual type would traverse through a
        /// segment whose resolved type is one of <see cref="BannedGatewayTypes"/>, a
        /// <c>static</c> member, an ambiguously-resolved member name, a segment that fails to
        /// resolve at all, or a path exceeding <see cref="MaxDepth"/> segments.
        /// <para>
        /// <b>Issue #1080: pure delegation.</b> This is a thin boolean wrapper around
        /// <see cref="Classify(object, string, string)"/> — literally
        /// <c>Classify(...) == BindingRejectionReason.None</c> — kept as one shared classifier
        /// rather than two parallel implementations, since a duplicated criterion is exactly the
        /// failure mode the declaring-type/name check earlier in this file's history already hit
        /// once (see the class doc comment). Most call sites only need this allow/deny decision;
        /// a caller that also needs to know WHY — <c>RedoUpdateModel</c>'s per-key logging is the
        /// reason this distinction exists at all — should call
        /// <see cref="Classify(object, string, string)"/> directly. See
        /// <see cref="Classify(Type, string, string)"/>'s own doc comment for the full per-hop
        /// algorithm.
        /// </para>
        /// </summary>
        public static bool IsPathAllowed(object? source, string? property, string? prefix = null)
            => Classify(source, property, prefix) == BindingRejectionReason.None;

        /// <inheritdoc cref="IsPathAllowed(object, string, string)"/>
        public static bool IsPathAllowed(Type? sourceType, string? property, string? prefix = null)
            => Classify(sourceType, property, prefix) == BindingRejectionReason.None;

        /// <summary>
        /// <see cref="Classify(Type, string, string)"/> overload that accepts an instance instead
        /// of its <see cref="Type"/>. A <see langword="null"/> <paramref name="source"/> has
        /// nothing to traverse — <c>PropertyHelper.SetPropertyValue</c> itself no-ops when
        /// <c>source == null || property == null</c> — so there is nothing to reject either;
        /// returns <see cref="BindingRejectionReason.None"/>, not a rejection.
        /// </summary>
        public static BindingRejectionReason Classify(object? source, string? property, string? prefix = null)
        {
            return source == null ? BindingRejectionReason.None : Classify(source.GetType(), property, prefix);
        }

        /// <summary>
        /// Classifies why <paramref name="property"/> (a caller-supplied <c>RedoUpdateModel</c>
        /// form/query key, optionally dotted) would be rejected when resolved against
        /// <paramref name="sourceType"/>, or returns <see cref="BindingRejectionReason.None"/>
        /// when it is allowed. This is the ONLY place the walk is implemented —
        /// <see cref="IsPathAllowed(Type, string, string)"/> is a pure delegation to it. The
        /// normalization (indexer strip, dot-split, prefix insert) and the per-hop TYPE
        /// progression on a successfully-resolved segment match
        /// <see cref="PropertyHelper.SetPropertyValue(object, string, object, string, bool)"/>'s
        /// own exactly; the REJECTED set is unchanged from before this method existed — see the
        /// class doc comment's "Issue #1080" paragraph for what changed (the reason granularity,
        /// not the set).
        /// </summary>
        public static BindingRejectionReason Classify(Type? sourceType, string? property, string? prefix = null)
        {
            if (sourceType == null || string.IsNullOrEmpty(property)) return BindingRejectionReason.None;

            // Mirror PropertyHelper.SetPropertyValue's own normalization exactly (indexer strip,
            // then dot-split, then optional prefix) so the segments inspected here are the same
            // segments that method would traverse.
            var normalized = IndexerBracketRegex().Replace(property, string.Empty);
            List<string> level = [];
            if (normalized.Contains('.'))
            {
                level.AddRange(normalized.Split('.'));
            }
            else
            {
                level.Add(normalized);
            }
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                level.Insert(0, prefix);
            }

            if (level.Count > MaxDepth) return BindingRejectionReason.PathTooDeep;

            // Every type PropertyHelper.SetPropertyValue's OWN intermediate loop
            // (PropertyHelper.cs:523-551) could possibly have its traversal type frozen at when it
            // goes on to resolve the FINAL segment: sourceType itself, plus the resolved type of
            // every intermediate hop that fully clears every guard below (unique member,
            // non-static, non-gateway). That loop can `break` early for a reason this
            // Type/name-only classification cannot observe — a resolved intermediate member whose
            // current VALUE is null and whose type has no public parameterless constructor —
            // leaving tempType at whichever earlier candidate the loop had reached. This list is
            // every such candidate, not a guess at which one the real traversal would pick.
            List<Type> traversalTypes = [sourceType];

            // Starts as the caller's own type; advanced to member.GetMemberType() (the resolved
            // TYPE, not the declaring type) after each hop that resolves cleanly below — same
            // progression PropertyHelper.SetPropertyValue itself uses to decide what the next hop
            // resolves against. This only matches on the SUCCESS path; see the zero-resolution
            // branch below for where this walk deliberately diverges (more conservatively) from
            // what SetPropertyValue itself does when a segment fails to resolve.
            Type? tempType = sourceType;
            for (var i = 0; i < level.Count; i++)
            {
                if (tempType == null) break;

                var isLastSegment = i == level.Count - 1;
                var segment = level[i];

                // Same resolution PropertyHelper.SetPropertyValue uses (default BindingFlags —
                // Public | Instance | Static — which is exactly why the static check below is
                // needed independently of the type check).
                var members = tempType.GetMember(segment);
                if (members.Length == 0)
                {
                    if (!isLastSegment)
                    {
                        // PR #884 review, round 2: an unresolved INTERMEDIATE segment must stay
                        // loud, never a silent no-op. PropertyHelper.SetPropertyValue's
                        // intermediate loop does NOT stop the write here: tempType/temp are simply
                        // left at whatever they were before this failed hop (the ORIGINAL source,
                        // if it is the very first segment that fails), and execution falls through
                        // to resolve and WRITE the FINAL segment against that frozen type
                        // (PropertyHelper.cs:553-559). A key like "Missing.StaticSecret" therefore
                        // still writes StaticSecret onto the VM itself even though "Missing" never
                        // resolved to anything — verified empirically, see
                        // RequestBindingPolicyTests867.MissingIntermediateSegment_ActuallyWritesFinalSegmentOnVm_WhenPolicyIsIgnored.
                        return BindingRejectionReason.UnresolvedIntermediateSegment;
                    }

                    // Last segment resolves to zero members against the DEEPEST traversal type.
                    // A real write of the FINAL segment's VALUE can land only if some type the
                    // real traversal could have frozen at — i.e. some entry already collected in
                    // traversalTypes — resolves this exact segment name too. If NONE of them do,
                    // then no matter which of those types the real (value-dependent) traversal
                    // actually froze at, tempType.GetMember(level.Last()) also finds nothing
                    // there, and PropertyHelper.cs:554-557 (`if (!memberInfos.Any()) { return; }`)
                    // returns without writing that value. That is the only write this method can
                    // prove is suppressed — for a MULTI-segment key it does NOT prove the object
                    // graph is untouched: the intermediate loop (PropertyHelper.cs:523-551) can
                    // still instantiate and attach a new intermediate object before this check is
                    // ever reached. See BindingRejectionReason.NoWritableTarget's own doc comment
                    // (Issue #1098) for the single- vs. multi-segment distinction.
                    foreach (var candidate in traversalTypes)
                    {
                        if (candidate.GetMember(segment).Length > 0)
                        {
                            // Resolves against a SHALLOWER type in the chain — a frozen-type write
                            // there is possible, so this must stay just as loud as case (1) above.
                            return BindingRejectionReason.UnresolvedIntermediateSegment;
                        }
                    }
                    return BindingRejectionReason.NoWritableTarget;
                }
                if (members.Length > 1)
                {
                    // Ambiguous resolution (verified concrete cause: a field hidden by a
                    // differently-kinded property of the same name — see the class doc comment
                    // for why plain same-kind new-hiding does NOT trigger this) — fail closed
                    // rather than trust members[0] to be the safe one.
                    return BindingRejectionReason.AmbiguousMember;
                }
                var member = members[0];

                if (IsStaticMember(member)) return BindingRejectionReason.StaticMember;

                var memberType = member.GetMemberType();
                if (IsBannedGatewayType(memberType)) return BindingRejectionReason.GatewayType;

                tempType = memberType;
                if (!isLastSegment && tempType != null)
                {
                    traversalTypes.Add(tempType);
                }
            }

            return BindingRejectionReason.None;
        }

        /// <summary>
        /// True when <paramref name="memberType"/> IS, or is assignable to (implements/derives
        /// from), one of <see cref="BannedGatewayTypes"/> — catches an exact match, a downstream
        /// subtype, and an interface-typed member whose declared type implements one of the
        /// interfaces in the set. This is what makes the check alias/shadowing/interface/
        /// intermediate-base/generic-parameter-proof: none of those tricks can change what TYPE
        /// the getter's declared return type actually is.
        /// <para>
        /// Entries that are open generic type DEFINITIONS (<see cref="Type.IsGenericTypeDefinition"/>,
        /// e.g. <c>typeof(IOptionsMonitor&lt;&gt;)</c>) are matched via
        /// <see cref="IsOrImplementsOpenGenericDefinition"/> instead of
        /// <see cref="Type.IsAssignableFrom"/> — the latter does not relate an open generic
        /// definition to any of its closed constructions (verified empirically; see the
        /// <see cref="BannedGatewayTypes"/> doc comment).
        /// </para>
        /// </summary>
        private static bool IsBannedGatewayType(Type? memberType)
        {
            if (memberType == null) return false;

            foreach (var banned in BannedGatewayTypes)
            {
                if (banned.IsGenericTypeDefinition)
                {
                    if (IsOrImplementsOpenGenericDefinition(memberType, banned)) return true;
                }
                else if (banned.IsAssignableFrom(memberType))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="type"/> is itself a closed construction of
        /// <paramref name="openGenericDefinition"/> (covers a member DECLARED as the generic
        /// interface/class directly, e.g. a property typed <c>IOptionsMonitor&lt;T&gt;</c>), OR
        /// implements it via one of its interfaces (covers a concrete class implementing
        /// <c>IOptionsMonitor&lt;T&gt;</c> without being it), OR derives from a closed
        /// construction of it somewhere in its base-type chain (covers a class deriving from a
        /// generic base). <see cref="Type.IsAssignableFrom"/> cannot express any of these three
        /// relationships when <paramref name="openGenericDefinition"/> is an open generic type
        /// definition — it always returns <see langword="false"/> — which is why this exists as a
        /// separate check rather than folding into the ordinary <see cref="IsBannedGatewayType"/>
        /// loop.
        /// </summary>
        private static bool IsOrImplementsOpenGenericDefinition(Type type, Type openGenericDefinition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == openGenericDefinition)
            {
                return true;
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openGenericDefinition)
                {
                    return true;
                }
            }

            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == openGenericDefinition)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStaticMember(MemberInfo member) => member switch
        {
            PropertyInfo p => (p.GetMethod ?? p.SetMethod)?.IsStatic ?? false,
            FieldInfo f => f.IsStatic,
            _ => false,
        };
    }
}
