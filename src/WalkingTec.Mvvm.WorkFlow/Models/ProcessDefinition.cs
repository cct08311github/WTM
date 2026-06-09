#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// Mutable logical head / catalog entry for a workflow definition.
/// Editing repoints <see cref="CurrentVersionId"/> to a new immutable
/// <see cref="ProcessDefinitionVersion"/> — the head is mutable, the version is not.
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/> so that
/// <c>DataContext.OnModelCreating</c> (DataContext.cs:164) auto-applies the
/// <c>IsValid == true</c> (soft-delete) and <c>TenantCode == this.TenantCode</c>
/// (tenant isolation) query filters.  Multi-level inheritance silently skips the filter —
/// derive directly (spec §3 invariant #2).
/// </remarks>
[AuditChanges]
public class ProcessDefinition : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>Unique business key within a tenant (e.g. "PurchaseApproval").</summary>
    [Required]
    [StringLength(100)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional category for grouping (e.g. "Finance", "HR").</summary>
    [StringLength(100)]
    public string? Category { get; set; }

    /// <summary>Whether this definition is available for new instances to start.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// FK to the currently active <see cref="ProcessDefinitionVersion"/>.
    /// Nullable until the first version is published.
    /// </summary>
    public Guid? CurrentVersionId { get; set; }

    /// <summary>Navigation to the currently active version.</summary>
    public ProcessDefinitionVersion? CurrentVersion { get; set; }
}
