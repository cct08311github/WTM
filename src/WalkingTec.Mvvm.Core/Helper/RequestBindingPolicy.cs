#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;

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
    /// type at every hop against <see cref="BannedGatewayTypes"/> inspects exactly what the actual
    /// write would traverse, and cannot be defeated by any renaming/hiding/re-declaring trick: see
    /// <c>RequestBindingPolicyTests867</c>'s alias/shadowing/interface/intermediate-base/generic
    /// tests, each built to prove one specific such trick no longer works.
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
    /// <b>Known, documented limitation (PR #884 review, round 2): a forwarding property whose
    /// DECLARED type is not banned can still launder a write into a banned type's shared state
    /// through its setter's body.</b> Example: <c>public List&lt;string&gt; SharedPublicUrls {
    /// get =&gt; Wtm!.GlobaInfo!.AllAccessUrls; set =&gt; Wtm!.GlobaInfo!.AllAccessUrls = value; }
    /// </c> — <c>SharedPublicUrls</c>'s resolved type is <c>List&lt;string&gt;</c>, not itself a
    /// banned type, so a single-segment key naming it passes this policy; its setter then
    /// overwrites <see cref="GlobalData"/>'s own <c>AllAccessUrls</c> — a real, live,
    /// process-wide singleton mutation with the same severity as the original finding. This is a
    /// fundamental limit of ANY policy that inspects reflection metadata (a type, a name, a
    /// declaring class) rather than executing or statically analyzing a setter's actual body: the
    /// setter's logic is opaque to <c>Type.GetMember</c>/<c>PropertyInfo.PropertyType</c>, and
    /// there is no such forwarding property anywhere in this repository today (confirmed by
    /// <c>git grep</c> in both review rounds) for this policy to have missed — but nothing stops
    /// a downstream <see cref="BaseVM"/>/<see cref="BaseSearcher"/> subclass from adding one. This
    /// PR does not attempt to close that gap: doing so needs either a real positive
    /// binding-contract (explicit per-VM annotation of which members <c>RedoUpdateModel</c> may
    /// write, a breaking change of a different magnitude) or making <c>Configs</c>/
    /// <see cref="GlobalData"/> immutable at the DI boundary after startup (the architectural fix
    /// Issue #867's own original analysis already identified and deliberately deferred to a
    /// separate issue, precisely so this narrower fix could ship first). See the CHANGELOG's #867
    /// entry for the same disclosure and the tracking issue for the deferred architectural fix.
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
        };

        [GeneratedRegex(@"\[[^\]]*\]")]
        private static partial Regex IndexerBracketRegex();

        /// <summary>
        /// Returns <c>false</c> when <paramref name="property"/> (a caller-supplied
        /// <c>RedoUpdateModel</c> form/query key, optionally dotted) must be rejected because
        /// resolving it against <paramref name="source"/>'s actual type would traverse through a
        /// segment whose resolved type is one of <see cref="BannedGatewayTypes"/>, a
        /// <c>static</c> member, an ambiguously-resolved member name, a segment that fails to
        /// resolve at all, or a path exceeding <see cref="MaxDepth"/> segments. The normalization
        /// (indexer strip, dot-split, prefix insert) and the per-hop TYPE progression on a
        /// successfully-resolved segment match
        /// <see cref="PropertyHelper.SetPropertyValue(object, string, object, string, bool)"/>'s
        /// own exactly. They deliberately do NOT match on a resolution FAILURE: SetPropertyValue
        /// leaves its traversal type frozen and falls through to evaluate the final segment
        /// against it (see the zero-resolution branch below for why replicating that exactly
        /// would be both harder to keep correct and less safe than simply rejecting outright).
        /// </summary>
        public static bool IsPathAllowed(object? source, string? property, string? prefix = null)
        {
            // A null source has nothing to traverse — SetPropertyValue itself no-ops on null
            // (source == null || property == null) return; — so there is nothing dangerous to
            // reject here either.
            return source == null || IsPathAllowed(source.GetType(), property, prefix);
        }

        /// <inheritdoc cref="IsPathAllowed(object, string, string)"/>
        public static bool IsPathAllowed(Type? sourceType, string? property, string? prefix = null)
        {
            if (sourceType == null || string.IsNullOrEmpty(property)) return true;

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

            if (level.Count > MaxDepth) return false;

            // Starts as the caller's own type; advanced to member.GetMemberType() (the resolved
            // TYPE, not the declaring type) after each hop that resolves cleanly below — same
            // progression PropertyHelper.SetPropertyValue itself uses to decide what the next hop
            // resolves against. This only matches on the SUCCESS path; see the zero-resolution
            // branch below for where this walk deliberately diverges (more conservatively) from
            // what SetPropertyValue itself does when a segment fails to resolve.
            Type? tempType = sourceType;
            foreach (var segment in level)
            {
                if (tempType == null) break;

                // Same resolution PropertyHelper.SetPropertyValue uses (default BindingFlags —
                // Public | Instance | Static — which is exactly why the static check below is
                // needed independently of the type check).
                var members = tempType.GetMember(segment);
                if (members.Length == 0)
                {
                    // PR #884 review, round 2: this used to `break` and fall through to `return
                    // true` — WRONG. PropertyHelper.SetPropertyValue's intermediate loop
                    // (PropertyHelper.cs:523-551) also `break`s when a middle segment fails to
                    // resolve, but it does NOT stop the write there: tempType/temp are simply
                    // left at whatever they were before this failed hop (the ORIGINAL source, if
                    // it is the very first segment that fails), and execution falls through to
                    // resolve and WRITE the FINAL segment against that frozen type
                    // (PropertyHelper.cs:553-559). A key like "Missing.StaticSecret" therefore
                    // still writes StaticSecret onto the VM itself even though "Missing" never
                    // resolved to anything — verified empirically, see
                    // RequestBindingPolicyTests867.MissingIntermediateSegment_ActuallyWritesFinalSegmentOnVm_WhenPolicyIsIgnored.
                    // This policy cannot safely allow a key it failed to fully resolve, so it
                    // fails closed here instead of trying to replicate that frozen-type fallback
                    // (which would also have to be kept in lockstep with PropertyHelper forever).
                    return false;
                }
                if (members.Length > 1)
                {
                    // Ambiguous resolution (verified concrete cause: a field hidden by a
                    // differently-kinded property of the same name — see the class doc comment
                    // for why plain same-kind new-hiding does NOT trigger this) — fail closed
                    // rather than trust members[0] to be the safe one.
                    return false;
                }
                var member = members[0];

                if (IsStaticMember(member)) return false;

                var memberType = member.GetMemberType();
                if (IsBannedGatewayType(memberType)) return false;

                tempType = memberType;
            }

            return true;
        }

        /// <summary>
        /// True when <paramref name="memberType"/> IS, or is assignable to (implements/derives
        /// from), one of <see cref="BannedGatewayTypes"/> — catches an exact match, a downstream
        /// subtype, and an interface-typed member whose declared type implements one of the
        /// interfaces in the set. This is what makes the check alias/shadowing/interface/
        /// intermediate-base/generic-parameter-proof: none of those tricks can change what TYPE
        /// the getter's declared return type actually is.
        /// </summary>
        private static bool IsBannedGatewayType(Type? memberType)
        {
            if (memberType == null) return false;

            foreach (var banned in BannedGatewayTypes)
            {
                if (banned.IsAssignableFrom(memberType)) return true;
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
