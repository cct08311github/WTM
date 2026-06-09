#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// One approver's inbox row for a single node.  This is the CAS target for
/// task-claim transitions (task-level guarded-transition).
///
/// <see cref="RowVer"/> is the app-incremented concurrency token used by
/// <c>GuardedTransition</c>.  See spec §7.2 and <c>ApplyWorkFlowModels</c>
/// for per-provider mapping.
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/>.
/// </remarks>
[AuditChanges]
public class ApprovalTask : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>FK to the owning <see cref="NodeInstance"/>.</summary>
    [Required]
    public Guid NodeInstanceId { get; set; }

    /// <summary>Navigation to the owning node instance.</summary>
    public NodeInstance? NodeInstance { get; set; }

    /// <summary>ITCode of the assigned approver.</summary>
    [Required]
    [StringLength(50)]
    public string AssigneeITCode { get; set; } = string.Empty;

    /// <summary>Current state of this task.</summary>
    public TaskState State { get; set; } = TaskState.NotYetActive;

    /// <summary>
    /// Position in the 串签 sequence (0-based).
    /// Only the task whose <c>SequenceOrder</c> matches
    /// <see cref="NodeInstance.SequencePointer"/> is active at any time.
    /// </summary>
    public int SequenceOrder { get; set; }

    /// <summary>
    /// ITCode of the original principal when this task arose from a delegation.
    /// </summary>
    [StringLength(50)]
    public string? DelegatedFromITCode { get; set; }

    /// <summary>ITCode of the approver who added this task via 加签.</summary>
    [StringLength(50)]
    public string? AddedByITCode { get; set; }

    /// <summary>
    /// True when this task was injected at runtime via 加签.
    /// These tasks are dropped when a 回退 re-entry discards the span.
    /// </summary>
    public bool IsRuntimeInjected { get; set; }

    /// <summary>Approver's comment recorded when acting on this task.</summary>
    public string? Comment { get; set; }

    /// <summary>UTC timestamp when the approver acted.</summary>
    public DateTime? ActedAtUtc { get; set; }

    /// <summary>UTC deadline for this task (arms a <see cref="WorkflowTimer"/> when set).</summary>
    public DateTime? DueUtc { get; set; }

    /// <summary>
    /// App-incremented concurrency token.  Not mapped as an EF concurrency token —
    /// managed inside the WHERE clause of <c>ExecuteUpdateAsync</c> for portable
    /// CAS across all 7 DBTypeEnum providers (spec §7.2).
    /// </summary>
    public uint RowVer { get; set; }
}
