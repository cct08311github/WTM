#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.ViewModels;

/// <summary>
/// Validation refinements added in 10.5+ for the new ETL fields:
/// LoadMode coherence with MergeKeyColumn / ReplaceWhereClause,
/// SQL-injection guard on the WHERE clause, ColumnMappingJson
/// shape and value validation.
/// </summary>
[TestClass]
public class EtlJobDefinitionVMValidationTests
{
    private WTMContext _wtm = null!;

    [TestInitialize]
    public void Setup()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _wtm = MockWtmContext.CreateWtmContext(dc);
    }

    private static bool HasError(IModelStateService msd, string key)
        => msd != null && msd.Keys.Any(k => k == key);

    private EtlJobDefinitionVM BaseVm()
    {
        var vm = _wtm.CreateVM<EtlJobDefinitionVM>();
        vm.Entity.Name = "TestJob";
        vm.Entity.CronExpression = "0 0 * * * ?";
        vm.Entity.SourceCsKey = "src";
        vm.Entity.SourceDbType = DBTypeEnum.SqlServer;
        vm.Entity.QueryTemplate = "SELECT * FROM Orders";
        vm.Entity.TargetTableName = "Orders";
        // Intentionally NOT setting TargetCsKey — keeps the merge-key
        // uniqueness probe out of the way; we focus on the new rules.
        return vm;
    }

    // ── LoadMode coherence ─────────────────────────────────────────────

    [TestMethod]
    public void Merge_mode_with_empty_MergeKey_adds_model_error()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Merge;
        vm.Entity.MergeKeyColumn = "";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.MergeKeyColumn"),
            "Merge mode requires a MergeKeyColumn — saving without it must surface as a form error.");
    }

    [TestMethod]
    public void Replace_mode_with_empty_MergeKey_validates_silently()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Replace;
        vm.Entity.MergeKeyColumn = ""; // unused in Replace mode

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.MergeKeyColumn"),
            "Replace mode does not use MergeKeyColumn; empty must be silent.");
    }

    // ── ReplaceWhereClause SQL-injection guard ────────────────────────

    [TestMethod]
    public void Replace_mode_with_unsafe_where_clause_adds_model_error()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Replace;
        vm.Entity.ReplaceWhereClause = "1=1; DROP TABLE Users";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.ReplaceWhereClause"),
            "Unsafe WHERE clause must be caught at save, not at first run.");
    }

    [TestMethod]
    public void Replace_mode_with_safe_where_clause_validates_silently()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Replace;
        vm.Entity.ReplaceWhereClause = "OrderDate >= '2026-01-01' AND Status = 'Active'";

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.ReplaceWhereClause"));
    }

    [TestMethod]
    public void Replace_mode_with_null_where_clause_validates_silently()
    {
        // null = "delete entire target on every run". Operator's choice;
        // not a security issue, validator must accept.
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Replace;
        vm.Entity.ReplaceWhereClause = null;

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.ReplaceWhereClause"));
    }

    [TestMethod]
    public void Merge_mode_does_not_run_where_safety_check()
    {
        // Even if WHERE is unsafe, Merge mode shouldn't be flagged for it
        // (the field is unused in Merge mode anyway).
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Merge;
        vm.Entity.MergeKeyColumn = "OrderID";
        vm.Entity.ReplaceWhereClause = "1=1; DROP TABLE Users"; // would be unsafe in Replace

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.ReplaceWhereClause"),
            "WHERE-clause safety check is scoped to Replace mode only.");
    }

    // ── ColumnMappingJson validation ───────────────────────────────────

    [TestMethod]
    public void ColumnMappingJson_null_or_empty_validates_silently()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Merge;
        vm.Entity.MergeKeyColumn = "OrderID";
        vm.Entity.ColumnMappingJson = null;

        vm.Validate();
        Assert.IsFalse(HasError(vm.MSD, "Entity.ColumnMappingJson"));

        vm.Entity.ColumnMappingJson = "";
        vm.Validate();
        Assert.IsFalse(HasError(vm.MSD, "Entity.ColumnMappingJson"));
    }

    [TestMethod]
    public void ColumnMappingJson_valid_dict_validates_silently()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Merge;
        vm.Entity.MergeKeyColumn = "OrderID";
        vm.Entity.ColumnMappingJson = """{ "cust_id": "CustomerID", "order_no": "OrderNumber" }""";

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.ColumnMappingJson"));
    }

    [TestMethod]
    public void ColumnMappingJson_malformed_adds_model_error()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Merge;
        vm.Entity.MergeKeyColumn = "OrderID";
        vm.Entity.ColumnMappingJson = "this is not json";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.ColumnMappingJson"),
            "Malformed JSON must be caught at save time, not at first run.");
    }

    [TestMethod]
    public void ColumnMappingJson_with_blank_value_adds_model_error()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Merge;
        vm.Entity.MergeKeyColumn = "OrderID";
        vm.Entity.ColumnMappingJson = """{ "cust_id": "" }""";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.ColumnMappingJson"));
    }

    [TestMethod]
    public void ColumnMappingJson_with_blank_key_adds_model_error()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Merge;
        vm.Entity.MergeKeyColumn = "OrderID";
        vm.Entity.ColumnMappingJson = """{ " ": "Tgt" }""";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.ColumnMappingJson"));
    }

    // ── Existing checks still work in combination ──────────────────────

    [TestMethod]
    public void Combined_replace_mode_with_safe_where_and_valid_mapping_clean()
    {
        var vm = BaseVm();
        vm.Entity.LoadMode = EtlLoadMode.Replace;
        vm.Entity.MergeKeyColumn = ""; // unused
        vm.Entity.ReplaceWhereClause = "OrderDate >= '2026-01-01'";
        vm.Entity.ColumnMappingJson = """{ "cust_id": "CustomerID" }""";

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.MergeKeyColumn"));
        Assert.IsFalse(HasError(vm.MSD, "Entity.ReplaceWhereClause"));
        Assert.IsFalse(HasError(vm.MSD, "Entity.ColumnMappingJson"));
    }
}
