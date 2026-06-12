#nullable enable
// WF-21.3: Draft store for the workflow designer.
//
// One row per (TenantCode, DefinitionId) — enforced by a unique index.
// The engine NEVER reads drafts. No designer code path can UPDATE or DELETE
// a ProcessDefinitionVersion row — IWorkflowDefinitionStore has no such member.
//
// Concurrency model: optimistic, via RowVersion (plain uint, app-incremented CAS,
// same pattern as WF-2/WF-3 NodeInstance/ApprovalTask). Transported in the HTTP
// If-Match / ETag headers so the raw body stays a pure graph document.
//
// Consumer migration note (same discipline as Etl #236 / Wave-1):
//   WalkingTec.Mvvm.WorkFlow ships ZERO migrations. Generate migrations in your
//   consumer application after calling ApplyWorkFlowModels() in OnModelCreating:
//
//     dotnet ef migrations add WorkFlow_AddDraftStore \
//       --context DataContext \
//       --project <YourApp>/<YourApp>.csproj \
//       --startup-project <YourApp>/<YourApp>.csproj
//
//   This adds one new table: Wf_ProcessDefinitionDraft with columns:
//     Id, TenantCode, DefinitionId, GraphJson, BaseContentHash,
//     RowVersion, LastSavedBy, LastSavedAt, IsValid, CreateTime, UpdateTime.
//
// HasQueryFilter invariant: DIRECT `: PersistPoco, ITenant` descendant so that
// DataContext.OnModelCreating (DataContext.cs:164) auto-applies the soft-delete
// (IsValid == true) and tenant-isolation (TenantCode == this.TenantCode) query
// filters. Multi-level inheritance silently skips the filter — derive directly.

using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// In-progress (draft) edit of a workflow definition.
/// One draft per <c>(TenantCode, DefinitionId)</c> pair — enforced by a unique index.
///
/// <para>Lifecycle: created or updated via <c>PUT definitions/{code}/draft</c>;
/// deleted atomically in the same transaction as <c>POST definitions/{code}/publish</c>.
/// Explicit discard via <c>DELETE definitions/{code}/draft</c>.</para>
///
/// <para><strong>Engine isolation:</strong>
/// The engine never reads or writes this table.
/// No <c>IWorkflowDefinitionStore</c> or <c>IProcessDefinitionPublisher</c> method
/// updates or deletes a <c>ProcessDefinitionVersion</c> row.</para>
///
/// <para><strong>RowVersion concurrency (WF-2/3 pattern):</strong>
/// <see cref="RowVersion"/> is a plain <c>uint</c> column — NOT an EF concurrency token.
/// It is managed entirely in the application via app-incremented CAS (same pattern as
/// <c>NodeInstance.RowVer</c> and <c>ApprovalTask.RowVer</c>).
/// Transported in the <c>If-Match</c> / <c>ETag</c> HTTP headers — never in the raw
/// JSON body — so the body stays a pure graph document (spec §3.2 / R2 verdict).</para>
///
/// <para><strong>HasQueryFilter invariant:</strong>
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/> so that
/// <c>DataContext.OnModelCreating</c> auto-applies soft-delete + tenant query filters.
/// Multi-level inheritance silently skips the filter; derive directly (spec §3.2 / T-DSN-6).
/// </para>
///
/// <para><strong>Post-publish resurrection guard:</strong>
/// After publish deletes this row (in-txn), a stale editor's subsequent
/// <c>PUT</c> carrying the old <c>If-Match</c> tag finds the row gone and gets a 409,
/// prompting it to reload the new published version. The guard is enforced by the
/// store: <c>PUT</c> with <c>If-Match</c> does NOT create-on-missing (only
/// <c>If-None-Match:*</c> creates a new draft). See <c>IWorkflowDefinitionStore.SaveDraftAsync</c>.
/// </para>
/// </summary>
[AuditChanges]
public class ProcessDefinitionDraft : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>FK to the owning <see cref="ProcessDefinition"/> head.</summary>
    [Required]
    public Guid DefinitionId { get; set; }

    /// <summary>Navigation to the owning definition head (load with care — large GraphJson).</summary>
    public ProcessDefinition? Definition { get; set; }

    /// <summary>
    /// Raw JSON graph document being edited.
    /// Stored as the client supplied it — never re-canonicalized until publish time.
    /// May differ from any published version's <c>GraphJson</c>.
    /// </summary>
    [Required]
    public string GraphJson { get; set; } = string.Empty;

    /// <summary>
    /// <c>ContentHash</c> of the published version the user started editing from.
    /// Used for the publish CAS check when the user publishes from the draft buffer.
    /// Null when the draft was started from a new (never-published) definition.
    /// </summary>
    [StringLength(64)]
    public string? BaseContentHash { get; set; }

    /// <summary>
    /// Optimistic concurrency token.
    /// Plain <c>uint</c> column — NOT an EF concurrency token.
    /// Incremented by the application (same pattern as <c>NodeInstance.RowVer</c>).
    /// Transported in <c>If-Match</c> / <c>ETag</c> HTTP headers.
    /// Stale-token PUT → 409; designer prompts to reload.
    /// </summary>
    public uint RowVersion { get; set; }

    /// <summary>ITCode of the user who last saved this draft (dual-audit: may differ from publisher).</summary>
    [StringLength(50)]
    public string? LastSavedBy { get; set; }

    /// <summary>UTC timestamp of the last save (dual-audit companion to <c>LastSavedBy</c>).</summary>
    public DateTime? LastSavedAt { get; set; }
}
