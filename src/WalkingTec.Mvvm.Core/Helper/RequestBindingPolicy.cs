#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

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
    /// <b>Why an allowlist keyed on declaring type, not a name blocklist.</b> The same dangerous
    /// target is reachable through many alias key strings — <c>ConfigInfo.X</c>,
    /// <c>Wtm.ConfigInfo.X</c>, <c>Searcher.Wtm.ConfigInfo.X</c>, and deeper through any sub-VM —
    /// and the set of downstream <see cref="BaseVM"/>/<see cref="BaseSearcher"/> subclasses is not
    /// bounded by this repository, so a fixed catalogue of dangerous key STRINGS can never be
    /// exhaustive. Resolving each dotted-path segment via reflection and checking WHERE the
    /// resolved member is declared is alias-proof: every alias above necessarily passes through a
    /// hop whose resolved member is declared on <see cref="BaseVM"/>, <see cref="BaseSearcher"/>,
    /// or <see cref="WTMContext"/> — a downstream VM subclass cannot rename or hide those
    /// inherited members, only add its own (which this policy does not restrict at all).
    /// </para>
    ///
    /// <para>
    /// <b>Why this is a curated name list on <see cref="BaseVM"/>/<see cref="BaseSearcher"/>, not
    /// "every member declared there."</b> <see cref="BaseSearcher"/> itself directly declares the
    /// framework's own designed binding surface — <c>Page</c>, <c>Limit</c>, <c>SortInfo</c>, and
    /// friends, exactly what a LayUI DataTable POST needs to reach via
    /// <c>Searcher.SortInfo.Property</c> (the deepest verified-legitimate payload) — so a blanket
    /// "declared on BaseSearcher ⇒ reject" rule would break ordinary paging and sorting. Only the
    /// specific members that are themselves gateways to a further, larger object graph (the
    /// framework context, its configuration, the current login identity, the data context, …) are
    /// listed in <see cref="BannedGatewayMemberNames"/>. <see cref="WTMContext"/> is banned in
    /// full (any member, not a curated subset) because the framework's designed binding surface
    /// never legitimately needs to reach into it at all — every real payload targets VM-local or
    /// Searcher-local state only.
    /// </para>
    ///
    /// <para>
    /// <b>Static members.</b> <c>Type.GetMember(name)</c> defaults to
    /// <c>BindingFlags.Public | Instance | Static</c>, so a <c>public static</c> member (e.g.
    /// <c>WTMContext.ReloadUserFunc</c>) is reachable through an ordinary instance path even
    /// though it has nothing to do with the instance being traversed. This is rejected
    /// independently of the declaring-type check, at every hop.
    /// </para>
    ///
    /// <para>
    /// <b>Depth cap.</b> <c>Searcher.SortInfo.Property</c> (3 segments) is the deepest verified
    /// legitimate payload this framework's own front end sends, so paths longer than 3 segments
    /// are rejected outright regardless of what they resolve to.
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
        /// Member names that, when declared directly on <see cref="BaseVM"/> or
        /// <see cref="BaseSearcher"/> (not a downstream subclass), are gateways into a larger
        /// object graph rather than VM-local/Searcher-local state, and are therefore never part
        /// of the framework's designed request-binding surface. See the class doc comment for
        /// why this is a curated list rather than "every member declared on those two classes."
        /// </summary>
        private static readonly HashSet<string> BannedGatewayMemberNames = new(StringComparer.Ordinal)
        {
            nameof(BaseVM.Wtm),
            nameof(BaseVM.ConfigInfo),
            "GlobaInfo", // WTMContext.GlobaInfo — not directly on BaseVM/BaseSearcher today, listed
                         // defensively in case a future shortcut property is added there.
            nameof(BaseVM.LoginUserInfo),
            nameof(BaseVM.DC),
            nameof(BaseVM.Session),
            nameof(BaseVM.MSD),
            nameof(BaseVM.Cache),
            nameof(BaseVM.FC),
            nameof(BaseVM.Localizer),
            nameof(BaseVM.UIService),
        };

        [GeneratedRegex(@"\[[^\]]*\]")]
        private static partial Regex IndexerBracketRegex();

        /// <summary>
        /// Returns <c>false</c> when <paramref name="property"/> (a caller-supplied
        /// <c>RedoUpdateModel</c> form/query key, optionally dotted) must be rejected because
        /// resolving it against <paramref name="source"/>'s actual type would traverse through a
        /// member declared on <see cref="BaseVM"/>, <see cref="BaseSearcher"/>, or
        /// <see cref="WTMContext"/> that is a gateway to shared/process-wide state, a static
        /// member, or exceeds <see cref="MaxDepth"/> segments. Mirrors
        /// <see cref="PropertyHelper.SetPropertyValue(object, string, object, string, bool)"/>'s
        /// own path normalization exactly, so what is inspected here is what would actually be
        /// traversed there.
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

            Type? tempType = sourceType;
            foreach (var segment in level)
            {
                if (tempType == null) break;

                // Same resolution PropertyHelper.SetPropertyValue uses (default BindingFlags —
                // Public | Instance | Static — which is exactly why the static check below is
                // needed independently of the declaring-type check).
                var members = tempType.GetMember(segment);
                if (members.Length == 0)
                {
                    // Nothing resolves here — SetPropertyValue's own traversal would stop (middle
                    // hop) or no-op (final hop) too, so there is nothing dangerous to reject.
                    break;
                }
                var member = members[0];

                if (IsStaticMember(member)) return false;
                if (IsBannedGatewayMember(member)) return false;

                tempType = member.GetMemberType();
            }

            return true;
        }

        private static bool IsBannedGatewayMember(MemberInfo member)
        {
            if (member.DeclaringType == typeof(WTMContext))
            {
                // No member of WTMContext is part of the designed binding surface — every real
                // RedoUpdateModel payload targets VM-local/Searcher-local state only.
                return true;
            }

            return (member.DeclaringType == typeof(BaseVM) || member.DeclaringType == typeof(BaseSearcher))
                && BannedGatewayMemberNames.Contains(member.Name);
        }

        private static bool IsStaticMember(MemberInfo member) => member switch
        {
            PropertyInfo p => (p.GetMethod ?? p.SetMethod)?.IsStatic ?? false,
            FieldInfo f => f.IsStatic,
            _ => false,
        };
    }
}
