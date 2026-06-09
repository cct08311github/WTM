#nullable enable
// WF-6: IWorkflowEngine public contract.
//
// All methods operate inside a single DbContext transaction per call (spec §7.3).
// Callers (WorkflowActionVM) interact only through this interface — never touching
// DataContext directly (WTM red line: no controller-level DC access).

using System;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// The workflow engine public contract.
/// Registered as scoped by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
///
/// <para><strong>Transaction contract:</strong> every method wraps all DB writes in a
/// single transaction.  On <c>DbUpdateConcurrencyException</c> the engine retries up to
/// 3 times with a fresh read (spec §7.3).</para>
///
/// <para><strong>Audit:</strong> every method appends at least one
/// <see cref="WorkflowEventLog"/> row inside its transaction (spec §8.4).</para>
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// Start a new process instance for the published definition identified by
    /// <paramref name="definitionVersionId"/>.
    ///
    /// <list type="number">
    ///   <item>Creates a <see cref="ProcessInstance"/> in <see cref="InstanceState.Running"/>.</item>
    ///   <item>Deserializes the pinned <see cref="ProcessDefinitionVersion.GraphJson"/>.</item>
    ///   <item>Mints the initial token at the Start node and calls <c>AdvanceAsync</c>
    ///         to drive through all pass-through nodes until the process either completes
    ///         or reaches an Approval node that blocks.</item>
    /// </list>
    /// </summary>
    /// <param name="definitionVersionId">FK to the immutable <see cref="ProcessDefinitionVersion"/>.</param>
    /// <param name="formDataJson">JSON-serialized form data captured at submission time.
    /// Used by the routing evaluator (WF-11).</param>
    /// <param name="initiatorITCode">ITCode of the submitter.</param>
    /// <param name="tenantCode">Tenant isolation code.</param>
    /// <param name="businessType">Optional discriminator for the business object type.</param>
    /// <param name="businessKey">Optional PK of the associated business object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created <see cref="ProcessInstance"/> (ID + State after driving).</returns>
    Task<ProcessInstance> StartAsync(
        Guid definitionVersionId,
        string? formDataJson,
        string initiatorITCode,
        string? tenantCode,
        string? businessType = null,
        string? businessKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Advance the process instance identified by <paramref name="instanceId"/>.
    ///
    /// <para>This is the single internal advancement entry:
    /// claim current active node → run handler → on completion route via outgoing
    /// transitions → mint next token(s) → write event log — all in one transaction.</para>
    ///
    /// <para>Called automatically by <see cref="StartAsync"/> and after human actions
    /// (Approve/Reject/etc.) once the node's completion condition is satisfied.</para>
    /// </summary>
    Task<WorkflowActionResult> AdvanceAsync(
        Guid instanceId,
        CancellationToken ct = default);
}
