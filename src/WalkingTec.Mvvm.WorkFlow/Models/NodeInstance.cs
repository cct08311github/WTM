#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// Runtime materialization of one graph node within a <see cref="ProcessInstance"/>.
/// This is the CAS target for 或签 / 会签 completion transitions.
///
/// <see cref="RowVer"/> is the app-incremented concurrency token used by
/// <c>GuardedTransition</c> for node-level CAS.  See spec §7.2 and
/// <c>ApplyWorkFlowModels</c> for per-provider mapping.
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="BasePoco"/> and <see cref="ITenant"/>.
/// Uses <see cref="BasePoco"/> (audit-only) rather than <see cref="PersistPoco"/>
/// because node instances are not soft-deleted — they are superseded during 回退
/// re-entry by a controlled discard-and-recount projection (spec §5.7).
/// </remarks>
[AuditChanges]
public class NodeInstance : BasePoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>FK to the owning <see cref="ProcessInstance"/>.</summary>
    [Required]
    public Guid InstanceId { get; set; }

    /// <summary>Navigation to the owning instance.</summary>
    public ProcessInstance? Instance { get; set; }

    /// <summary>
    /// Unique key of this node within the graph definition
    /// (matches a <c>nodeKey</c> in <c>GraphJson</c>).
    /// </summary>
    [Required]
    [StringLength(100)]
    public string NodeKey { get; set; } = string.Empty;

    /// <summary>Kind of this node as declared in the graph definition.</summary>
    public NodeKind NodeKind { get; set; }

    /// <summary>Current FSM state of this node.</summary>
    public NodeState State { get; set; } = NodeState.Pending;

    /// <summary>
    /// Approval mode copied from the graph definition for query speed.
    /// Null for non-Approval node kinds.
    /// </summary>
    public ApproveMode? ApproveMode { get; set; }

    /// <summary>
    /// Minimum approval fraction for 比例会签 (e.g. 0.6 = 60 %).
    /// Null means all approvers must approve (<see cref="Models.ApproveMode.All"/>).
    /// </summary>
    public decimal? ApprovePercent { get; set; }

    /// <summary>When a rejection closes the node in 会签 mode.</summary>
    public RejectGate RejectGate { get; set; } = RejectGate.Immediate;

    /// <summary>What happens to the instance when this node is rejected.</summary>
    public RejectPolicy RejectPolicy { get; set; } = RejectPolicy.ReturnToInitiator;

    /// <summary>Running count of approvals received (advisory; completion decided by CAS).</summary>
    public int ApprovedCount { get; set; }

    /// <summary>Running count of rejections received.</summary>
    public int RejectedCount { get; set; }

    /// <summary>Total number of approvers assigned to this node.</summary>
    public int TotalRequired { get; set; }

    /// <summary>
    /// Current sequence pointer for 串签 (Sequential) mode.
    /// Points to the <c>SequenceOrder</c> of the currently active task.
    /// </summary>
    public int SequencePointer { get; set; }

    /// <summary>ITCode of the approver who cast the deciding vote (or签 winner).</summary>
    [StringLength(50)]
    public string? DecidedBy { get; set; }

    /// <summary>UTC timestamp when this node was activated.</summary>
    public DateTime? ActivatedAt { get; set; }

    /// <summary>
    /// App-incremented concurrency token.  Not mapped as an EF concurrency token —
    /// managed inside the WHERE clause of <c>ExecuteUpdateAsync</c> for portable
    /// CAS across all 7 DBTypeEnum providers (spec §7.2).
    /// </summary>
    public uint RowVer { get; set; }

    // ── Wave-3 回退-to-node fields (WF-16) ────────────────────────────────────

    /// <summary>
    /// Epoch this node was minted in.  Matches <c>ProcessInstance.Generation</c> at
    /// mint time.  Live-marking queries filter <c>Generation == instance.Generation</c>
    /// so stale-epoch tokens are excluded from all post-return processing (Race A / §4.1).
    /// Default 0 (all pre-Wave-3 nodes are generation 0, consistent with instance default).
    /// </summary>
    public uint Generation { get; set; }

    /// <summary>
    /// The generation at which this node was superseded by a 回退-to-node operation.
    /// Null for nodes that have not been superseded.
    /// Set atomically by <c>SupersedeNodeAsync</c> in the same CAS that flips
    /// <c>State</c> to <c>NodeState.Superseded</c> (Race A guard).
    /// </summary>
    public uint? SupersededAtGen { get; set; }

    // ── Wave-3 WF-17: Parallel/Inclusive gateway + Join fields ───────────────

    /// <summary>
    /// Identifies the fork group this token belongs to.
    /// Set on ALL branch tokens minted from a <see cref="NodeKind.ParallelGateway"/> or
    /// <see cref="NodeKind.InclusiveGateway"/> fork; null for tokens not in a fork group.
    /// All tokens with the same <c>ForkGroupId</c> share the same paired Join.
    /// </summary>
    public Guid? ForkGroupId { get; set; }

    /// <summary>
    /// The <see cref="NodeKey"/> of the Join node that this token is expected to converge into.
    /// Set on branch tokens at fork time; null for tokens not inside a fork/Join region.
    /// </summary>
    [StringLength(100)]
    public string? JoinNodeKey { get; set; }

    /// <summary>
    /// For <see cref="NodeKind.Join"/> nodes: the number of branch arrivals expected before
    /// the Join can fire.  Pinned at fork time to the number of branch tokens actually activated
    /// (AND-fork: all N; OR-fork: N matching branches, 1 ≤ N ≤ total).
    /// Default 0 for non-Join nodes.
    /// </summary>
    public int JoinExpectedArrivals { get; set; }

    /// <summary>
    /// For <see cref="NodeKind.Join"/> nodes: the number of branch arrivals received so far.
    /// Incremented atomically by <c>GuardedTransition.IncrementJoinArrivedAsync</c>.
    /// Default 0 for non-Join nodes.
    /// </summary>
    public int JoinArrivedCount { get; set; }

    /// <summary>
    /// For <see cref="NodeKind.Ack"/> nodes: the completion mode for the blocking-acknowledge
    /// barrier.  Mirrors <see cref="ApproveMode"/> semantics but for acknowledge actions.
    /// Null for non-Ack node kinds.
    /// </summary>
    public Models.AckMode? AckMode { get; set; }
}
