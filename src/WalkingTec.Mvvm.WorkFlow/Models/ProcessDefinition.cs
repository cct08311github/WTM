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
/// <c>DataContext.OnModelCreating</c> (DataContext.cs:164) is INTENDED to auto-apply the
/// <c>IsValid == true</c> (soft-delete) and <c>TenantCode == this.TenantCode</c>
/// (tenant isolation) query filters. <strong>It currently does not (#899)</strong>: WorkFlow's
/// entity types are registered via <c>ApplyWorkFlowModels()</c> from the consumer's own
/// <c>DataContext.OnModelCreating</c>, called AFTER <c>base.OnModelCreating()</c> returns, by
/// which point the filter-applying pass has already finished and cannot see them -- the same
/// wiring-order bug #862 fixed for the ETL module. Do not rely on tenant isolation for this
/// entity until #899 lands. Multi-level inheritance would ALSO silently skip the filter once
/// #899 is fixed -- derive directly (spec §3 invariant #2).
/// </remarks>
[AuditChanges]
public class ProcessDefinition : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>
    /// Business key. The schema's unique index is <c>(TenantCode, Code)</c> (composite,
    /// so two tenants can share a Code by design), but
    /// <see cref="WalkingTec.Mvvm.WorkFlow.Definition.IWorkflowDefinitionStore.CreateDefinitionAsync"/>'s
    /// duplicate check currently has no <c>TenantCode</c> predicate, so in practice a Code
    /// already used by ANY tenant is rejected (#899). Example: "PurchaseApproval".
    /// </summary>
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
