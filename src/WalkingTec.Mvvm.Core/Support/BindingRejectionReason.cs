#nullable enable
using System;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Issue #1080: why <see cref="RequestBindingPolicy.Classify(object, string, string)"/> /
    /// <see cref="RequestBindingPolicy.Classify(Type, string, string)"/> rejected a
    /// caller-supplied <c>RedoUpdateModel</c> form/query key — or that it did not reject it at
    /// all (<see cref="None"/>).
    /// <para>
    /// <b>Why this exists.</b> <c>Configs.EnforceRequestBindingScope</c> defaults to
    /// <see langword="true"/> since v10.22.0, and <c>RedoUpdateModel</c> used to log a Warning
    /// for every key <see cref="RequestBindingPolicy.IsPathAllowed(object?, string?, string?)"/>
    /// rejected. But an ordinary LayUI grid paging POST carries several framework transport keys
    /// — <c>_DONOT_USE_CS</c>, <c>_DONOT_USE_VMNAME</c>, <c>__RequestVerificationToken</c>,
    /// <c>page</c>, <c>limit</c>, … — that are never VM properties, so every normal request
    /// logged several Warnings from what is supposed to be a security-relevant logger. That
    /// trains operators to ignore it, burying the rejections that actually matter. This enum
    /// lets the two controller <c>RedoUpdateModel</c> implementations log at <c>Debug</c>
    /// instead of <c>Warning</c> for the one rejection class that is mechanically provable to
    /// never write anything (<see cref="NoWritableTarget"/>), while every other reason — which
    /// could still correspond to a real out-of-scope binding attempt — stays exactly as loud as
    /// before. The SET of rejected keys is unchanged; only how loudly each reason is logged
    /// changed.
    /// </para>
    /// </summary>
    public enum BindingRejectionReason
    {
        /// <summary>
        /// Not rejected. <see cref="RequestBindingPolicy.IsPathAllowed(object?, string?, string?)"/>
        /// would return <see langword="true"/> for this input.
        /// </summary>
        None = 0,

        /// <summary>
        /// The dotted path has more segments than <see cref="RequestBindingPolicy.MaxDepth"/>.
        /// <c>Searcher.SortInfo.Property</c> (3 segments) is the deepest verified-legitimate
        /// payload the framework's own LayUI DataTable front end sends, so anything deeper is
        /// rejected outright regardless of what it would resolve to.
        /// </summary>
        PathTooDeep,

        /// <summary>
        /// The <b>only</b> rejection reason that is mechanically provable to never write
        /// anything — the sole class this repository's <c>RedoUpdateModel</c> is allowed to log
        /// at <c>Debug</c> instead of <c>Warning</c>.
        /// <para>
        /// The final path segment resolves to zero members against <b>every</b> type
        /// <c>PropertyHelper.SetPropertyValue</c>'s own intermediate loop
        /// (<c>PropertyHelper.cs:523-551</c>) could possibly have frozen its traversal type at —
        /// <c>sourceType</c> itself, plus the resolved type of each intermediate hop that fully
        /// clears every guard <see cref="RequestBindingPolicy.Classify(Type, string, string)"/>
        /// applies (unique member, non-static, non-gateway). That loop can <c>break</c> early for
        /// a reason this policy cannot observe from a <see cref="Type"/>/name pair alone — a
        /// resolved intermediate member whose current VALUE is <see langword="null"/> and whose
        /// type has no public parameterless constructor — leaving <c>tempType</c> frozen at
        /// whichever of those candidate types the loop had reached, not necessarily the deepest
        /// one. Wherever it actually froze, that type is already in the candidate set this
        /// classification checked. If the final segment resolves to zero members against every
        /// one of those candidates, then no matter where the real, value-dependent traversal
        /// froze, <c>tempType.GetMember(level.Last())</c> also finds nothing there, and
        /// <c>PropertyHelper.cs:554-557</c> (<c>if (!memberInfos.Any()) { return; }</c>) returns
        /// without writing anything. The rejection provably suppressed nothing.
        /// </para>
        /// <para>
        /// This is exactly why a single unresolvable segment on a single-segment key (e.g. the
        /// LayUI transport keys this classification exists to quiet) always lands here rather
        /// than <see cref="UnresolvedIntermediateSegment"/>: with no intermediate hops, the
        /// candidate set is just <c>{ sourceType }</c>, and it already failed to resolve against
        /// that one type by construction.
        /// </para>
        /// </summary>
        NoWritableTarget,

        /// <summary>
        /// A path segment resolved to zero members, and it was NOT proven safe the way
        /// <see cref="NoWritableTarget"/> is. Covers two shapes, both of which must stay as loud
        /// as every other rejection reason:
        /// <para>
        /// <b>(1) An intermediate segment (not the last one) resolved to zero members.</b> This
        /// must never be treated as a no-op: <c>PropertyHelper.SetPropertyValue</c>'s
        /// intermediate loop does not stop the write when a middle segment fails to resolve — it
        /// leaves the traversal type frozen at whatever it was before that failed hop and falls
        /// through to resolve and WRITE the final segment against that frozen type
        /// (<c>PropertyHelper.cs:553-559</c>). A key like <c>"Missing.StaticSecret"</c> still
        /// writes <c>StaticSecret</c> onto the VM itself even though <c>"Missing"</c> never
        /// resolved to anything — proved empirically by
        /// <c>RequestBindingPolicyTests867.MissingIntermediateSegment_ActuallyWritesFinalSegmentOnVm_WhenPolicyIsIgnored</c>.
        /// </para>
        /// <para>
        /// <b>(2) The final segment resolved to zero members against the deepest traversal type,
        /// but resolves to at least one member against an earlier (shallower) type in the
        /// traversal chain.</b> If the real traversal's value-dependent freeze (see
        /// <see cref="NoWritableTarget"/>'s own doc comment) happened to land on that shallower
        /// type instead of the deepest one, the final segment DOES resolve there and the write
        /// lands. Because this classification cannot know, without executing the real traversal
        /// against a live instance, which candidate type the write would actually freeze at, it
        /// cannot rule out a real write and must not be silenced.
        /// </para>
        /// </summary>
        UnresolvedIntermediateSegment,

        /// <summary>
        /// <c>Type.GetMember(name)</c> resolved more than one member for the segment (the
        /// verified concrete cause is a base-class field hidden by a differently-kinded derived
        /// property of the same name — plain same-kind <c>new</c>-hiding resolves to a single
        /// member and does not trigger this). Rejected outright rather than trusting
        /// <c>members[0]</c> — the member <c>PropertyHelper.SetPropertyValue</c> itself
        /// unconditionally uses — to be the safe candidate.
        /// </summary>
        AmbiguousMember,

        /// <summary>
        /// The resolved member is <c>static</c>. <c>Type.GetMember(name)</c> defaults to
        /// <c>BindingFlags.Public | Instance | Static</c>, so a <c>public static</c> member is
        /// reachable through an ordinary instance path even though it has nothing to do with the
        /// instance being traversed.
        /// </summary>
        StaticMember,

        /// <summary>
        /// The segment resolved to a type that is, or implements/derives from, one of
        /// <see cref="RequestBindingPolicy.BannedGatewayTypes"/> — a gateway into a larger,
        /// shared, or process-wide object graph (the framework context, its configuration, global
        /// framework data, the current login identity, the data context, or per-request
        /// infrastructure services) rather than VM-local/Searcher-local state.
        /// </summary>
        GatewayType,
    }
}
