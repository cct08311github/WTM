#nullable enable
// WF-4: Publish-flow service interface.

using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Publishes (or idempotently re-publishes) a workflow graph for a named
/// <see cref="Models.ProcessDefinition"/>.
///
/// <para><strong>Publish flow (spec §4):</strong>
/// <list type="number">
///   <item>Validate the graph structurally (fail-closed; returns <see cref="PublishOutcome.ValidationFailed"/> — NOT an exception).</item>
///   <item>Canonicalize to deterministic JSON (<see cref="WorkflowGraphSerializer"/>).</item>
///   <item>Compute SHA-256 <see cref="Models.ProcessDefinitionVersion.ContentHash"/>.</item>
///   <item>If hash == current version ContentHash → <see cref="PublishOutcome.IdempotentNoOp"/> (no DB write).</item>
///   <item>Else: INSERT new <see cref="Models.ProcessDefinitionVersion"/> with
///       <c>VersionNo = max(existing) + 1</c>; repoint
///       <see cref="Models.ProcessDefinition.CurrentVersionId"/> — all in one transaction.</item>
/// </list>
/// </para>
/// </summary>
public interface IProcessDefinitionPublisher
{
    /// <summary>
    /// Publish <paramref name="graph"/> for the definition identified by
    /// <paramref name="definitionCode"/>.
    /// </summary>
    /// <param name="definitionCode">
    /// The unique business code of the <see cref="Models.ProcessDefinition"/> to publish.
    /// Must exist in the current tenant scope.
    /// </param>
    /// <param name="graph">The workflow graph to publish.</param>
    /// <param name="publishedBy">
    /// ITCode of the user performing the publish (stored on the version row).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="PublishResult"/> describing the outcome.
    /// <see cref="PublishResult.IsSuccess"/> is true for both
    /// <see cref="PublishOutcome.Published"/> and
    /// <see cref="PublishOutcome.IdempotentNoOp"/>.
    /// </returns>
    Task<PublishResult> PublishAsync(
        string definitionCode,
        WorkflowGraph graph,
        string? publishedBy,
        CancellationToken cancellationToken = default);
}
