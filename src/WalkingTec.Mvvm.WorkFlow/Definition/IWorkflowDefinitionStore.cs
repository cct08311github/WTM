#nullable enable
// WF-21.2: Scoped definition-catalog service for the designer.
// WF-21.3: Draft CRUD (ProcessDefinitionDraft entity) + draft info in GetCurrentGraphAsync envelope.
//
// This interface covers the read, create, and metadata operations surfaced by
// WorkflowDesignerController.  It has NO ProcessDefinitionVersion update/delete
// member — version immutability is a structural guarantee enforced here
// (spec §3.2 / T-DSN-14).
//
// Threading: scoped service (one instance per request).

using System;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Scoped data-access service for the WorkFlow designer catalog operations.
///
/// <para>Covers head creation, metadata updates, paged listing, version history, and
/// graph fetch.  The interface deliberately excludes any version update or delete
/// operation — published versions are immutable once written (spec §3.2, T-DSN-14).</para>
///
/// <para>Tenant isolation is automatic: all implementations apply the DataContext
/// tenant query filter via LINQ queries scoped to the current <c>IDataContext</c>
/// (the same filter applied by <see cref="ProcessDefinitionPublisher"/>).</para>
/// </summary>
public interface IWorkflowDefinitionStore
{
    // ── Head catalog ──────────────────────────────────────────────────────────

    /// <summary>
    /// Return a paged list of definition heads visible to the current tenant.
    ///
    /// <para>Each item includes: Code, Name, Category, IsEnabled, CurrentVersionNo
    /// (0 if never published), and a flag indicating whether an in-progress draft exists
    /// for this definition (always false until WF-21.3 introduces <c>ProcessDefinitionDraft</c>).
    /// </para>
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (capped internally at 200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A paged result containing the matching items and total count.
    /// </returns>
    Task<DefinitionListResult> ListDefinitionsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new <c>ProcessDefinition</c> head (no initial version).
    /// </summary>
    /// <param name="request">Creation parameters (code, name, category).</param>
    /// <param name="tenantCode">Tenant code of the acting user (server-set, never client).</param>
    /// <param name="createdBy">ITCode of the creating user (server-set, never client).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="CreateDefinitionOutcome.Created"/> on success;
    /// <see cref="CreateDefinitionOutcome.DuplicateCode"/> when <paramref name="request.Code"/>
    /// already exists in this tenant.
    /// </returns>
    Task<CreateDefinitionResult> CreateDefinitionAsync(
        CreateDefinitionRequest request,
        string? tenantCode,
        string? createdBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update mutable head metadata: Name, Category, and/or IsEnabled.
    /// </summary>
    /// <param name="code">Definition code (unique within tenant).</param>
    /// <param name="request">Fields to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the update succeeded; <c>false</c> when the definition was not found
    /// (or soft-deleted) in the current tenant scope.
    /// </returns>
    Task<bool> UpdateDefinitionMetadataAsync(
        string code,
        UpdateDefinitionMetadataRequest request,
        CancellationToken cancellationToken = default);

    // ── Version access (read-only) ────────────────────────────────────────────

    /// <summary>
    /// Load the currently published version's graph JSON + metadata for a definition,
    /// plus stub draft info (always null until WF-21.3).
    ///
    /// <para>Returns <c>null</c> when the definition does not exist in the current
    /// tenant scope (404 mapping).</para>
    ///
    /// <para>If the definition exists but has no published version yet,
    /// <see cref="DefinitionGraphEnvelope.GraphJson"/> is <c>null</c>.</para>
    /// </summary>
    /// <param name="code">Definition code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DefinitionGraphEnvelope?> GetCurrentGraphAsync(
        string code,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the complete version history for a definition, newest first.
    /// </summary>
    /// <param name="code">Definition code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Ordered list of version summaries; empty list when the definition has no published
    /// versions.  Returns <c>null</c> when the definition is not found.
    /// </returns>
    Task<VersionHistoryResult?> GetVersionHistoryAsync(
        string code,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the verbatim GraphJson of one immutable version, tenant-scoped.
    ///
    /// <para>Cross-tenant ID access behaves as 404 (tenant filter applied).</para>
    /// </summary>
    /// <param name="versionId">PK of the <c>ProcessDefinitionVersion</c> row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version's graph envelope, or <c>null</c> when not found / cross-tenant.</returns>
    Task<VersionGraphEnvelope?> GetVersionGraphAsync(
        Guid versionId,
        CancellationToken cancellationToken = default);

    // ── WF-21.3: Draft CRUD ───────────────────────────────────────────────────

    /// <summary>
    /// Load the current draft for a definition, or <c>null</c> when none exists.
    ///
    /// <para>Returns <c>null</c> when the definition does not exist or when there is no
    /// draft; the controller maps both to 404.</para>
    /// </summary>
    /// <param name="code">Definition code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DraftInfo?> GetDraftAsync(
        string code,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update the draft for a definition using If-None-Match / If-Match semantics.
    ///
    /// <para>Concurrency model:</para>
    /// <list type="bullet">
    ///   <item><c>create = true</c> (<c>If-None-Match:*</c>): insert a new draft row;
    ///         definition must exist in tenant scope; no draft must already exist —
    ///         existing draft → <see cref="SaveDraftOutcome.Conflict"/>.</item>
    ///   <item><c>create = false</c> (<c>If-Match</c>): update an existing draft;
    ///         row must exist with matching <c>RowVersion</c>; row-gone → <see cref="SaveDraftOutcome.Conflict"/>
    ///         (no create-on-missing — post-publish resurrection guard, spec §3.2).</item>
    /// </list>
    /// </summary>
    /// <param name="code">Definition code.</param>
    /// <param name="graphJson">Raw graph JSON from the request body.</param>
    /// <param name="baseContentHash">
    /// Optional: the ContentHash of the published version the user started editing from.
    /// Recorded for the publish CAS flow. Null for new (never-published) definitions.
    /// </param>
    /// <param name="expectedRowVersion">
    /// Required when <paramref name="create"/> is <c>false</c>: the RowVersion value
    /// the client last received (from the previous GET or PUT response).
    /// Ignored when <paramref name="create"/> is <c>true</c>.
    /// </param>
    /// <param name="create">
    /// <c>true</c> = create semantics (If-None-Match:*);
    /// <c>false</c> = update semantics (If-Match).
    /// </param>
    /// <param name="savedBy">ITCode of the saving user (server-set).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="SaveDraftOutcome.Saved"/> on success (new RowVersion echoed back);
    /// <see cref="SaveDraftOutcome.DefinitionNotFound"/> when the definition is missing;
    /// <see cref="SaveDraftOutcome.Conflict"/> when the concurrency check fails.
    /// </returns>
    Task<SaveDraftResult> SaveDraftAsync(
        string code,
        string graphJson,
        string? baseContentHash,
        uint expectedRowVersion,
        bool create,
        string? savedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly delete the draft for a definition.
    ///
    /// <para>No-op when no draft exists (idempotent). Returns <c>false</c> when the
    /// definition itself is not found (404 mapping).</para>
    /// </summary>
    /// <param name="code">Definition code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the definition was found (draft may or may not have existed);
    /// <c>false</c> when the definition does not exist in the current tenant scope.</returns>
    Task<bool> DeleteDraftAsync(
        string code,
        CancellationToken cancellationToken = default);
}
