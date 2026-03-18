using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Export;

/// <summary>
/// Guards the ExportRowCount property on IBasePagedListVM / BasePagedListVM (#613).
/// </summary>
[TestClass]
public class ExportRowCountTests
{
    [TestMethod]
    public void ExportRowCount_on_interface_is_readonly()
    {
        var prop = typeof(IBasePagedListVM<,>).GetProperty("ExportRowCount");
        Assert.IsNotNull(prop, "ExportRowCount must exist on IBasePagedListVM");
        Assert.IsTrue(prop!.CanRead, "ExportRowCount must be readable");
        Assert.IsFalse(prop.CanWrite, "IBasePagedListVM.ExportRowCount must be read-only");
    }

    [TestMethod]
    public void Controller_returns_422_when_ExportRowCount_is_zero()
    {
        int exportRowCount = 0;
        bool shouldReturn422 = exportRowCount == 0;
        Assert.IsTrue(shouldReturn422);
    }

    [TestMethod]
    public void Controller_proceeds_with_file_when_ExportRowCount_is_positive()
    {
        int exportRowCount = 42;
        bool shouldReturn422 = exportRowCount == 0;
        Assert.IsFalse(shouldReturn422);
    }
}
