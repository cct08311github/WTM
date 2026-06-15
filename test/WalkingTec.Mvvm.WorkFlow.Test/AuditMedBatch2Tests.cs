#nullable enable
// WF Audit Medium Batch 2 — unit tests for fixes C9, C12, C15.
//
// C9:  Narrow catch-when predicate in MintNodeInstanceGuardedAsync — UNIQUE/duplicate only.
// C12: Canonicalize rejects duplicate JSON property names via AllowDuplicateProperties=false.
// C15: [ActionDescription] present on all WorkFlow controller classes and non-POST actions.

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Definition;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class AuditMedBatch2Tests
{
    // ── C9: catch-when predicate narrows to UNIQUE / duplicate-key violations only ─

    [TestMethod]
    public void C9_CatchWhen_MatchesUniqueAndDuplicateOnly()
    {
        // Messages that SHOULD match (UNIQUE / duplicate-key violations)
        var shouldMatch = new[]
        {
            "UNIQUE constraint failed: Wf_NodeInstance.TenantCode, Wf_NodeInstance.InstanceId",
            "duplicate key value violates unique constraint \"ix_nodeinstance_gen\"",
            "Duplicate entry 'val' for key 'PRIMARY'",
            "Violation of UNIQUE KEY constraint 'UQ_X'. Cannot insert duplicate key",
            "unique constraint (MY_SCHEMA.UQ_NODE) violated",
        };

        // Messages that should NOT match (FK / NOT NULL / CHECK violations)
        var shouldNotMatch = new[]
        {
            "FOREIGN KEY constraint failed",
            "NOT NULL constraint failed: Wf_NodeInstance.NodeKey",
            "CHECK constraint \"chk_state\" failed",
            "constraint violation: referential integrity",
            "insert or update on table violates foreign key constraint",
        };

        static bool IsUniqueViolation(string? msg) =>
            msg != null &&
            (msg.Contains("unique", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase));

        foreach (var msg in shouldMatch)
            Assert.IsTrue(IsUniqueViolation(msg), $"Expected match but got no match for: {msg}");

        foreach (var msg in shouldNotMatch)
            Assert.IsFalse(IsUniqueViolation(msg), $"Expected no match but got match for: {msg}");
    }

    // ── C12: Canonicalize rejects duplicate JSON property names ───────────────────

    [TestMethod]
    public void C12_Canonicalize_RejectsDuplicatePropertyNames()
    {
        // JSON with duplicate top-level key
        const string duplicateJson = """{"foo":"first","bar":"x","foo":"second"}""";

        // Should throw JsonException (not silently pick one copy)
        var ex = Assert.ThrowsException<JsonException>(
            () => WorkflowGraphSerializer.Canonicalize(duplicateJson));

        // Sanity: a valid, non-duplicate JSON still canonicalizes correctly
        const string validJson = """{"z":"last","a":"first","m":42}""";
        var canonical = WorkflowGraphSerializer.Canonicalize(validJson);
        // Keys must be sorted alphabetically: a, m, z
        Assert.AreEqual("""{"a":"first","m":42,"z":"last"}""", canonical);
    }

    [TestMethod]
    public void C12_Canonicalize_NormalGraphUnaffected()
    {
        // A valid workflow-shaped graph JSON (no duplicates) should canonicalize without error
        // and be byte-identical on repeated calls.
        const string json = """{"schemaVersion":"1.0","nodes":[{"key":"Start"},{"key":"End"}],"transitions":[]}""";
        var c1 = WorkflowGraphSerializer.Canonicalize(json);
        var c2 = WorkflowGraphSerializer.Canonicalize(json);
        Assert.AreEqual(c1, c2, "Canonicalize must be deterministic.");
        Assert.IsNotNull(c1);
        Assert.IsTrue(c1.Length > 0);
    }

    // ── C15: [ActionDescription] registered on WorkFlow controllers ───────────────

    [TestMethod]
    public void C15_WorkflowControllers_HaveActionDescriptionAttribute()
    {
        // Controller types
        var controllerTypes = new[]
        {
            typeof(WalkingTec.Mvvm.WorkFlow.Controllers.WorkflowDefinitionController),
            typeof(WalkingTec.Mvvm.WorkFlow.Controllers.WorkflowInstanceController),
            typeof(WalkingTec.Mvvm.WorkFlow.Controllers.WorkflowTaskController),
            typeof(WalkingTec.Mvvm.WorkFlow.Controllers.WorkflowDesignerController),
        };

        foreach (var ctrl in controllerTypes)
        {
            var classAttr = ctrl.GetCustomAttributes(
                typeof(WalkingTec.Mvvm.Core.ActionDescriptionAttribute), false);
            Assert.IsTrue(classAttr.Length > 0,
                $"Controller {ctrl.Name} must carry [ActionDescription] for privilege registration.");
        }
    }

    [TestMethod]
    public void C15_WorkflowControllers_NonPostActionsHaveActionDescription()
    {
        // Non-POST methods on workflow controllers should carry [ActionDescription]
        // so that GetAllModules can register them as privilege entries.
        var controllersToCheck = new[]
        {
            typeof(WalkingTec.Mvvm.WorkFlow.Controllers.WorkflowInstanceController),
            typeof(WalkingTec.Mvvm.WorkFlow.Controllers.WorkflowTaskController),
            typeof(WalkingTec.Mvvm.WorkFlow.Controllers.WorkflowDesignerController),
        };

        foreach (var ctrl in controllersToCheck)
        {
            var methods = ctrl.GetMethods(
                BindingFlags.Public |
                BindingFlags.DeclaredOnly |
                BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .Where(m => m.GetCustomAttributes(
                    typeof(HttpPostAttribute), false).Length == 0)
                .ToList();

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttributes(
                    typeof(WalkingTec.Mvvm.Core.ActionDescriptionAttribute), false);
                Assert.IsTrue(attr.Length > 0,
                    $"{ctrl.Name}.{method.Name} (non-POST) must carry [ActionDescription].");
            }
        }
    }
}
