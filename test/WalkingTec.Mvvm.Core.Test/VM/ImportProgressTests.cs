#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM;

/// <summary>Synchronous IProgress&lt;T&gt; — avoids thread-pool ordering issues in tests.</summary>
file sealed class SyncProgress<T>(List<T> target) : IProgress<T>
{
    public void Report(T value) => target.Add(value);
}

/// <summary>
/// Guards issue #607 — IProgress&lt;ImportProgress&gt; reporting in BatchSaveData,
/// and issue #615 — InlineErrors / InlineErrorLimit on BaseImportVM.
/// </summary>
[TestClass]
public class ImportProgressTests
{
    private string _seed = null!;

    [TestInitialize]
    public void Init() => _seed = Guid.NewGuid().ToString();

    private IDataContext CreateDb() => new ImportTestDataContext(_seed, DBTypeEnum.Memory);

    private TestImportVM MakeVm(List<ImportTestItem> entities)
    {
        var vm = new TestImportVM(entities);
        vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "testuser");
        return vm;
    }

    // ── ImportProgress reporting (#607) ─────────────────────────────────────

    [TestMethod]
    public void BatchSaveData_reports_progress_for_each_row()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "A", Value = 1 },
            new ImportTestItem { Name = "B", Value = 2 },
            new ImportTestItem { Name = "C", Value = 3 }
        };
        var vm = MakeVm(entities);
        var reports = new List<ImportProgress>();
        var progress = new SyncProgress<ImportProgress>(reports);

        vm.BatchSaveData(progress);

        // Progress should be reported at least once per entity (validate + save = 2 phases)
        Assert.IsTrue(reports.Count >= 3, $"Expected >= 3 progress reports, got {reports.Count}");
    }

    [TestMethod]
    public void BatchSaveData_progress_total_matches_entity_count()
    {
        var entities = Enumerable.Range(1, 5)
            .Select(i => new ImportTestItem { Name = $"Item{i}", Value = i })
            .ToList();
        var vm = MakeVm(entities);
        var reports = new List<ImportProgress>();

        vm.BatchSaveData(new SyncProgress<ImportProgress>(reports));

        Assert.IsTrue(reports.All(p => p.Total == 5),
            "All progress reports should carry Total = entity count");
    }

    [TestMethod]
    public void BatchSaveData_progress_processed_is_monotonically_increasing()
    {
        var entities = Enumerable.Range(1, 4)
            .Select(i => new ImportTestItem { Name = $"X{i}", Value = i })
            .ToList();
        var vm = MakeVm(entities);
        var reports = new List<ImportProgress>();

        vm.BatchSaveData(new SyncProgress<ImportProgress>(reports));

        // Filter to a single phase to check monotonicity
        var saveReports = reports.Where(p => p.Phase == "Saving").ToList();
        for (int i = 1; i < saveReports.Count; i++)
            Assert.IsTrue(saveReports[i].Processed >= saveReports[i - 1].Processed,
                "Processed should not decrease within the same phase");
    }

    [TestMethod]
    public void BatchSaveData_null_progress_does_not_throw()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "NoProgress", Value = 7 }
        };
        var vm = MakeVm(entities);

        // Calling without progress parameter (null) must not throw
        Assert.IsTrue(vm.BatchSaveData(null));
    }

    [TestMethod]
    public void ImportProgress_phase_names_are_non_empty()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "PhaseCheck", Value = 1 }
        };
        var vm = MakeVm(entities);
        var reports = new List<ImportProgress>();

        vm.BatchSaveData(new SyncProgress<ImportProgress>(reports));

        Assert.IsTrue(reports.All(p => !string.IsNullOrEmpty(p.Phase)),
            "All progress reports must have a non-empty Phase");
    }

    // ── InlineErrors (#615) ──────────────────────────────────────────────────

    [TestMethod]
    public void InlineErrors_is_empty_before_import()
    {
        var vm = MakeVm(new List<ImportTestItem>());
        Assert.AreEqual(0, vm.InlineErrors.Count);
    }

    [TestMethod]
    public void InlineErrors_returns_errors_after_validation_failure()
    {
        // Name exceeds StringLength(50) → validation error
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = new string('Z', 60), Value = 1 },
            new ImportTestItem { Name = new string('Z', 60), Value = 2 }
        };
        var vm = MakeVm(entities);
        vm.BatchSaveData();

        Assert.IsTrue(vm.InlineErrors.Count > 0, "InlineErrors should contain validation failures");
    }

    [TestMethod]
    public void InlineErrors_respects_InlineErrorLimit()
    {
        // Create 100 invalid rows; InlineErrorLimit default is 50
        var entities = Enumerable.Range(1, 100)
            .Select(i => new ImportTestItem { Name = new string('E', 60), Value = i })
            .ToList();
        var vm = MakeVm(entities);
        vm.BatchSaveData();

        Assert.IsTrue(vm.InlineErrors.Count <= vm.InlineErrorLimit,
            $"InlineErrors ({vm.InlineErrors.Count}) must not exceed InlineErrorLimit ({vm.InlineErrorLimit})");
    }

    [TestMethod]
    public void InlineErrors_is_empty_when_InlineErrorLimit_is_zero()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = new string('Q', 60), Value = 1 }
        };
        var vm = MakeVm(entities);
        vm.InlineErrorLimit = 0;
        vm.BatchSaveData();

        Assert.AreEqual(0, vm.InlineErrors.Count,
            "InlineErrorLimit = 0 means no inline errors are surfaced");
    }

    [TestMethod]
    public void InlineErrorLimit_default_is_50()
    {
        var vm = MakeVm(new List<ImportTestItem>());
        Assert.AreEqual(50, vm.InlineErrorLimit);
    }
}
