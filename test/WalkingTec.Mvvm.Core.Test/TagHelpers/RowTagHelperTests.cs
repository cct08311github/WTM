using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading.Tasks;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

[TestClass]
public class RowTagHelperTests
{
    private static TagHelperContext MakeContext(Dictionary<object, object> items = null)
        => new("wt:row", new TagHelperAttributeList(),
               items ?? new Dictionary<object, object>(), "row-test");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestMethod]
    public void Process_SetsIprFromItemsPerRow()
    {
        var context = MakeContext();
        var output = MakeOutput();
        new RowTagHelper { ItemsPerRow = ItemsPerRowEnum.Four }.Process(context, output);
        Assert.AreEqual((int?)4, context.Items["ipr"]);
    }

    [TestMethod]
    public void Process_SetsIprXsWhenItemsPerRowXsSet()
    {
        var context = MakeContext();
        var output = MakeOutput();
        new RowTagHelper { ItemsPerRowXs = ItemsPerRowEnum.Two }.Process(context, output);
        Assert.AreEqual((int?)2, context.Items["ipr_xs"]);
    }

    [TestMethod]
    public void Process_SetsIprSmWhenItemsPerRowSmSet()
    {
        var context = MakeContext();
        var output = MakeOutput();
        new RowTagHelper { ItemsPerRowSm = ItemsPerRowEnum.Three }.Process(context, output);
        Assert.AreEqual((int?)3, context.Items["ipr_sm"]);
    }

    [TestMethod]
    public void Process_NoXs_IprXsNotInContext()
    {
        var context = MakeContext();
        var output = MakeOutput();
        new RowTagHelper { ItemsPerRow = ItemsPerRowEnum.Four }.Process(context, output);
        Assert.IsFalse(context.Items.ContainsKey("ipr_xs"));
    }

    [TestMethod]
    public void Process_NoSm_IprSmNotInContext()
    {
        var context = MakeContext();
        var output = MakeOutput();
        new RowTagHelper { ItemsPerRow = ItemsPerRowEnum.Four }.Process(context, output);
        Assert.IsFalse(context.Items.ContainsKey("ipr_sm"));
    }

    [TestMethod]
    public void Process_AllThreeBreakpoints_AllContextItemsSet()
    {
        var context = MakeContext();
        var output = MakeOutput();
        new RowTagHelper
        {
            ItemsPerRow = ItemsPerRowEnum.Four,
            ItemsPerRowSm = ItemsPerRowEnum.Two,
            ItemsPerRowXs = ItemsPerRowEnum.One,
        }.Process(context, output);
        Assert.AreEqual((int?)4, context.Items["ipr"]);
        Assert.AreEqual((int?)2, context.Items["ipr_sm"]);
        Assert.AreEqual((int?)1, context.Items["ipr_xs"]);
    }

    [TestMethod]
    public void Process_NullItemsPerRow_IprNotSetInContext()
    {
        var context = MakeContext();
        var output = MakeOutput();
        // No ItemsPerRow set — ipr should either not be in context or be null
        new RowTagHelper().Process(context, output);
        // Either key absent or value is null
        if (context.Items.ContainsKey("ipr"))
        {
            Assert.IsNull(context.Items["ipr"]);
        }
    }
}
