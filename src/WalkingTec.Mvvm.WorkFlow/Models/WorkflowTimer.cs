#nullable enable
// WF-20: 超时 engine behavior is DEFERRED to Wave 5.
// This entity's schema is present from Sprint 1 so no migration churn lands when Wave 5 ships.
// Engine logic that arms/fires/cancels WorkflowTimer will be implemented in WF-20.
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// Durable 超时 timer.  Schema present from Sprint 1; consumed by the timeout wave (WF-20).
///
/// The <c>WorkflowTimerHostedService</c> (modeled on <c>EtlHostedService</c>) polls
/// <c>Status == Armed</c> timers idempotently using <c>IdempotencyKey</c>.
///
/// <see cref="RowVer"/> is the app-incremented concurrency token used for the
/// timeout-vs-human CAS (spec §7.2, T-CONC-4).
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="BasePoco"/> and <see cref="ITenant"/>.
/// </remarks>
public class WorkflowTimer : BasePoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>
    /// Optional FK to the specific <see cref="ApprovalTask"/> this timer covers.
    /// Null when the timer is node-scoped rather than task-scoped.
    /// </summary>
    public Guid? ApprovalTaskId { get; set; }

    /// <summary>Navigation to the associated task (nullable).</summary>
    public ApprovalTask? ApprovalTask { get; set; }

    /// <summary>FK to the <see cref="NodeInstance"/> this timer is watching.</summary>
    [Required]
    public Guid NodeInstanceId { get; set; }

    /// <summary>Navigation to the associated node instance.</summary>
    public NodeInstance? NodeInstance { get; set; }

    /// <summary>UTC timestamp when this timer should fire.</summary>
    public DateTime FireAtUtc { get; set; }

    /// <summary>Action to execute when this timer fires.</summary>
    public TimerAction Action { get; set; }

    /// <summary>
    /// Unique key used by the poller for idempotent delivery.
    /// Prevents duplicate fires when the poller crashes mid-flight.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Current status of this timer in its lifecycle.</summary>
    public TimerStatus Status { get; set; } = TimerStatus.Armed;

    /// <summary>Number of reminder notifications already sent for this timer.</summary>
    public int RemindCount { get; set; }

    /// <summary>
    /// App-incremented concurrency token.  Not mapped as an EF concurrency token —
    /// managed inside the WHERE clause of <c>ExecuteUpdateAsync</c> for portable
    /// CAS across all 7 DBTypeEnum providers (spec §7.2).
    /// </summary>
    public uint RowVer { get; set; }
}
