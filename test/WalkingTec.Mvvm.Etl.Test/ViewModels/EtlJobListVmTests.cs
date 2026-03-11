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
public class EtlJobListVmTests
{
    private WTMContext _wtm = null!;

    [TestInitialize]
    public void Setup()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _wtm = MockWtmContext.CreateWtmContext(dc);

        // Seed data
        dc.Set<EtlJobDefinition>().AddRange(
            new EtlJobDefinition
            {
                Name = "OrderSync",
                CronExpression = "0 0 * * *",
                SourceCsKey = "upstream",
                SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.Timestamp,
                Status = EtlJobStatus.Enabled,
                CreateTime = DateTime.UtcNow.AddMinutes(-3)
            },
            new EtlJobDefinition
            {
                Name = "ProductSync",
                CronExpression = "0 */6 * * *",
                SourceCsKey = "upstream",
                SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.Identity,
                Status = EtlJobStatus.Disabled,
                CreateTime = DateTime.UtcNow.AddMinutes(-2)
            },
            new EtlJobDefinition
            {
                Name = "InventorySync",
                CronExpression = "0 0 * * *",
                SourceCsKey = "warehouse",
                SourceDbType = DBTypeEnum.SqlServer,
                WatermarkType = EtlWatermarkType.FullLoad,
                Status = EtlJobStatus.Enabled,
                CreateTime = DateTime.UtcNow.AddMinutes(-1)
            }
        );
        dc.SaveChanges();
    }

    private EtlJobListVM CreateVm()
    {
        var vm = _wtm.CreateVM<EtlJobListVM>();
        return vm;
    }

    [TestMethod]
    public void Search_by_name_filters_correctly()
    {
        var vm = CreateVm();
        vm.Searcher.Name = "Order";
        vm.DoSearch();

        Assert.AreEqual(1, vm.EntityList.Count);
        Assert.AreEqual("OrderSync", vm.EntityList[0].Name);
    }

    [TestMethod]
    public void Search_by_status_filters_correctly()
    {
        var vm = CreateVm();
        vm.Searcher.Status = EtlJobStatus.Enabled;
        vm.DoSearch();

        Assert.AreEqual(2, vm.EntityList.Count);
        Assert.IsTrue(vm.EntityList.All(x => x.Status == EtlJobStatus.Enabled));
    }

    [TestMethod]
    public void List_orders_by_create_time_desc()
    {
        var vm = CreateVm();
        vm.DoSearch();

        Assert.AreEqual(3, vm.EntityList.Count);
        Assert.AreEqual("InventorySync", vm.EntityList[0].Name);
        Assert.AreEqual("ProductSync", vm.EntityList[1].Name);
        Assert.AreEqual("OrderSync", vm.EntityList[2].Name);
    }

    [TestMethod]
    public void Search_by_source_db_type_filters_correctly()
    {
        var vm = CreateVm();
        vm.Searcher.SourceDbType = DBTypeEnum.Oracle;
        vm.DoSearch();

        Assert.AreEqual(0, vm.EntityList.Count);
    }

    [TestMethod]
    public void Search_no_filter_returns_all()
    {
        var vm = CreateVm();
        vm.DoSearch();

        Assert.AreEqual(3, vm.EntityList.Count);
    }
}
