#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// APPEND-ONLY audit log.  One row per engine transition.  Never updated.
///
/// Because engine transitions use <c>ExecuteUpdateAsync</c> they bypass the EF
/// change-tracker and <c>[AuditChanges]</c> does NOT capture them.  Every engine
/// transition writes a <see cref="WorkflowEventLog"/> row inside its transaction —
/// this IS the authoritative audit trail (spec §8.4).
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="BasePoco"/> and <see cref="ITenant"/>.
/// Uses <see cref="BasePoco"/> (audit-only, no soft-delete) because event log rows
/// must never be soft-deleted — they are permanent audit records.
/// </remarks>
public class WorkflowEventLog : BasePoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>FK to the <see cref="ProcessInstance"/> this event belongs to.</summary>
    [Required]
    public Guid InstanceId { get; set; }

    /// <summary>Navigation to the owning instance.</summary>
    public ProcessInstance? Instance { get; set; }

    /// <summary>
    /// Monotonically increasing sequence number per instance (1, 2, 3 …).
    /// Set by the engine; ensures timeline ordering per instance.
    /// </summary>
    public int Seq { get; set; }

    /// <summary>ITCode of the actor who triggered this event.</summary>
    [StringLength(50)]
    public string? ActorITCode { get; set; }

    /// <summary>The action that caused this event.</summary>
    public EventAction Action { get; set; }

    /// <summary>
    /// Key of the node this event relates to
    /// (matches a <c>nodeKey</c> in <c>GraphJson</c>).
    /// </summary>
    [StringLength(100)]
    public string? NodeKey { get; set; }

    /// <summary>Original assignee ITCode when acting on behalf of a delegator.</summary>
    [StringLength(50)]
    public string? OnBehalfOfITCode { get; set; }

    /// <summary>ITCode of the approver who used 加签 to create a runtime task.</summary>
    [StringLength(50)]
    public string? AddedByITCode { get; set; }

    /// <summary>Reason or comment supplied by the actor.</summary>
    public string? Reason { get; set; }

    /// <summary>State before the transition (serialized enum name for human readability).</summary>
    [StringLength(50)]
    public string? BeforeState { get; set; }

    /// <summary>State after the transition (serialized enum name for human readability).</summary>
    [StringLength(50)]
    public string? AfterState { get; set; }

    /// <summary>UTC timestamp when this event occurred.</summary>
    public DateTime OccurredUtc { get; set; }
}
