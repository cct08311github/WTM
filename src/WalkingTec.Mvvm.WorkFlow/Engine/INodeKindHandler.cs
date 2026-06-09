#nullable enable
// WF-6: INodeKindHandler dispatcher seam.
//
// Design:
//   • One interface; registry maps NodeKind → INodeKindHandler.
//   • Three methods: OnEnter (mints tasks / CcRecords), CanComplete (decides if the node
//     is done), OnComplete (cleans up / side effects before AdvanceAsync routes onward).
//   • MVP built-ins: Start, End, Cc, Condition (stub → default/first transition).
//   • Approval: stub that returns Blocked (WF-8/9/10 fill it in).
//   • INodeKindDispatcher is the registry; WorkflowEngine resolves it.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Context passed into a <see cref="INodeKindHandler"/> for a single node activation.
/// </summary>
public sealed class NodeHandlerContext
{
    /// <summary>The definition of the node being processed.</summary>
    public required NodeDef NodeDef { get; init; }

    /// <summary>The runtime materialization of this node.</summary>
    public required NodeInstance NodeInstance { get; init; }

    /// <summary>The owning process instance.</summary>
    public required ProcessInstance ProcessInstance { get; init; }

    /// <summary>The deserialized graph for routing lookups.</summary>
    public required WorkflowGraph Graph { get; init; }

    /// <summary>The DbContext for transactional writes (already in a transaction).</summary>
    public required DbContext Db { get; init; }

    /// <summary>Tenant code (propagated from the process instance).</summary>
    public string? TenantCode => ProcessInstance.TenantCode;

    /// <summary>Cancellation token for the current operation.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Strategy for handling one <see cref="NodeKind"/> within the token/marking engine.
///
/// <para>The engine calls <see cref="OnEnterAsync"/> when a token arrives at a node,
/// then checks <see cref="CanCompleteAsync"/> to decide whether to continue routing,
/// then calls <see cref="OnCompleteAsync"/> just before minting the successor token(s).</para>
///
/// <para>Approval nodes are handled by a stub until WF-8/9/10 fill in
/// 串签 / 会签 / 或签 completion policies.</para>
/// </summary>
public interface INodeKindHandler
{
    /// <summary>
    /// Called when the engine mints a token at this node (activation).
    /// Responsible for: creating <see cref="ApprovalTask"/> rows (Approval handler — WF-8/9/10),
    /// writing <see cref="CcRecord"/> rows (Cc handler — WF-13), or any other on-enter side effect.
    /// Must be idempotent with respect to the guarded-CAS discipline.
    /// </summary>
    Task OnEnterAsync(NodeHandlerContext ctx);

    /// <summary>
    /// Returns true when the node's completion condition is satisfied and the engine
    /// may advance to the next node.
    ///
    /// <list type="bullet">
    ///   <item>Start/End/Cc/Condition: always true (pass-through, no human wait).</item>
    ///   <item>Approval: false until the approval-mode policy is satisfied (WF-8/9/10).</item>
    /// </list>
    /// </summary>
    Task<bool> CanCompleteAsync(NodeHandlerContext ctx);

    /// <summary>
    /// Called just before the engine routes onward from a completed node.
    /// Handler may update advisory counts, cancel sibling tasks, etc.
    /// For pass-through nodes (Start/Cc/Condition) this is typically a no-op.
    /// </summary>
    Task OnCompleteAsync(NodeHandlerContext ctx);
}

/// <summary>
/// Registry that maps <see cref="NodeKind"/> to the appropriate <see cref="INodeKindHandler"/>.
/// </summary>
public interface INodeKindDispatcher
{
    /// <summary>
    /// Resolve the handler for the given <paramref name="kind"/>.
    /// Throws <see cref="System.InvalidOperationException"/> for unknown kinds.
    /// </summary>
    INodeKindHandler Resolve(NodeKind kind);
}
