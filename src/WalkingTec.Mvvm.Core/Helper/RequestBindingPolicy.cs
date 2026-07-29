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
    /// for the same name (most concretely: <c>new</c>-hiding). <see cref="PropertyHelper"/>'s own
    /// traversal unconditionally takes <c>members[0]</c>, so this policy's check and the actual
    /// write always agree with each other on WHICH member is used — but "which one metadata
    /// ordering happens to put first" is not a security boundary either of them should rely on.
    /// Any segment with more than one resolved member is rejected outright (fail closed) rather
    /// than trusting <c>[0]</c> to be the safe one.
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
        /// <c>static</c> member, an ambiguously-resolved member name, or a path exceeding
        /// <see cref="MaxDepth"/> segments. Mirrors
        /// <see cref="PropertyHelper.SetPropertyValue(object, string, object, string, bool)"/>'s
        /// own path normalization and per-hop type resolution exactly, so what is inspected here
        /// is what would actually be traversed there.
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
            // TYPE, not the declaring type) after each hop below — same progression
            // PropertyHelper.SetPropertyValue itself uses to decide what the next hop resolves
            // against, so this walk and the real write are always looking at the same thing.
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
                    // Nothing resolves here — SetPropertyValue's own traversal would stop (middle
                    // hop) or no-op (final hop) too, so there is nothing dangerous to reject.
                    break;
                }
                if (members.Length > 1)
                {
                    // Ambiguous resolution (e.g. new-hiding) — fail closed rather than trust
                    // members[0] to be the safe one. See the class doc comment.
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
