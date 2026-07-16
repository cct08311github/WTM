#nullable enable
// WorkflowEngine — Timers region (ArmNodeTimerIfConfiguredAsync / ArmTaskTimerIfConfiguredAsync).
//
// #668: partial-class split of WorkflowEngine.cs — pure code motion (see WorkflowEngine.cs
// for the shared design notes, invariants, and race-condition catalogue). Members below were
// cut verbatim (including their original doc comments) from WorkflowEngine.cs; no signature,
// accessibility, or logic changes were made during the move.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowEngine
{
    // ── WF-20.2: Timer arm helpers ─────────────────────────────────────────────

    /// <summary>
    /// Arm a node-scoped timer for an All/Any Approval node immediately after the
    /// <c>ActivateNodeInstanceAsync</c> winner site.
    ///
    /// <para>Skips silently when:
    /// <list type="bullet">
    ///   <item><see cref="_businessCalendar"/> is null (AddWtmWorkFlowTimers not called).</item>
    ///   <item><see cref="Definition.TimeoutDef"/> is absent on the node.</item>
    ///   <item>BusinessCalendar:true + pass-through + dangerous auto-action (§0 S2 fail-closed).</item>
    /// </list>
    /// </para>
    ///
    /// <para>IdempotencyKey: <c>tmo:n:{NodeInstanceId:N}:{Generation}:0</c>.
    /// Unique-key violations are caught and swallowed (idempotent — concurrent double-activation loses gracefully).</para>
    /// </summary>
    private async Task ArmNodeTimerIfConfiguredAsync(
        NodeInstance nodeInst,
        Definition.NodeDef nodeDef,
        DateTime now,
        CancellationToken ct)
    {
        if (_businessCalendar is null) return;
        var td = nodeDef.Timeout;
        if (td is null) return;

        bool isAutoAction = td.Action is TimerAction.AutoApprove or TimerAction.AutoReject or TimerAction.Escalate;

        // §0 S2 arm-time severity: pass-through calendar + auto-action → skip + warn.
        if (td.BusinessCalendar && _businessCalendar.IsPassThrough && isAutoAction)
        {
            _logger.LogWarning(
                "ArmNodeTimer: node '{NodeKey}' timeout action={Action} + businessCalendar:true " +
                "but only PassThroughBusinessCalendar is registered. Skipping arm (fail-closed on weekends). " +
                "Register a real IBusinessCalendar to enable this action.",
                nodeDef.NodeKey, td.Action);
            return;
        }

        var fireAt = _businessCalendar.AddBusinessTime(now, td.Duration, _options.BusinessCalendarId);
        // Deterministic idempotency key: tmo:n:{nodeId:N}:{generation}:0
        var key = $"tmo:n:{nodeInst.ID:N}:{nodeInst.Generation}:0";

        // FIX-B5d: stamp DueUtc on all Pending tasks covered by this node-scoped timer.
        // Design §0 table: "set ApprovalTask.DueUtc = activation + duration at the SAME site
        // that arms the covering timer" (task-scoped ArmTaskTimerIfConfiguredAsync already does
        // this; node-scoped was missing the stamp until this fix).
        await Db.Set<ApprovalTask>()
            .Where(t => t.NodeInstanceId == nodeInst.ID
                         && t.State == TaskState.Pending
                         && t.Generation == nodeInst.Generation)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DueUtc, fireAt), ct);

        var timer = new WorkflowTimer
        {
            ID              = Guid.NewGuid(),
            TenantCode      = nodeInst.TenantCode,
            NodeInstanceId  = nodeInst.ID,
            ApprovalTaskId  = null, // node-scoped: no task FK
            FireAtUtc       = fireAt,
            Action          = td.Action,
            IdempotencyKey  = key,
            Status          = TimerStatus.Armed,
            RemindCount     = 0,
            Generation      = nodeInst.Generation,
            RowVer          = 0,
        };

        try
        {
            Db.Set<WorkflowTimer>().Add(timer);
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dbEx) when (GuardedTransition.IsUniqueConstraintViolation(dbEx))
        {
            // Idempotent: concurrent activation already inserted this key → detach and continue.
            Db.Entry(timer).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            _logger.LogDebug(
                "ArmNodeTimer: unique-key collision for key '{Key}' (concurrent activation) — no-op.",
                key);
        }
    }

    /// <summary>
    /// Arm a task-scoped timer for a Sequential step that just became Pending.
    ///
    /// <para>Stamps <see cref="ApprovalTask.DueUtc"/> on the task at the same site (derived output).</para>
    ///
    /// <para>IdempotencyKey: <c>tmo:t:{TaskId:N}:0</c>.
    /// Unique-key violations are caught and swallowed (idempotent).</para>
    /// </summary>
    private async Task ArmTaskTimerIfConfiguredAsync(
        ApprovalTask task,
        NodeInstance nodeInst,
        Definition.NodeDef nodeDef,
        DateTime now,
        CancellationToken ct)
    {
        if (_businessCalendar is null) return;
        var td = nodeDef.Timeout;
        if (td is null) return;

        bool isAutoAction = td.Action is TimerAction.AutoApprove or TimerAction.AutoReject or TimerAction.Escalate;

        // §0 S2 arm-time severity: pass-through calendar + auto-action → skip + warn.
        if (td.BusinessCalendar && _businessCalendar.IsPassThrough && isAutoAction)
        {
            _logger.LogWarning(
                "ArmTaskTimer: node '{NodeKey}' task {TaskId} timeout action={Action} + businessCalendar:true " +
                "but only PassThroughBusinessCalendar is registered. Skipping arm (fail-closed on weekends).",
                nodeDef.NodeKey, task.ID, td.Action);
            return;
        }

        var fireAt = _businessCalendar.AddBusinessTime(now, td.Duration, _options.BusinessCalendarId);
        // Deterministic idempotency key: tmo:t:{taskId:N}:0
        var key = $"tmo:t:{task.ID:N}:0";

        // Stamp DueUtc on the task (derived output, not a timer predicate).
        await Db.Set<ApprovalTask>()
            .Where(t => t.ID == task.ID)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DueUtc, fireAt), ct);

        var timer = new WorkflowTimer
        {
            ID              = Guid.NewGuid(),
            TenantCode      = nodeInst.TenantCode,
            NodeInstanceId  = nodeInst.ID,
            ApprovalTaskId  = task.ID,
            FireAtUtc       = fireAt,
            Action          = td.Action,
            IdempotencyKey  = key,
            Status          = TimerStatus.Armed,
            RemindCount     = 0,
            Generation      = nodeInst.Generation,
            RowVer          = 0,
        };

        try
        {
            Db.Set<WorkflowTimer>().Add(timer);
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dbEx) when (GuardedTransition.IsUniqueConstraintViolation(dbEx))
        {
            Db.Entry(timer).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            _logger.LogDebug(
                "ArmTaskTimer: unique-key collision for key '{Key}' (concurrent step activation) — no-op.",
                key);
        }
    }
}
