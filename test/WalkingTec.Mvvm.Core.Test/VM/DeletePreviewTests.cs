#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM;

/// <summary>
/// Guards issue #619 — GetDeletePreviewString() on BaseCRUDVM.
/// </summary>
[TestClass]
public class DeletePreviewTests
{
    private BaseCRUDVM<School> CreateVm()
    {
        var vm = new BaseCRUDVM<School>();
        vm.Wtm = MockWtmContext.CreateWtmContext();
        return vm;
    }

    [TestMethod]
    public void GetDeletePreviewString_returns_SchoolName_when_set()
    {
        var vm = CreateVm();
        vm.Entity = new School
        {
            ID = Guid.NewGuid(),
            SchoolCode = "001",
            SchoolName = "TestSchool",
            SchoolType = SchoolTypeEnum.PUB
        };

        var label = vm.GetDeletePreviewString();

        // SchoolName matches the "SchoolName" candidate in the search list
        Assert.AreEqual("TestSchool", label);
    }

    [TestMethod]
    public void GetDeletePreviewString_falls_back_to_primary_key_when_no_name_props()
    {
        // Use a minimal entity type without Name/Title/Code etc.
        var vm = new BaseCRUDVM<TopBasePoco>();
        vm.Wtm = MockWtmContext.CreateWtmContext();
        var id = Guid.NewGuid();
        vm.Entity = new TopBasePoco { ID = id };

        var label = vm.GetDeletePreviewString();

        Assert.AreEqual(id.ToString(), label);
    }

    [TestMethod]
    public void GetDeletePreviewString_is_on_interface()
    {
        var method = typeof(IBaseCRUDVM<>).GetMethod("GetDeletePreviewString");
        Assert.IsNotNull(method, "GetDeletePreviewString must exist on IBaseCRUDVM<>");
    }

    [TestMethod]
    public void GetDeletePreviewString_can_be_overridden()
    {
        // Verify the method is virtual so downstream code can customise it.
        var method = typeof(BaseCRUDVM<School>).GetMethod("GetDeletePreviewString");
        Assert.IsNotNull(method);
        Assert.IsTrue(method!.IsVirtual, "GetDeletePreviewString must be virtual");
    }
}
