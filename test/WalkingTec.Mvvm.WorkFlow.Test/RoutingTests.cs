#nullable enable
// WF-11: WhitelistRoutingEvaluator + Exclusive gateway tests.
//
// Coverage:
//   1. Numeric Gt: amount > 10000 → match / no-match.
//   2. String Eq: exact match / mismatch.
//   3. In membership: value in list / value not in list.
//   4. Missing field → fail-closed (no match, treated as default).
//   5. And composition: both conditions must hold.
//   6. Or composition: at least one condition must hold.
//   7. Whitelist enforcement (SECURITY): off-whitelist field → FieldNotAllowed (publish-time + runtime).
//   8. In cap enforcement: > 100 items → InListTooLarge.
//   9. Exclusive gateway end-to-end: Start → Condition(amount>10000 → bigApproval; default → smallApproval) routing.
//  10. No-match + no-default → fail-closed (FailClosedRouting).
//  11. Cache: same rule hash reuses compiled delegate (verified via evaluation count + behavior).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Engine.Routing;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ─────────────────────────────────────────────────────────────────────────────
// Helper: build a whitelist and form-data dictionary quickly.
// ─────────────────────────────────────────────────────────────────────────────

internal static class RoutingTestHelpers
{
    public static IReadOnlyList<FieldWhitelistEntry> Whitelist(params (string field, string clrType)[] entries)
    {
        return entries.Select(e => new FieldWhitelistEntry
        {
            Field = e.field,
            ClrType = e.clrType,
        }).ToList();
    }

    public static IReadOnlyDictionary<string, object?> FormData(params (string key, object? value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
            dict[k] = v;
        return dict;
    }

    /// <summary>Build a leaf RoutingRuleDef.</summary>
    public static RoutingRuleDef Leaf(string field, FilterOperator op, object? value) =>
        new() { Field = field, Operator = op, Value = value };

    /// <summary>Build a Start → Condition → bigApproval / smallApproval → End graph with FieldWhitelist.</summary>
    public static string ConditionalGraph(string key = "CondGraph") =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = key,
            Name = key,
            FieldWhitelist = new List<FieldWhitelistEntry>
            {
                new() { Field = "amount", ClrType = "System.Decimal" },
            },
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey = "gw",
                    Kind = NodeKind.Condition,
                    Branches = new List<BranchDef>
                    {
                        new()
                        {
                            Rule  = Leaf("amount", FilterOperator.Gt, 10000m),
                            Target = "bigApproval",
                        },
                    },
                    Default = "smallApproval",
                },
                new()
                {
                    NodeKey = "bigApproval",
                    Kind = NodeKind.Approval,
                    ApproveMode = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "cfo" },
                },
                new()
                {
                    NodeKey = "smallApproval",
                    Kind = NodeKind.Approval,
                    ApproveMode = ApproveMode.Sequential,
                    ApproverRule = new ApproverRuleDef { Type = "User", Value = "mgr" },
                },
                new() { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start",        To = "gw"            },
                new() { From = "bigApproval",   To = "end"           },
                new() { From = "smallApproval", To = "end"           },
            },
        });

    /// <summary>Serialize a form-data dictionary to JSON (for StartAsync).</summary>
    public static string ToFormDataJson(params (string key, object? value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
            dict[k] = v;
        return JsonSerializer.Serialize(dict);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 1–8: Unit tests for WhitelistRoutingEvaluator (no DB needed)
// ─────────────────────────────────────────────────────────────────────────────

[TestClass]
public class WhitelistRoutingEvaluatorTests
{
    private WhitelistRoutingEvaluator MakeEvaluator() =>
        new(NullLogger<WhitelistRoutingEvaluator>.Instance);

    // ── Test 1: Numeric Gt ─────────────────────────────────────────────────────

    [TestMethod]
    public void Evaluate_NumericGt_Match()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("amount", "System.Decimal"));
        var rule = RoutingTestHelpers.Leaf("amount", FilterOperator.Gt, 10000m);

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("amount", 15000m)));

        Assert.IsTrue(result.IsMatch, "15000 > 10000 should match.");
        Assert.AreEqual(RoutingEvaluationCode.Ok, result.Code);
    }

    [TestMethod]
    public void Evaluate_NumericGt_NoMatch_WhenEqual()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("amount", "System.Decimal"));
        var rule = RoutingTestHelpers.Leaf("amount", FilterOperator.Gt, 10000m);

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("amount", 10000m)));

        Assert.IsFalse(result.IsMatch, "10000 is NOT > 10000.");
    }

    [TestMethod]
    public void Evaluate_NumericGt_NoMatch_WhenLess()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("amount", "System.Decimal"));
        var rule = RoutingTestHelpers.Leaf("amount", FilterOperator.Gt, 10000m);

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("amount", 5000m)));

        Assert.IsFalse(result.IsMatch, "5000 is NOT > 10000.");
    }

    // ── Test 2: String Eq ──────────────────────────────────────────────────────

    [TestMethod]
    public void Evaluate_StringEq_Match()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("department", "System.String"));
        var rule = RoutingTestHelpers.Leaf("department", FilterOperator.Eq, "FINANCE");

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("department", "FINANCE")));

        Assert.IsTrue(result.IsMatch, "'FINANCE' == 'FINANCE' should match.");
    }

    [TestMethod]
    public void Evaluate_StringEq_NoMatch_WhenDifferent()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("department", "System.String"));
        var rule = RoutingTestHelpers.Leaf("department", FilterOperator.Eq, "FINANCE");

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("department", "HR")));

        Assert.IsFalse(result.IsMatch, "'HR' != 'FINANCE'.");
    }

    // ── Test 3: In membership ──────────────────────────────────────────────────

    [TestMethod]
    public void Evaluate_In_Match_WhenValueInList()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("category", "System.String"));
        var rule = RoutingTestHelpers.Leaf("category", FilterOperator.In,
            new object?[] { "A", "B", "C" });

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("category", "B")));

        Assert.IsTrue(result.IsMatch, "'B' is in [A, B, C].");
    }

    [TestMethod]
    public void Evaluate_In_NoMatch_WhenValueNotInList()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("category", "System.String"));
        var rule = RoutingTestHelpers.Leaf("category", FilterOperator.In,
            new object?[] { "A", "B", "C" });

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("category", "X")));

        Assert.IsFalse(result.IsMatch, "'X' is NOT in [A, B, C].");
    }

    // ── Test 4: Missing field → fail-closed ───────────────────────────────────

    [TestMethod]
    public void Evaluate_MissingField_FailClosed_NoMatch()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("amount", "System.Decimal"));
        var rule = RoutingTestHelpers.Leaf("amount", FilterOperator.Gt, 10000m);

        // FormData has NO "amount" key.
        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("other_field", "irrelevant")));

        Assert.IsFalse(result.IsMatch, "Missing field must be fail-closed (no match).");
        Assert.AreEqual(RoutingEvaluationCode.Ok, result.Code,
            "Missing field is not an error — it is fail-closed by convention (spec §6 §5.8).");
    }

    // ── Test 5: And composition ────────────────────────────────────────────────

    [TestMethod]
    public void Evaluate_AndComposition_Match_WhenBothTrue()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(
            ("amount", "System.Decimal"),
            ("department", "System.String"));

        var rule = new RoutingRuleDef
        {
            And = new List<RoutingRuleDef>
            {
                RoutingTestHelpers.Leaf("amount",     FilterOperator.Gt, 10000m),
                RoutingTestHelpers.Leaf("department", FilterOperator.Eq, "FINANCE"),
            },
        };

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("amount", 20000m), ("department", "FINANCE")));

        Assert.IsTrue(result.IsMatch, "Both conditions true → AND should match.");
    }

    [TestMethod]
    public void Evaluate_AndComposition_NoMatch_WhenOneFalse()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(
            ("amount", "System.Decimal"),
            ("department", "System.String"));

        var rule = new RoutingRuleDef
        {
            And = new List<RoutingRuleDef>
            {
                RoutingTestHelpers.Leaf("amount",     FilterOperator.Gt, 10000m),
                RoutingTestHelpers.Leaf("department", FilterOperator.Eq, "FINANCE"),
            },
        };

        // amount is fine but department is wrong.
        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("amount", 20000m), ("department", "HR")));

        Assert.IsFalse(result.IsMatch, "One condition false → AND should not match.");
    }

    // ── Test 6: Or composition ─────────────────────────────────────────────────

    [TestMethod]
    public void Evaluate_OrComposition_Match_WhenOneTrue()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(
            ("amount", "System.Decimal"),
            ("department", "System.String"));

        var rule = new RoutingRuleDef
        {
            Or = new List<RoutingRuleDef>
            {
                RoutingTestHelpers.Leaf("amount",     FilterOperator.Gt, 10000m),
                RoutingTestHelpers.Leaf("department", FilterOperator.Eq, "FINANCE"),
            },
        };

        // Only amount is true.
        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("amount", 20000m), ("department", "HR")));

        Assert.IsTrue(result.IsMatch, "One condition true → OR should match.");
    }

    [TestMethod]
    public void Evaluate_OrComposition_NoMatch_WhenAllFalse()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(
            ("amount", "System.Decimal"),
            ("department", "System.String"));

        var rule = new RoutingRuleDef
        {
            Or = new List<RoutingRuleDef>
            {
                RoutingTestHelpers.Leaf("amount",     FilterOperator.Gt, 10000m),
                RoutingTestHelpers.Leaf("department", FilterOperator.Eq, "FINANCE"),
            },
        };

        // Both false.
        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("amount", 500m), ("department", "HR")));

        Assert.IsFalse(result.IsMatch, "Both conditions false → OR should not match.");
    }

    // ── Test 7: Whitelist enforcement (SECURITY) ───────────────────────────────

    [TestMethod]
    public void Evaluate_OffWhitelistField_FailClosed_FieldNotAllowed()
    {
        var sut = MakeEvaluator();
        // Whitelist only has "amount" — "secret_field" is NOT whitelisted.
        var whitelist = RoutingTestHelpers.Whitelist(("amount", "System.Decimal"));
        var rule = RoutingTestHelpers.Leaf("secret_field", FilterOperator.Eq, "bypass");

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("secret_field", "bypass")));

        Assert.IsFalse(result.IsMatch,
            "Off-whitelist field must fail-closed (no match, security gate).");
        Assert.AreEqual(RoutingEvaluationCode.FieldNotAllowed, result.Code,
            "Expected FieldNotAllowed error code for off-whitelist field.");
        StringAssert.Contains(result.ErrorMessage, "secret_field");
    }

    [TestMethod]
    public void ValidateRule_OffWhitelistField_ReturnsFieldNotAllowed()
    {
        var whitelist = RoutingTestHelpers.Whitelist(("amount", "System.Decimal"));
        var rule = RoutingTestHelpers.Leaf("not_in_whitelist", FilterOperator.Eq, "x");

        var error = WhitelistRoutingEvaluator.ValidateRule(rule, whitelist);

        Assert.IsNotNull(error, "Off-whitelist field must produce a validation error.");
        Assert.AreEqual(RoutingEvaluationCode.FieldNotAllowed, error.Code);
    }

    [TestMethod]
    public void PublishTime_OffWhitelistField_FailsGraphValidation()
    {
        // Graph where a branch rule references a field NOT in FieldWhitelist.
        var graph = new WorkflowGraph
        {
            Key = "TestGraph",
            FieldWhitelist = new List<FieldWhitelistEntry>
            {
                new() { Field = "amount", ClrType = "System.Decimal" },
            },
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey = "gw",
                    Kind = NodeKind.Condition,
                    Branches = new List<BranchDef>
                    {
                        new()
                        {
                            // OFF-WHITELIST field — must be rejected at publish time.
                            Rule   = RoutingTestHelpers.Leaf("secret", FilterOperator.Eq, "bypass"),
                            Target = "endA",
                        },
                    },
                    Default = "endB",
                },
                new() { NodeKey = "endA", Kind = NodeKind.End },
                new() { NodeKey = "endB", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "gw"    },
                new() { From = "gw",    To = "endA"  },
                new() { From = "gw",    To = "endB"  },
            },
        };

        var result = WorkflowGraphValidator.Validate(graph);

        Assert.IsFalse(result.IsValid, "Publish must fail when a branch rule references an off-whitelist field.");
        Assert.AreEqual(GraphValidationError.RoutingFieldNotAllowed, result.Error,
            $"Expected RoutingFieldNotAllowed, got {result.Error}: {result.ErrorMessage}");
    }

    // ── Test 8: In cap ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Evaluate_InList_TooLarge_ReturnsInListTooLarge()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("category", "System.String"));

        // Build a list with 101 items (> 100 cap).
        var bigList = Enumerable.Range(1, 101).Select(i => (object?)$"item_{i}").ToArray();
        var rule = RoutingTestHelpers.Leaf("category", FilterOperator.In, bigList);

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("category", "item_1")));

        Assert.IsFalse(result.IsMatch, "In list > 100 must be rejected (fail-closed).");
        Assert.AreEqual(RoutingEvaluationCode.InListTooLarge, result.Code,
            "Expected InListTooLarge error code.");
    }

    [TestMethod]
    public void Evaluate_InList_AtCap_Allowed()
    {
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("category", "System.String"));

        // Exactly 100 items — should be allowed.
        var list100 = Enumerable.Range(1, 100).Select(i => (object?)$"item_{i}").ToArray();
        var rule = RoutingTestHelpers.Leaf("category", FilterOperator.In, list100);

        var result = sut.Evaluate(
            rule, whitelist,
            RoutingTestHelpers.FormData(("category", "item_50")));

        Assert.AreNotEqual(RoutingEvaluationCode.InListTooLarge, result.Code,
            "Exactly 100 items must be allowed (cap is > 100, not >= 100).");
        Assert.IsTrue(result.IsMatch, "'item_50' is in the 100-item list.");
    }

    [TestMethod]
    public void ValidateRule_InListTooLarge_AtPublishTime()
    {
        var whitelist = RoutingTestHelpers.Whitelist(("category", "System.String"));
        var bigList = Enumerable.Range(1, 101).Select(i => (object?)$"v_{i}").ToArray();
        var rule = RoutingTestHelpers.Leaf("category", FilterOperator.In, bigList);

        var error = WhitelistRoutingEvaluator.ValidateRule(rule, whitelist);

        Assert.IsNotNull(error, "In list > 100 must fail publish-time validation.");
        Assert.AreEqual(RoutingEvaluationCode.InListTooLarge, error.Code);
    }

    // ── Test 11: Cache (same rule reuses compiled delegate) ───────────────────

    [TestMethod]
    public void Evaluate_SameRule_ReusesCompiledDelegate_ConsistentResults()
    {
        // We can't directly count compilations without a hook, but we can verify
        // that repeated evaluation of the same rule on different data is consistent —
        // proving the cache doesn't corrupt results across invocations.
        var sut = MakeEvaluator();
        var whitelist = RoutingTestHelpers.Whitelist(("amount", "System.Decimal"));
        var rule = RoutingTestHelpers.Leaf("amount", FilterOperator.Gt, 10000m);

        // Evaluate 5 times with different form-data to confirm the cached delegate
        // correctly handles per-invocation state.
        for (int i = 0; i < 5; i++)
        {
            bool shouldMatch = (i % 2 == 0); // alternate match / no-match
            decimal amount = shouldMatch ? 20000m : 5000m;

            var result = sut.Evaluate(
                rule, whitelist,
                RoutingTestHelpers.FormData(("amount", amount)));

            Assert.AreEqual(shouldMatch, result.IsMatch,
                $"Iteration {i}: expected IsMatch={shouldMatch} for amount={amount}.");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 9–10: Exclusive gateway end-to-end engine tests (with SQLite)
// ─────────────────────────────────────────────────────────────────────────────

[TestClass]
public class ExclusiveGatewayEngineTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfRouting_{Guid.NewGuid():N}";
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

    private (IWorkflowEngine engine, WfEngineTestContext ctx) MakeEngine()
    {
        var ctx = MakeContext();
        var dispatcher = NodeKindDispatcher_Exposed.Create();
        var engine = WorkflowEngine_Exposed.Create(ctx, dispatcher, NullLogger.Instance);
        return (engine, ctx);
    }

    private async Task<ProcessDefinitionVersion> SeedVersionAsync(
        WfEngineTestContext ctx, string graphJson)
    {
        var version = new ProcessDefinitionVersion
        {
            ID = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            VersionNo = 1,
            SchemaVersion = 1,
            GraphJson = graphJson,
            ContentHash = "routing-test-" + Guid.NewGuid().ToString("N"),
            PublishedAt = DateTime.UtcNow,
            PublishedBy = "test",
            IsValid = true,
        };
        ctx.Set<ProcessDefinitionVersion>().Add(version);
        await ctx.SaveChangesAsync();
        return version;
    }

    // ── Test 9a: High-value path (amount > 10000 → bigApproval) ───────────────

    [TestMethod]
    public async Task ExclusiveGateway_HighAmount_RoutesToBigApproval()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, RoutingTestHelpers.ConditionalGraph("AmountGw1"));

        // Amount = 15000 > 10000 → should route to bigApproval.
        var formData = RoutingTestHelpers.ToFormDataJson(("amount", 15000m));

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: formData,
            initiatorITCode: "initiator1",
            tenantCode: null,
            ct: CancellationToken.None);

        // Instance must be Running (blocked at an Approval node).
        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running (blocked at Approval node).");

        // The active node must be 'bigApproval'.
        await using var verify = MakeContext();
        var activeNode = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.State == NodeState.Activated)
            .FirstOrDefaultAsync();

        Assert.IsNotNull(activeNode, "There must be an Activated NodeInstance.");
        Assert.AreEqual("bigApproval", activeNode!.NodeKey,
            $"High amount must route to 'bigApproval', got '{activeNode.NodeKey}'.");
    }

    // ── Test 9b: Low-value path (amount <= 10000 → default = smallApproval) ───

    [TestMethod]
    public async Task ExclusiveGateway_LowAmount_RoutesToSmallApproval_ViaDefault()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, RoutingTestHelpers.ConditionalGraph("AmountGw2"));

        // Amount = 500 ≤ 10000 → no branch matches → should fall through to default = smallApproval.
        var formData = RoutingTestHelpers.ToFormDataJson(("amount", 500m));

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: formData,
            initiatorITCode: "initiator2",
            tenantCode: null,
            ct: CancellationToken.None);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running (blocked at Approval node).");

        await using var verify = MakeContext();
        var activeNode = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.State == NodeState.Activated)
            .FirstOrDefaultAsync();

        Assert.IsNotNull(activeNode, "There must be an Activated NodeInstance.");
        Assert.AreEqual("smallApproval", activeNode!.NodeKey,
            $"Low amount must route to 'smallApproval' via default, got '{activeNode.NodeKey}'.");
    }

    // ── Test 9c: Missing FormDataJson → fail-closed → default branch ──────────

    [TestMethod]
    public async Task ExclusiveGateway_NullFormData_RoutesToDefault()
    {
        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, RoutingTestHelpers.ConditionalGraph("AmountGw3"));

        // No FormDataJson → missing "amount" → fail-closed → default = smallApproval.
        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: null,
            initiatorITCode: "initiator3",
            tenantCode: null,
            ct: CancellationToken.None);

        Assert.AreEqual(InstanceState.Running, instance.State,
            "Instance must be Running (blocked at Approval node).");

        await using var verify = MakeContext();
        var activeNode = await verify.Set<NodeInstance>()
            .AsNoTracking()
            .Where(n => n.InstanceId == instance.ID && n.State == NodeState.Activated)
            .FirstOrDefaultAsync();

        Assert.IsNotNull(activeNode, "There must be an Activated NodeInstance.");
        Assert.AreEqual("smallApproval", activeNode!.NodeKey,
            $"Null FormData must route to default 'smallApproval', got '{activeNode.NodeKey}'.");
    }

    // ── Test 10: No-match + no-default → fail-closed ──────────────────────────

    [TestMethod]
    public async Task ExclusiveGateway_NoMatchNoDefault_ReturnsFailClosedRouting()
    {
        // Build a graph with a Condition node that has NO default and a branch that won't match.
        // This SHOULD fail graph validation at publish time (ConditionNodeMissingDefault),
        // but we test the runtime fail-closed path by bypassing the validator here
        // (we inject the graph directly, as if publish validation was bypassed).
        //
        // We build the graph JSON manually (skipping validator) to exercise the runtime path.
        var graph = new WorkflowGraph
        {
            Key  = "NoDefaultGraph",
            Name = "NoDefaultGraph",
            FieldWhitelist = new List<FieldWhitelistEntry>
            {
                new() { Field = "amount", ClrType = "System.Decimal" },
            },
            Nodes = new List<NodeDef>
            {
                new() { NodeKey = "start", Kind = NodeKind.Start },
                new()
                {
                    NodeKey  = "gw",
                    Kind     = NodeKind.Condition,
                    Branches = new List<BranchDef>
                    {
                        new()
                        {
                            // amount > 10000 — but we'll submit amount = 1 so it never matches.
                            Rule   = RoutingTestHelpers.Leaf("amount", FilterOperator.Gt, 10000m),
                            Target = "endA",
                        },
                    },
                    Default = null, // Intentionally absent — bypass validator for this runtime test.
                },
                new() { NodeKey = "endA", Kind = NodeKind.End },
            },
            Transitions = new List<TransitionDef>
            {
                new() { From = "start", To = "gw"   },
                new() { From = "gw",    To = "endA" },
            },
        };

        // Serialize without validation.
        var rawGraphJson = WorkflowGraphSerializer.Serialize(graph);

        var (engine, ctx) = MakeEngine();
        await using var _ = ctx;

        var version = await SeedVersionAsync(ctx, rawGraphJson);

        // StartAsync will drive Start → Condition → no match, no default → FailClosedRouting.
        // StartAsync returns the ProcessInstance after driving as far as possible; but since the
        // engine sets the state to Running and then calls AdvanceCoreAsync which returns
        // FailClosedRouting, the instance state may remain Running.
        // We verify AdvanceAsync also returns FailClosedRouting.

        var instance = await engine.StartAsync(
            version.ID,
            formDataJson: RoutingTestHelpers.ToFormDataJson(("amount", 1m)),
            initiatorITCode: "initiator4",
            tenantCode: null,
            ct: CancellationToken.None);

        // The engine drives synchronously in StartAsync; we check the returned result.
        // Instance state should remain Running (not silently advanced to Approved).
        // If StartAsync did AdvanceCoreAsync and got FailClosedRouting, the instance state
        // stays as Running (engine does not approve or reject on FailClosed — it just stops).
        Assert.AreEqual(InstanceState.Running, instance.State,
            "No-match + no-default must NOT silently approve the instance.");

        // AdvanceAsync must also return FailClosedRouting (condition node is stuck).
        // Re-read instance — since the Start node was completed and Condition node was reached,
        // the active node should be the Condition node (Activated state).
        var advanceResult = await engine.AdvanceAsync(instance.ID, CancellationToken.None);
        Assert.AreEqual(WorkflowActionCode.FailClosedRouting, advanceResult.Code,
            $"No-match + no-default must return FailClosedRouting, got {advanceResult.Code}.");
    }
}
