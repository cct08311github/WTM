#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.ViewModels;

[TestClass]
public class EtlJobDefinitionVMTests
{
    private WTMContext _wtm = null!;

    [TestInitialize]
    public void Setup()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _wtm = MockWtmContext.CreateWtmContext(dc);
    }

    private bool HasError(IModelStateService msd, string key)
        => msd.Keys.Any(k => k == key);

    private EtlJobDefinitionVM CreateVm()
    {
        var vm = _wtm.CreateVM<EtlJobDefinitionVM>();
        vm.Entity.Name = "TestJob";
        vm.Entity.CronExpression = "0 0 * * * ?"; // valid 6-field Quartz cron
        vm.Entity.SourceCsKey = "src";
        vm.Entity.SourceDbType = DBTypeEnum.SqlServer;
        vm.Entity.QueryTemplate = "SELECT * FROM Orders";
        vm.Entity.TargetTableName = "Orders";
        return vm;
    }

    // ─── MergeKeyColumn 驗證：缺少條件時應略過 ─────────────────────────────

    [TestMethod]
    public void Validate_skips_merge_key_check_when_MergeKeyColumn_empty()
    {
        var vm = CreateVm();
        vm.Entity.MergeKeyColumn = "";
        vm.Entity.TargetTableName = "Orders";
        vm.Entity.TargetCsKey = "target";

        vm.Validate();

        Assert.IsFalse(vm.MSD.IsValid == false && HasError(vm.MSD, "Entity.TargetDbType"),
            "MergeKeyColumn 為空時不應有 TargetDbType 錯誤");
    }

    [TestMethod]
    public void Validate_skips_merge_key_check_when_TargetCsKey_empty()
    {
        var vm = CreateVm();
        vm.Entity.MergeKeyColumn = "OrderId";
        vm.Entity.TargetTableName = "Orders";
        vm.Entity.TargetCsKey = "";

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.TargetDbType"),
            "TargetCsKey 為空時不應有 TargetDbType 錯誤");
    }

    // ─── NotSupportedException → 應加入 ModelError ──────────────────────────

    [TestMethod]
    public void Validate_adds_model_error_when_TargetDbType_unsupported()
    {
        // Arrange: 注入一個有效連線設定，但使用不支援的 DBType
        _wtm.ConfigInfo.Connections.Add(new CS { Key = "target_db", Value = "Server=fake;Database=test;" });

        var vm = CreateVm();
        vm.Entity.MergeKeyColumn = "OrderId";
        vm.Entity.TargetTableName = "Orders";
        vm.Entity.TargetCsKey = "target_db";
        vm.Entity.TargetDbType = DBTypeEnum.Memory; // EtlSourceFactory.CreateLoader 不支援 Memory → NotSupportedException

        // Act
        vm.Validate();

        // Assert: NotSupportedException 應被轉為 ModelError，而非靜默吞噬
        Assert.IsTrue(HasError(vm.MSD, "Entity.TargetDbType"),
            "不支援的 TargetDbType 應觸發 NotSupportedException 並加入 ModelError");
    }

    // ─── 連線金鑰不存在 → targetCs 為 null → 靜默略過 ────────────────────────

    [TestMethod]
    public void Validate_skips_merge_key_check_when_connection_key_not_found()
    {
        // TargetCsKey 在 ConfigInfo.Connections 中找不到 → targetCs = null → 不執行 loader 建立
        var vm = CreateVm();
        vm.Entity.MergeKeyColumn = "OrderId";
        vm.Entity.TargetTableName = "Orders";
        vm.Entity.TargetCsKey = "nonexistent_key";
        vm.Entity.TargetDbType = DBTypeEnum.SqlServer;

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.TargetDbType"),
            "找不到連線設定時應靜默略過，不產生 ModelError");
        Assert.IsFalse(HasError(vm.MSD, "Entity.MergeKeyColumn"),
            "找不到連線設定時不應有 MergeKeyColumn 錯誤");
    }

    // ─── Cron 表達式驗證 ─────────────────────────────────────────────────────

    [TestMethod]
    public void Validate_auto_converts_5field_unix_cron_to_6field_quartz()
    {
        var vm = CreateVm();
        vm.Entity.CronExpression = "0 0 * * *"; // 5-field Unix

        vm.Validate();

        Assert.AreEqual("0 0 0 * * *", vm.Entity.CronExpression,
            "5-field Unix cron 應自動補秒欄位轉為 6-field Quartz 格式");
    }

    [TestMethod]
    public void Validate_adds_model_error_for_invalid_cron()
    {
        var vm = CreateVm();
        vm.Entity.CronExpression = "not-a-cron";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.CronExpression"),
            "無效的 Cron 表達式應加入 ModelError");
    }
}
