#nullable enable
// WF-14: Controller unit tests.
//
// Tests covered:
//
//   Definition controller:
//     1.  Publish_Published_Returns200
//     2.  Publish_IdempotentNoOp_Returns200
//     3.  Publish_ValidationFailed_Returns400
//     4.  Publish_DefinitionNotFound_Returns404
//     5.  Publish_Uses_ServerSide_Actor_Not_RequestBody
//     6.  Validate_Valid_Returns200
//     7.  Validate_Invalid_Returns400
//
//   Instance controller:
//     8.  Start_Uses_ServerSide_Actor_Not_RequestBody
//     9.  Withdraw_NonAdmin_IsAdmin_Ignored
//    10.  Withdraw_NotInitiator_Returns403
//    11.  Withdraw_CannotWithdrawAlreadyFinal_Returns409
//
//   Task controller:
//    12.  Inbox_Returns_Only_Callers_PendingTasks
//    13.  Approve_Uses_ServerSide_Actor
//    14.  Reject_Uses_ServerSide_Actor
//    15.  Approve_TaskNotActive_Returns409 (non-assignee)
//
//   CC cross-tenant validation (WF-14 ICcTenantValidator):
//    16.  CcHandler_RejectsInvalidTenantRecipient_NoCcRecordWritten
//    17.  CcHandler_AcceptsValidTenantRecipient_CcRecordWritten
//    18.  CcTenantValidator_FallbackTrue_WhenFrameworkUserMissing
//
// All DB tests use SQLite shared-in-memory (NEVER EF InMemory — #119/#162).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.WorkFlow.Controllers;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ── Helper: wire a WorkFlow controller with a mock Wtm context ─────────────────

internal static class ControllerTestHelpers
{
    /// <summary>
    /// Creates a <typeparamref name="T"/> controller wired with a minimal WTMContext
    /// seeded with the given <paramref name="itCode"/> and optional <paramref name="tenantCode"/>.
    /// </summary>
    public static T WireWtm<T>(T controller, string itCode = "actor1", string? tenantCode = null)
        where T : WalkingTec.Mvvm.Mvc.BaseController
    {
        controller.Wtm = MockWtmContext.CreateWtmContext(usercode: itCode);
        controller.Wtm.LoginUserInfo!.TenantCode = tenantCode;
        Mock<HttpContext> mockHttpContext = new();
        mockHttpContext.Setup(s => s.Session).Returns(new MockHttpSession());
        mockHttpContext.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = mockHttpContext.Object,
        };
        return controller;
    }
}

// ── Definition controller tests ───────────────────────────────────────────────

[TestClass]
public class WorkflowDefinitionControllerTests
{
    private Mock<IProcessDefinitionPublisher> _publisherMock = null!;
    private WorkflowDefinitionController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _publisherMock = new Mock<IProcessDefinitionPublisher>(MockBehavior.Strict);
        _controller = new WorkflowDefinitionController(_publisherMock.Object);
        ControllerTestHelpers.WireWtm(_controller, itCode: "admin1");
    }

    private static WorkflowGraph ValidGraph() => new()
    {
        Key = "test",
        Name = "test",
        Nodes = new List<NodeDef>
        {
            new() { NodeKey = "start", Kind = NodeKind.Start },
            new() { NodeKey = "end",   Kind = NodeKind.End },
        },
        Transitions = new List<TransitionDef>
        {
            new() { From = "start", To = "end" },
        },
    };

    // ── Test 1: Published → 200 ────────────────────────────────────────────────

    [TestMethod]
    public async Task Publish_Published_Returns200()
    {
        var versionId = Guid.NewGuid();
        _publisherMock
            .Setup(p => p.PublishAsync("flow1", It.IsAny<WorkflowGraph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishResult.NewVersion(versionId, 1, "hash1"));

        var result = await _controller.Publish("flow1", ValidGraph()) as OkObjectResult;

        Assert.IsNotNull(result, "Expected 200 OK for Published.");
        var dto = result!.Value as PublishResponseDto;
        Assert.IsTrue(dto!.Success);
        Assert.AreEqual("Published", dto.Outcome);
    }

    // ── Test 2: IdempotentNoOp → 200 ──────────────────────────────────────────

    [TestMethod]
    public async Task Publish_IdempotentNoOp_Returns200()
    {
        var versionId = Guid.NewGuid();
        _publisherMock
            .Setup(p => p.PublishAsync("flow1", It.IsAny<WorkflowGraph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishResult.NoOp(versionId, 2, "hash2"));

        var result = await _controller.Publish("flow1", ValidGraph()) as OkObjectResult;

        Assert.IsNotNull(result, "Expected 200 OK for IdempotentNoOp.");
        var dto = result!.Value as PublishResponseDto;
        Assert.IsTrue(dto!.Success);
        Assert.AreEqual("IdempotentNoOp", dto.Outcome);
    }

    // ── Test 3: ValidationFailed → 400 ────────────────────────────────────────

    [TestMethod]
    public async Task Publish_ValidationFailed_Returns400()
    {
        _publisherMock
            .Setup(p => p.PublishAsync("flow1", It.IsAny<WorkflowGraph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishResult.Invalid(GraphValidationError.MissingStartNode, "Must have exactly one Start node."));

        var result = await _controller.Publish("flow1", ValidGraph()) as BadRequestObjectResult;

        Assert.IsNotNull(result, "Expected 400 for ValidationFailed.");
        var dto = result!.Value as PublishResponseDto;
        Assert.IsFalse(dto!.Success);
        Assert.AreEqual("ValidationFailed", dto.Outcome);
    }

    // ── Test 4: DefinitionNotFound → 404 ──────────────────────────────────────

    [TestMethod]
    public async Task Publish_DefinitionNotFound_Returns404()
    {
        _publisherMock
            .Setup(p => p.PublishAsync("flow1", It.IsAny<WorkflowGraph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishResult.NotFound("flow1")); // uses the existing NotFound factory

        var result = await _controller.Publish("flow1", ValidGraph()) as NotFoundObjectResult;

        Assert.IsNotNull(result, "Expected 404 for DefinitionNotFound.");
        var dto = result!.Value as PublishResponseDto;
        Assert.IsFalse(dto!.Success);
        Assert.AreEqual("DefinitionNotFound", dto.Outcome);
    }

    // ── Test 5: Server-side actor is passed (not request body) ────────────────

    [TestMethod]
    public async Task Publish_Uses_ServerSide_Actor_Not_RequestBody()
    {
        // The Wtm.LoginUserInfo.ITCode is "admin1" (set in Setup).
        // The graph itself has no actor field — the test confirms the engine receives
        // the server-side actor, not anything from the client body.
        string? capturedPublishedBy = null;
        _publisherMock
            .Setup(p => p.PublishAsync("flow1", It.IsAny<WorkflowGraph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorkflowGraph, string?, CancellationToken>((_, _, pb, _) => capturedPublishedBy = pb)
            .ReturnsAsync(PublishResult.NewVersion(Guid.NewGuid(), 1, "hash"));

        await _controller.Publish("flow1", ValidGraph());

        Assert.AreEqual("admin1", capturedPublishedBy,
            "publishedBy must come from Wtm.LoginUserInfo.ITCode, not the request body.");
    }

    // ── Test 6: Valid graph → 200 (validate) ──────────────────────────────────

    [TestMethod]
    public void Validate_Valid_Returns200()
    {
        var result = _controller.Validate(ValidGraph()) as OkObjectResult;

        Assert.IsNotNull(result, "Expected 200 OK for valid graph.");
        var dto = result!.Value as ValidateResponseDto;
        Assert.IsTrue(dto!.IsValid);
    }

    // ── Test 7: Invalid graph → 400 (validate) ────────────────────────────────

    [TestMethod]
    public void Validate_Invalid_Returns400()
    {
        // Graph with no Start node → invalid.
        var badGraph = new WorkflowGraph
        {
            Key = "bad",
            Name = "bad",
            Nodes = new List<NodeDef> { new() { NodeKey = "end", Kind = NodeKind.End } },
            Transitions = new List<TransitionDef>(),
        };

        var result = _controller.Validate(badGraph) as BadRequestObjectResult;

        Assert.IsNotNull(result, "Expected 400 for invalid graph.");
        var dto = result!.Value as ValidateResponseDto;
        Assert.IsFalse(dto!.IsValid);
    }
}

// ── Instance controller tests ─────────────────────────────────────────────────

[TestClass]
public class WorkflowInstanceControllerTests
{
    private Mock<IWorkflowEngine> _engineMock = null!;
    private WorkflowInstanceController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _engineMock = new Mock<IWorkflowEngine>(MockBehavior.Strict);
        _controller = new WorkflowInstanceController(_engineMock.Object);
        ControllerTestHelpers.WireWtm(_controller, itCode: "initiator1", tenantCode: "tenant1");
    }

    // ── Test 8: Start uses server-side actor ──────────────────────────────────

    [TestMethod]
    public async Task Start_Uses_ServerSide_Actor_Not_RequestBody()
    {
        var versionId = Guid.NewGuid();
        string? capturedActor = null;
        string? capturedTenant = null;

        var instance = new ProcessInstance
        {
            ID = Guid.NewGuid(),
            State = InstanceState.Approved,
            InitiatorITCode = "initiator1",
        };

        _engineMock
            .Setup(e => e.StartAsync(
                versionId,
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string?, string, string?, string?, string?, CancellationToken>(
                (_, _, actor, tenant, _, _, _) =>
                {
                    capturedActor  = actor;
                    capturedTenant = tenant;
                })
            .ReturnsAsync(instance);

        // Client tries to supply InitiatorITCode in the body — must be ignored.
        var request = new StartInstanceRequest
        {
            DefinitionVersionId = versionId,
            InitiatorITCode = "attacker",  // [BindNever] in production; we verify ignored here
            TenantCode      = "evil-tenant",  // also [BindNever]
        };

        var result = await _controller.Start(request) as OkObjectResult;

        Assert.IsNotNull(result, "Expected 200 OK.");
        Assert.AreEqual("initiator1", capturedActor,
            "Actor must come from Wtm.LoginUserInfo.ITCode, not request body.");
        Assert.AreEqual("tenant1", capturedTenant,
            "TenantCode must come from Wtm.LoginUserInfo.TenantCode, not request body.");
    }

    // ── Test 9: Withdraw — non-admin cannot self-escalate with IsAdmin=true ───

    [TestMethod]
    public async Task Withdraw_NonAdmin_IsAdminFlagInBody_IsIgnored()
    {
        // The WTMContext for this test has no FunctionPrivileges → IsAccessable returns false.
        // So even if the client sends IsAdmin=true, the controller must pass isAdmin=false.
        bool? capturedIsAdmin = null;
        var instanceId = Guid.NewGuid();

        _engineMock
            .Setup(e => e.WithdrawAsync(
                instanceId,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string?, bool, CancellationToken>(
                (_, _, _, isAdmin, _) => capturedIsAdmin = isAdmin)
            .ReturnsAsync(WorkflowActionResult.Withdrawn);

        var request = new WithdrawRequest { IsAdmin = true }; // client tries to self-escalate

        await _controller.Withdraw(instanceId, request);

        // Because the user has no FunctionPrivilege for WorkflowAdmin URL,
        // Wtm.IsAccessable returns false → isAdmin must have been passed as false.
        Assert.AreEqual(false, capturedIsAdmin,
            "IsAdmin flag from request body must be ignored when caller lacks admin privilege.");
    }

    // ── Test 10: Withdraw NotInitiator → 403 ─────────────────────────────────

    [TestMethod]
    public async Task Withdraw_NotInitiator_Returns403()
    {
        var instanceId = Guid.NewGuid();
        _engineMock
            .Setup(e => e.WithdrawAsync(instanceId, "initiator1", null, false, default))
            .ReturnsAsync(WorkflowActionResult.NotInitiator);

        var result = await _controller.Withdraw(instanceId, null);

        Assert.IsInstanceOfType(result, typeof(ObjectResult));
        Assert.AreEqual(StatusCodes.Status403Forbidden,
            ((ObjectResult)result!).StatusCode,
            "NotInitiator must map to 403 Forbidden.");
    }

    // ── Test 11: Withdraw CannotWithdrawAlreadyFinal → 409 ────────────────────

    [TestMethod]
    public async Task Withdraw_CannotWithdrawAlreadyFinal_Returns409()
    {
        var instanceId = Guid.NewGuid();
        _engineMock
            .Setup(e => e.WithdrawAsync(instanceId, "initiator1", null, false, default))
            .ReturnsAsync(WorkflowActionResult.CannotWithdrawAlreadyFinal);

        var result = await _controller.Withdraw(instanceId, null) as ConflictObjectResult;

        Assert.IsNotNull(result, "CannotWithdrawAlreadyFinal must map to 409 Conflict.");
    }
}

// ── Task controller tests ─────────────────────────────────────────────────────

[TestClass]
public class WorkflowTaskControllerTests
{
    private Mock<IWorkflowEngine> _engineMock = null!;
    private WorkflowTaskController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _engineMock = new Mock<IWorkflowEngine>(MockBehavior.Strict);
        _controller = new WorkflowTaskController(_engineMock.Object);
        ControllerTestHelpers.WireWtm(_controller, itCode: "approver1", tenantCode: "tenant1");
    }

    // ── Test 12: Inbox returns only caller's pending tasks ────────────────────

    [TestMethod]
    public async Task Inbox_Returns_OnlyCallers_PendingTasks()
    {
        var task1Id = Guid.NewGuid();
        var nodeInst = new NodeInstance { NodeKey = "approval1" };
        var tasks = new List<ApprovalTask>
        {
            new()
            {
                ID              = task1Id,
                AssigneeITCode  = "approver1",
                TenantCode      = "tenant1",
                State           = TaskState.Pending,
                NodeInstance    = nodeInst,
                NodeInstanceId  = Guid.NewGuid(),
                IsValid         = true,
            },
        };

        string? capturedActor = null;
        string? capturedTenant = null;

        _engineMock
            .Setup(e => e.GetPendingTasksAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>(
                (actor, tenant, _) => { capturedActor = actor; capturedTenant = tenant; })
            .ReturnsAsync((IReadOnlyList<ApprovalTask>)tasks.AsReadOnly());

        var result = await _controller.Inbox() as OkObjectResult;

        Assert.IsNotNull(result, "Expected 200 OK.");
        Assert.AreEqual("approver1", capturedActor,
            "Inbox actor must come from Wtm.LoginUserInfo.ITCode.");
        Assert.AreEqual("tenant1", capturedTenant,
            "Inbox tenant must come from Wtm.LoginUserInfo.TenantCode.");

        var items = result!.Value as TaskInboxItem[];
        Assert.IsNotNull(items);
        Assert.AreEqual(1, items!.Length);
        Assert.AreEqual(task1Id, items[0].TaskId);
    }

    // ── Test 13: Approve uses server-side actor ───────────────────────────────

    [TestMethod]
    public async Task Approve_Uses_ServerSide_Actor()
    {
        var taskId = Guid.NewGuid();
        string? capturedActor = null;

        _engineMock
            .Setup(e => e.ApproveTaskAsync(
                taskId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string?, CancellationToken>(
                (_, actor, _, _) => capturedActor = actor)
            .ReturnsAsync(WorkflowActionResult.Advanced);

        // Client tries to pass a different actor in the body (the [BindNever] ActorITCode is
        // controlled by model binding; here we verify the controller ignores it regardless).
        var request = new TaskActionRequest { Comment = "Looks good.", ActorITCode = "attacker" };
        await _controller.Approve(taskId, request);

        Assert.AreEqual("approver1", capturedActor,
            "Approve actor must come from Wtm.LoginUserInfo.ITCode, not request body.");
    }

    // ── Test 14: Reject uses server-side actor ────────────────────────────────

    [TestMethod]
    public async Task Reject_Uses_ServerSide_Actor()
    {
        var taskId = Guid.NewGuid();
        string? capturedActor = null;

        _engineMock
            .Setup(e => e.RejectTaskAsync(
                taskId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string?, CancellationToken>(
                (_, actor, _, _) => capturedActor = actor)
            .ReturnsAsync(WorkflowActionResult.Rejected);

        var request = new TaskActionRequest { Comment = "Nope.", ActorITCode = "attacker" };
        await _controller.Reject(taskId, request);

        Assert.AreEqual("approver1", capturedActor,
            "Reject actor must come from Wtm.LoginUserInfo.ITCode, not request body.");
    }

    // ── Test 15: Non-assignee approve → TaskNotActive → 409 ──────────────────

    [TestMethod]
    public async Task Approve_TaskNotActive_Returns409()
    {
        // Engine returns TaskNotActive when actorITCode != AssigneeITCode.
        var taskId = Guid.NewGuid();
        _engineMock
            .Setup(e => e.ApproveTaskAsync(taskId, "approver1", null, default))
            .ReturnsAsync(WorkflowActionResult.TaskNotActive);

        var result = await _controller.Approve(taskId, null) as ConflictObjectResult;

        Assert.IsNotNull(result, "TaskNotActive must map to 409 Conflict.");
        var response = result!.Value as WorkflowActionResponse;
        Assert.AreEqual("TaskNotActive", response!.ResultCode);
    }
}

// ── CC cross-tenant validation (WF-14) ───────────────────────────────────────

/// <summary>
/// Tests for <see cref="ICcTenantValidator"/> and its integration with CcHandler.
/// Uses a SQLite shared-in-memory context identical to the pattern in EngineTests.cs.
/// </summary>
[TestClass]
public class CcTenantValidatorTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfCcValidator_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var ctx = MakeContext();
        ctx.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    private WfEngineTestContext MakeContext() => new(_dbName);

    // Engine with a mock validator so we can control accept/reject per-recipient.
    private (IWorkflowEngine engine, WfEngineTestContext ctx) MakeEngineWithValidator(
        ICcTenantValidator validator)
    {
        var ctx = MakeContext();
        var resolver = new StaticApproverResolver(); // from WithdrawCcTests — resolves "cc_user"
        var options  = new WorkFlowOptions();
        var optWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var seqHandler = new SequentialApprovalHandler(
            resolver, optWrapper, NullLogger<SequentialApprovalHandler>.Instance);
        var allHandler = new AllApprovalHandler(
            resolver, optWrapper, NullLogger<AllApprovalHandler>.Instance);
        var anyHandler = new AnyApprovalHandler(
            resolver, optWrapper, NullLogger<AnyApprovalHandler>.Instance);
        var approval = new ApprovalHandler(seqHandler, allHandler, anyHandler);
        var cc       = new CcHandler(resolver, validator, NullLogger<CcHandler>.Instance);
        var ack              = new AckHandler(NullLogger<AckHandler>.Instance);
        var join             = new JoinHandler();
        var routingEval      = new WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator(
            NullLogger<WalkingTec.Mvvm.WorkFlow.Engine.Routing.WhitelistRoutingEvaluator>.Instance);
        var parallelGateway  = new ParallelGatewayHandler(NullLogger<ParallelGatewayHandler>.Instance);
        var inclusiveGateway = new InclusiveGatewayHandler(routingEval, NullLogger<InclusiveGatewayHandler>.Instance);
        var dispatcher = new NodeKindDispatcher(cc, approval, ack, join, parallelGateway, inclusiveGateway);
        var engine     = WorkflowEngine_Exposed.CreateWithOptions(
            ctx, dispatcher, options, NullLogger.Instance);
        return (engine, ctx);
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfEngineTestContext ctx,
        string graphJson,
        string? tenantCode = null)
    {
        var version = new ProcessDefinitionVersion
        {
            ID            = Guid.NewGuid(),
            DefinitionId  = Guid.NewGuid(),
            VersionNo     = 1,
            SchemaVersion = 1,
            GraphJson     = graphJson,
            ContentHash   = "cv-hash-" + Guid.NewGuid().ToString("N"),
            PublishedAt   = DateTime.UtcNow,
            PublishedBy   = "test",
            TenantCode    = tenantCode,
            IsValid       = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    // ── Test 16: Validator rejects → no CcRecord written ─────────────────────

    [TestMethod]
    public async Task CcHandler_RejectsInvalidTenantRecipient_NoCcRecordWritten()
    {
        // Validator always returns false (simulates cross-tenant recipient).
        var rejectAll = new RejectAllCcTenantValidator();
        var (engine, ctx) = MakeEngineWithValidator(rejectAll);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx,
            EngineTestHelpers.StartCcEndGraph(recipientITCode: "cross_tenant_user"),
            tenantCode: "tenant_A");

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: null,
            initiatorITCode: "init1",
            tenantCode: "tenant_A",
            ct: CancellationToken.None);

        // Instance must still complete (CC nodes never block).
        Assert.AreEqual(InstanceState.Approved, instance.State,
            "CC node must not block even when recipient is rejected.");

        // No CcRecord must have been written for the rejected recipient.
        await using var verify = MakeContext();
        var count = await verify.Set<CcRecord>()
            .Where(r => r.InstanceId == instance.ID)
            .CountAsync();

        Assert.AreEqual(0, count,
            "No CcRecord must be written when tenant validator rejects the recipient.");
    }

    // ── Test 17: Validator accepts → CcRecord written with instance TenantCode ─

    [TestMethod]
    public async Task CcHandler_AcceptsValidTenantRecipient_CcRecordWritten()
    {
        // AcceptAllCcTenantValidator (already defined in EngineTests.cs) always returns true.
        var acceptAll = new AcceptAllCcTenantValidator();
        var (engine, ctx) = MakeEngineWithValidator(acceptAll);
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx,
            EngineTestHelpers.StartCcEndGraph(recipientITCode: "same_tenant_user"),
            tenantCode: "tenant_A");

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: null,
            initiatorITCode: "init1",
            tenantCode: "tenant_A",
            ct: CancellationToken.None);

        await using var verify = MakeContext();
        var records = await verify.Set<CcRecord>()
            .Where(r => r.InstanceId == instance.ID)
            .ToListAsync();

        Assert.AreEqual(1, records.Count,
            "One CcRecord must be written for the accepted recipient.");
        Assert.AreEqual("same_tenant_user", records[0].RecipientITCode);
        // CcRecord TenantCode must match the INSTANCE's tenant, not some other value.
        Assert.AreEqual("tenant_A", records[0].TenantCode,
            "CcRecord.TenantCode must match the process instance's TenantCode.");
    }

    // ── Test 18: CcTenantValidator fallback when FrameworkUser not in DbContext ─

    [TestMethod]
    public async Task CcTenantValidator_ReturnsTrueWhenFrameworkUserNotInDbContext()
    {
        // Use the real CcTenantValidator. The WfEngineTestContext does NOT have a
        // FrameworkUser DbSet → the fallback path (InvalidOperationException caught) must
        // return true and log a debug message, not throw.
        var validator = new CcTenantValidator(NullLogger<CcTenantValidator>.Instance);

        await using var ctx = MakeContext();

        // Act: the validator should catch the InvalidOperationException and return true.
        var result = await validator.IsValidTenantUserAsync(
            ctx, "any_user", "tenant_A", CancellationToken.None);

        Assert.IsTrue(result,
            "CcTenantValidator must fall back to true when FrameworkUser table is absent " +
            "(e.g. test contexts that only contain WorkFlow tables).");
    }

    // ── Local test stubs ──────────────────────────────────────────────────────

    /// <summary>Validator that unconditionally rejects every recipient.</summary>
    private sealed class RejectAllCcTenantValidator : ICcTenantValidator
    {
        public Task<bool> IsValidTenantUserAsync(
            DbContext db,
            string recipientITCode,
            string? tenantCode,
            CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
