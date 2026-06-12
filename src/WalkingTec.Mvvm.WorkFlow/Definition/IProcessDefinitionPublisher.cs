#nullable enable
// WF-4: Publish-flow service interface.
// WF-21.1: Additive PublishRawAsync for the designer raw-bytes path.

using System;
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

    // ── WF-21.1: Designer raw-path publish ────────────────────────────────────

    /// <summary>
    /// Publish a workflow graph from raw JSON bytes (the designer raw path).
    ///
    /// <para><strong>Raw-path pipeline (spec §2.2):</strong>
    /// <list type="number">
    ///   <item><see cref="WorkflowGraphSerializer.Canonicalize"/> the raw JSON —
    ///         unknown fields and exact number literals survive.</item>
    ///   <item>Compute SHA-256 <c>ContentHash</c> from the canonical bytes.</item>
    ///   <item>Typed-deserialize the SAME document for validation only
    ///         (<see cref="WorkflowGraphValidator.Validate"/>).  Fail-closed.</item>
    ///   <item>Designer gate: reject <c>schemaVersion != 1</c> → <see cref="PublishOutcome.ValidationFailed"/>.</item>
    ///   <item>Idempotent hash check: same hash as current version → <see cref="PublishOutcome.IdempotentNoOp"/>
    ///         (no CAS check, no conflict, regardless of <paramref name="expectedBaseContentHash"/>).</item>
    ///   <item>CAS check: if <paramref name="expectedBaseContentHash"/> is non-null and does NOT match
    ///         current version's <c>ContentHash</c> → <see cref="PublishOutcome.BaseVersionChanged"/> (HTTP 409).</item>
    ///   <item>INSERT immutable <c>ProcessDefinitionVersion</c> storing the CANONICAL RAW BYTES
    ///         (not the typed re-serialized form), repoint the definition head,
    ///         and delete any draft row for this definition — all in one transaction.</item>
    /// </list>
    /// The shipped typed <see cref="PublishAsync"/> is byte-for-byte untouched.
    /// </para>
    /// </summary>
    /// <param name="definitionCode">The unique business code of the ProcessDefinition.</param>
    /// <param name="rawGraphJson">
    /// The raw JSON graph document as received from the client.
    /// Must be a valid JSON object; malformed JSON → caller should pre-validate
    /// (this method throws <see cref="System.Text.Json.JsonException"/> on parse failure).
    /// </param>
    /// <param name="publishedBy">ITCode of the publishing user.</param>
    /// <param name="expectedBaseContentHash">
    /// The <c>ContentHash</c> of the version the client started editing from
    /// (the "base" for the CAS).
    /// <list type="bullet">
    ///   <item>Null or empty: skip CAS check (first publish or test caller).</item>
    ///   <item>Non-null: must equal <c>head.CurrentVersion.ContentHash</c>; mismatch →
    ///         <see cref="PublishOutcome.BaseVersionChanged"/>.</item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PublishResult"/> describing the outcome.</returns>
    /// <remarks>
    /// <para><strong>Additive compatibility (FIX-B3d):</strong>
    /// This member was added in WF-21.1 (10.11.0). Existing
    /// <see cref="IProcessDefinitionPublisher"/> implementations compiled against 10.10 or
    /// earlier do not implement this method and would fail to load if it were abstract.
    /// The default implementation (DIM) throws <see cref="NotSupportedException"/> to give a
    /// clear error message at call time instead of at load time, preserving binary
    /// compatibility for third-party implementations.</para>
    /// </remarks>
    Task<PublishResult> PublishRawAsync(
        string definitionCode,
        string rawGraphJson,
        string? publishedBy,
        string? expectedBaseContentHash,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"IProcessDefinitionPublisher.PublishRawAsync is not implemented by {GetType().FullName}. " +
            "Update the implementation to support the designer raw-path publish (WF-21.1 / 10.11.0).");
}
