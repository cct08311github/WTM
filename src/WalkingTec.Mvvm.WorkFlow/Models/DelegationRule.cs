#nullable enable
// WF-19: 委托/代理 engine behavior is DEFERRED to Wave 4.
// This entity's schema is present from Sprint 1 so no migration churn lands when Wave 4 ships.
// Engine logic that consumes DelegationRule will be implemented in WF-19.
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// Standing time-bounded 委托 rule.  Schema present from Sprint 1; consumed by the
/// 委托/代理 engine in Wave 4 (WF-19).
///
/// A rule is active when the current UTC time falls within
/// [<see cref="StartUtc"/>, <see cref="EndUtc"/>] and <see cref="IsValid"/> is true.
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/>.
/// </remarks>
[AuditChanges]
public class DelegationRule : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>ITCode of the user who is delegating their approval authority.</summary>
    [Required]
    [StringLength(50)]
    public string PrincipalITCode { get; set; } = string.Empty;

    /// <summary>ITCode of the user receiving the delegated authority.</summary>
    [Required]
    [StringLength(50)]
    public string DelegateeITCode { get; set; } = string.Empty;

    /// <summary>
    /// If set, delegation is restricted to definitions whose <c>Code</c>
    /// matches this value.  Null means delegation applies to all definitions.
    /// </summary>
    [StringLength(100)]
    public string? ScopeDefinitionCode { get; set; }

    /// <summary>UTC start of the delegation window.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>UTC end of the delegation window.</summary>
    public DateTime EndUtc { get; set; }
}
