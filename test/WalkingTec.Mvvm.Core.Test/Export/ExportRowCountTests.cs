#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Export;

/// <summary>
/// Guards the ExportRowCount property on IBasePagedListVM / BasePagedListVM (#613).
/// </summary>
[TestClass]
public class ExportRowCountTests
{
    private static IList<School> _data = new List<School>();

    private class SchoolListVM : BasePagedListVM<School, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<School>> InitGridHeader()
        {
            return new List<GridColumn<School>>
            {
                this.MakeGridHeader(x => x.SchoolCode),
                this.MakeGridHeader(x => x.SchoolName),
            };
        }

        public override IOrderedQueryable<School> GetSearchQuery()
            => _data.AsQueryable().OrderBy(x => x.SchoolCode);
    }

    private SchoolListVM CreateVm()
    {
        var vm = MockWtmContext.CreateWtmContext().CreateVM<SchoolListVM>();
        vm.NeedPage = false;
        return vm;
    }

    [TestInitialize]
    public void Init() => _data = new List<School>();

    [TestMethod]
    public void ExportRowCount_is_zero_before_export()
    {
        var vm = CreateVm();
        Assert.AreEqual(0, vm.ExportRowCount);
    }

    [TestMethod]
    public void ExportRowCount_is_zero_when_no_data()
    {
        var vm = CreateVm();
        vm.GenerateExcel();
        Assert.AreEqual(0, vm.ExportRowCount);
    }

    [TestMethod]
    public void ExportRowCount_matches_actual_rows()
    {
        _data = new List<School>
        {
            new School { SchoolCode = "001", SchoolName = "Alpha" },
            new School { SchoolCode = "002", SchoolName = "Beta" },
            new School { SchoolCode = "003", SchoolName = "Gamma" },
        };
        var vm = CreateVm();
        vm.GenerateExcel();
        Assert.AreEqual(3, vm.ExportRowCount);
    }

    [TestMethod]
    public void ExportRowCount_on_interface_is_readonly()
    {
        var prop = typeof(IBasePagedListVM<,>).GetProperty("ExportRowCount");
        Assert.IsNotNull(prop, "ExportRowCount must exist on IBasePagedListVM");
        Assert.IsTrue(prop!.CanRead);
        Assert.IsFalse(prop.CanWrite, "IBasePagedListVM.ExportRowCount must be read-only");
    }
}
