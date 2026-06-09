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
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/> so that
/// <c>DataContext.OnModelCreating</c> auto-applies soft-delete + tenant query filters.
/// </remarks>
[AuditChanges]
public class ProcessDefinitionVersion : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

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
