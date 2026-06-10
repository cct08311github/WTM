#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// One running approval instance.  FKs the immutable <see cref="ProcessDefinitionVersion"/>
/// so definition changes never affect in-flight approvals.
///
/// <see cref="RowVer"/> is the app-incremented concurrency token used by
/// <c>GuardedTransition</c> for instance-level CAS (e.g. 撤回-vs-final-approve).
/// See spec §7.2 and <c>ApplyWorkFlowModels</c> for per-provider mapping.
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="PersistPoco"/> and <see cref="ITenant"/>.
/// </remarks>
[AuditChanges]
public class ProcessInstance : PersistPoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>FK to the pinned definition version that governs this run.</summary>
    [Required]
    public Guid DefinitionVersionId { get; set; }

    /// <summary>Navigation to the pinned version.</summary>
    public ProcessDefinitionVersion? DefinitionVersion { get; set; }

    /// <summary>Current state of the instance FSM.</summary>
    public InstanceState State { get; set; } = InstanceState.Draft;

    /// <summary>ITCode of the user who submitted this instance.</summary>
    [Required]
    [StringLength(50)]
    public string InitiatorITCode { get; set; } = string.Empty;

    /// <summary>
    /// Discriminator that identifies the business object type
    /// (e.g. "PurchaseOrder", "LeaveRequest").
    /// </summary>
    [StringLength(200)]
    public string? BusinessType { get; set; }

    /// <summary>
    /// Primary key of the associated business object.
    /// Compound with <see cref="BusinessType"/> to locate the originating record.
    /// </summary>
    [StringLength(200)]
    public string? BusinessKey { get; set; }

    /// <summary>
    /// Serialized form data captured at submission time.
    /// Used by the routing evaluator; updated on re-submission after 回退.
    /// </summary>
    public string? FormDataJson { get; set; }

    /// <summary>
    /// App-incremented concurrency token.  Not mapped as an EF concurrency token —
    /// managed inside the WHERE clause of <c>ExecuteUpdateAsync</c> for portable
    /// CAS across all 7 DBTypeEnum providers (spec §7.2).
    /// </summary>
    public uint RowVer { get; set; }

    // ── Wave-3 回退-to-node fields (WF-16) ────────────────────────────────────

    /// <summary>
    /// Epoch counter.  Incremented atomically with <c>ReturnLoops</c> inside the
    /// STEP-1 <c>BeginReturnAsync</c> single-row CAS (the linearization point).
    /// Live tokens carry <c>Generation == instance.Generation</c>; superseded tokens
    /// carry an older generation and are excluded from all live-marking queries.
    /// Default 0 (all pre-Wave-3 instances are generation 0).
    /// </summary>
    public uint Generation { get; set; }

    /// <summary>
    /// Count of 回退 loops completed on this instance.  Incremented in the same
    /// STEP-1 CAS as <c>Generation</c>.  When <c>ReturnLoops &gt;= MaxReturnLoops</c>
    /// the CAS predicate fails and the engine terminates the instance fail-closed (Race D).
    /// </summary>
    public uint ReturnLoops { get; set; }

    /// <summary>
    /// Per-instance monotonic sequence counter for <c>WorkflowEventLog.Seq</c>.
    /// Allocated via a single-row CAS in <c>AllocateSeqAsync</c> — replaces the
    /// old <c>MAX(Seq)+1</c> pattern that collided without SERIALIZABLE isolation (Race B).
    /// Default 1 (first append takes Seq=1; backfill must seed from existing MAX(Seq)+1).
    /// </summary>
    public int NextSeq { get; set; } = 1;

    /// <summary>
    /// UTC lease expiry for the <c>Returning</c> sub-state.  Set in STEP-1 and cleared
    /// in STEP-6.  A Wave-5 reaper reclaims an expired lease via
    /// <c>ReclaimReturningLeaseAsync</c> when the engine crashes mid-return (Race D crash
    /// addendum).  Null when the instance is not in the <c>Returning</c> sub-state.
    /// </summary>
    public DateTime? ReturningLeaseUtc { get; set; }
}
