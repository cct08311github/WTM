#nullable enable
// WF-4: Closed result type for the publish flow.
//
// Uses a Result<T, E> / closed-union outcome (repo convention) so callers
// can branch on the specific outcome code without catching exceptions.

using System;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Closed outcome codes for the publish flow.
/// </summary>
public enum PublishOutcome
{
    /// <summary>A new version was inserted and the definition head was repointed.</summary>
    Published,

    /// <summary>
    /// The supplied graph is identical to the current version (same ContentHash).
    /// No new version was created.  The existing version is returned.
    /// </summary>
    IdempotentNoOp,

    /// <summary>The graph failed structural validation.  No changes were persisted.</summary>
    ValidationFailed,

    /// <summary>The ProcessDefinition with the supplied key was not found.</summary>
    DefinitionNotFound,
}

/// <summary>
/// Result returned by <see cref="IProcessDefinitionPublisher.PublishAsync"/>.
/// </summary>
/// <param name="Outcome">The specific publish outcome.</param>
/// <param name="VersionId">
/// The ID of the relevant <c>ProcessDefinitionVersion</c>:
///   <list type="bullet">
///     <item><see cref="PublishOutcome.Published"/> — the newly inserted version.</item>
///     <item><see cref="PublishOutcome.IdempotentNoOp"/> — the existing (unchanged) version.</item>
///     <item>Other outcomes — null.</item>
///   </list>
/// </param>
/// <param name="VersionNo">The version number of <see cref="VersionId"/>; 0 on non-success.</param>
/// <param name="ContentHash">
/// SHA-256 content hash of the canonical GraphJson; null on non-success outcomes.
/// </param>
/// <param name="ValidationError">
/// Validation error code when <see cref="Outcome"/> is
/// <see cref="PublishOutcome.ValidationFailed"/>; otherwise <see cref="GraphValidationError.None"/>.
/// </param>
/// <param name="ErrorMessage">
/// Human-readable error description; null on success outcomes.
/// </param>
public sealed record PublishResult(
    PublishOutcome Outcome,
    Guid? VersionId,
    int VersionNo,
    string? ContentHash,
    GraphValidationError ValidationError,
    string? ErrorMessage)
{
    /// <summary>True when the outcome is a success (published or idempotent).</summary>
    public bool IsSuccess =>
        Outcome == PublishOutcome.Published || Outcome == PublishOutcome.IdempotentNoOp;

    /// <summary>Create a Published success result.</summary>
    public static PublishResult NewVersion(Guid versionId, int versionNo, string contentHash) =>
        new(PublishOutcome.Published, versionId, versionNo, contentHash,
            GraphValidationError.None, null);

    /// <summary>Create an IdempotentNoOp result (same graph, no new version).</summary>
    public static PublishResult NoOp(Guid existingVersionId, int existingVersionNo, string contentHash) =>
        new(PublishOutcome.IdempotentNoOp, existingVersionId, existingVersionNo, contentHash,
            GraphValidationError.None, null);

    /// <summary>Create a ValidationFailed result.</summary>
    public static PublishResult Invalid(GraphValidationError error, string message) =>
        new(PublishOutcome.ValidationFailed, null, 0, null, error, message);

    /// <summary>Create a DefinitionNotFound result.</summary>
    public static PublishResult NotFound(string definitionCode) =>
        new(PublishOutcome.DefinitionNotFound, null, 0, null,
            GraphValidationError.None,
            $"ProcessDefinition with code '{definitionCode}' was not found (or is soft-deleted).");
}
