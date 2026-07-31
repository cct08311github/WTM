#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// IMMUTABLE, content-hashed snapshot of a workflow graph.
/// Running instances FK this version — never the mutable head — so definition
/// changes never retroactively alter in-flight approvals.
///
/// Publish flow: validate → canonicalize JSON → SHA-256 ContentHash →
///   if hash == CurrentVersion.ContentHash → no-op (idempotent republish)
///   else INSERT new version with VersionNo = max+1
///        UPDATE ProcessDefinition.CurrentVersionId.
///
/// No <c>DoEdit</c> path exists for this entity.
/// <see cref="GraphJson"/>, <see cref="ContentHash"/>, and <see cref="VersionNo"/>
/// carry <c>[BindNever]</c> so they cannot be overwritten by a model-binding update
/// (mirrors the CodeGen write-root guard added in PR #123).
///
/// <para><strong>Delete protection (PR #240):</strong>
/// <c>IsValid</c> is shadowed with <c>[BindNever]</c> to block model-binding from
/// flipping it (low-risk but now guarded).  Because <c>ProcessDefinitionVersion</c>
/// is a <c>PersistPoco</c>, <c>BaseCRUDVM.DoDelete</c> would soft-delete it by
/// casting to <c>IPersistPoco</c> and setting <c>IsValid = false</c>, which (since #899) DOES
/// hide the version from the <c>IsValid == true</c> query filter, and ALSO orphans any
/// in-flight <c>ProcessInstance</c> pinned via <c>DefinitionVersionId</c> (data loss --
/// this part is unaffected by #899, since it's an FK integrity problem, not a filter one).
/// DO NOT scaffold a delete-capable CRUD VM for this entity (read + publish only).
/// If a delete VM is ever added, override <c>DoDelete</c>/<c>DoDeleteAsync</c>
/// to throw <see cref="NotSupportedException"/> before that path goes live.</para>
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/> so that
/// soft-delete + tenant query filters apply via
/// <c>WorkFlowDbContextExtensions.ApplyWorkFlowModels(ModelBuilder, EmptyContext)</c> (#899) --
/// NOT via <c>DataContext.OnModelCreating</c>'s own pass, which never sees WorkFlow's entity
/// types (same registration-order gap #862 fixed for ETL) -- see <see cref="ProcessDefinition"/>.
/// </remarks>
[AuditChanges]
public class ProcessDefinitionVersion : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>
    /// Shadows <c>PersistPoco.IsValid</c> to block model-binding from flipping it.
    /// <c>ProcessDefinitionVersion</c> must never be soft-deleted — the published graph
    /// snapshot is immutable once written, and in-flight instances depend on it via FK.
    /// The default <c>true</c> is intended to preserve the standard <c>IsValid == true</c>
    /// query filter, but that filter is not currently wired up for this entity type (#899).
    /// DO NOT scaffold a delete-capable VM for this entity; if one is ever added, override
    /// <c>DoDelete</c>/<c>DoDeleteAsync</c> to throw <see cref="NotSupportedException"/>.
    /// </summary>
    [BindNever]
    public new bool IsValid { get; set; } = true;

    /// <summary>FK to the owning <see cref="ProcessDefinition"/> head.</summary>
    [Required]
    public Guid DefinitionId { get; set; }

    /// <summary>Navigation to the owning definition head.</summary>
    public ProcessDefinition? Definition { get; set; }

    /// <summary>
    /// Monotonically increasing version number per definition (1, 2, 3 …).
    /// Set by the publish pipeline; must not be supplied by client code.
    /// </summary>
    [BindNever]
    public int VersionNo { get; set; }

    /// <summary>
    /// Published contract version for the GraphJson schema.
    /// Sprint-1 publishes schemaVersion = 1.  Evolution is additive-only.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Canonical JSON representation of the workflow graph
    /// (deterministically serialized, key-sorted, per the repo prompt-cache discipline).
    /// Immutable once written. Must not be supplied by client code.
    /// </summary>
    [Required]
    [BindNever]
    public string GraphJson { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hex digest of <see cref="GraphJson"/>.
    /// Used for idempotent republish and as the cache key for compiled routing predicates.
    /// Must not be supplied by client code.
    /// </summary>
    [Required]
    [StringLength(64)]
    [BindNever]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this version was published.</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>ITCode of the user who published this version.</summary>
    [StringLength(50)]
    public string? PublishedBy { get; set; }
}
