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
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/> so that the
/// <c>IsValid == true</c> (soft-delete) and <c>TenantCode == this.TenantCode</c> (tenant
/// isolation) query filters apply. NOT via <c>DataContext.OnModelCreating</c>'s own Pass 2
/// (DataContext.cs:164) -- WorkFlow's entity types are registered via
/// <c>ApplyWorkFlowModels()</c> from the consumer's own <c>DataContext.OnModelCreating</c>,
/// called AFTER <c>base.OnModelCreating()</c> returns, by which point that pass has already
/// finished and cannot see them -- the same wiring-order bug #862 fixed for the ETL module.
/// Fixed (#899) by <c>ApplyWorkFlowModels(this ModelBuilder, EmptyContext)</c> re-applying the
/// same filter shape immediately after registering each entity -- see
/// <c>WorkFlowDbContextExtensions</c>'s remarks in <c>ServiceCollectionExtensions.cs</c>.
/// Multi-level inheritance would silently skip the filter -- derive directly (spec §3
/// invariant #2).
/// </remarks>
[AuditChanges]
public class ProcessDefinition : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>
    /// Business key. The schema's unique index is <c>(TenantCode, Code)</c> (composite,
    /// so two tenants can share a Code by design). Before #899,
    /// <see cref="WalkingTec.Mvvm.WorkFlow.Definition.IWorkflowDefinitionStore.CreateDefinitionAsync"/>'s
    /// duplicate check had no <c>TenantCode</c> predicate of its own and relied on the (then
    /// nonexistent) global tenant filter, so in practice a Code already used by ANY tenant was
    /// rejected -- fixed by #899's <c>ApplyWorkFlowModels(this)</c> wiring, which scopes that
    /// query's own <c>AnyAsync(d =&gt; d.Code == request.Code)</c> to the calling context's
    /// TenantCode automatically. Example: "PurchaseApproval".
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
