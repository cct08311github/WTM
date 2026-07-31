#nullable enable
// #666: IWorkflowGraphProvider — process-wide cache for deserialized WorkflowGraph documents.
//
// Problem: ProcessDefinitionVersion is immutable once published (spec invariant), yet every
// engine/timer call site re-deserializes the ENTIRE graph via WorkflowGraphSerializer.Deserialize
// on every operation (StartAsync, AdvanceAsync, ReturnToPrevAsync, ReturnToNodeAsync,
// LoadNodeDefAsync, LoadTimerNodeDefAsync, ...). For hot instances this repeats a non-trivial
// JSON parse + object-graph build on every single approve/reject/advance call.
//
// Fix: cache the DESERIALIZED WorkflowGraph object, keyed by DefinitionVersionId, in a
// process-wide singleton. The graph content is immutable per version, so the same
// DefinitionVersionId always maps to the same graph — safe to share across all callers
// and across tenants (the tenant/IsValid authorization check happens at the ProcessDefinitionVersion
// ROW QUERY, which callers keep doing unchanged; this provider only removes the repeat
// deserialize cost AFTER that row has already been fetched and validated by the caller).
//
// Security note: this provider intentionally does NOT touch the database and does NOT decide
// whether a caller may see a given version — it is a pure compute cache over data the caller
// already legitimately possesses (a fetched ProcessDefinitionVersion row). Do not repurpose it
// to skip the row query at any call site (see WorkflowEngine.StartAsync — DefinitionVersionId
// there is attacker-controlled request input; the tenant-scoped query filter is the actual
// authorization boundary and must run on every call).
//
// #899 provenance note: between #666 (when this comment was written) and #899 (2026-07), "the
// tenant-scoped query filter is the actual authorization boundary" was NOT actually true for any
// WorkFlow entity, including ProcessDefinitionVersion -- WorkFlow's ApplyWorkFlowModels()
// zero-arg overload registered entity types too late for FrameworkContext.OnModelCreating's
// Pass 2 to see them, so no HasQueryFilter was ever applied and the ROW QUERY above was
// completely unfiltered. #899 fixed the wiring (ApplyWorkFlowModels(this ModelBuilder,
// EmptyContext), ServiceCollectionExtensions.cs) so this comment's claim is now actually true.
// Cross-vendor architecture audit (2026-07-30) flagged this specific comment as the most
// severe instance of the false-auto-wiring assumption in this module, precisely because an
// engineer trusting it would have concluded cross-tenant access was already blocked here.

using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Process-wide cache of deserialized <see cref="WorkflowGraph"/> documents, keyed by
/// <see cref="ProcessDefinitionVersion.ID"/>. Registered as a singleton (mirrors
/// <c>IRoutingEvaluator</c>'s compiled-predicate cache — see
/// <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>).
///
/// <para><strong>Immutability contract:</strong> the returned <see cref="WorkflowGraph"/> is a
/// SHARED cached instance. Callers MUST treat it as read-only — never mutate
/// <c>graph.Nodes</c>, <c>graph.Transitions</c>, <c>graph.FieldWhitelist</c>, or any nested
/// <see cref="NodeDef"/>/<see cref="TransitionDef"/>. All current engine/executor call sites
/// only read from the graph (verified #666); a future call site that needs to mutate a graph
/// must clone it first (e.g. re-deserialize a fresh copy) rather than mutate the cached
/// instance in place.</para>
///
/// <para>Callers are still responsible for fetching the <see cref="ProcessDefinitionVersion"/>
/// row themselves (tenant-scoped query filter + <c>IsValid</c> check) — this provider only
/// caches the deserialization step for a row the caller has already legitimately fetched.</para>
/// </summary>
internal interface IWorkflowGraphProvider
{
    /// <summary>
    /// Returns the deserialized <see cref="WorkflowGraph"/> for <paramref name="version"/>,
    /// using the cached instance when this <see cref="ProcessDefinitionVersion.ID"/> has been
    /// seen before. Deserializes (and caches) on first use.
    /// </summary>
    /// <param name="version">An already-fetched, already-authorized version row. Both
    /// <see cref="ProcessDefinitionVersion.ID"/> and <see cref="ProcessDefinitionVersion.GraphJson"/>
    /// are read; <c>GraphJson</c> is only used on a cache miss.</param>
    WorkflowGraph GetGraph(ProcessDefinitionVersion version);
}
