using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using WalkingTec.Mvvm.TagHelpers.LayUI.Chart;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Smoke tests for the layout / container tag helpers: wt:tab*, wt:card, wt:panel, wt:row, wt:container,
    /// wt:treecontainer, wt:chart (+ the small content helpers).
    /// </summary>
    [TestClass]
    public class LayoutTagHelperTests : LayUITagHelperTestBase
    {
        [TestMethod]
        public void Tab_RendersLayuiTabWithSelectionScript()
        {
            var html = Render(new TabTagHelper { SelectedIndex = 1, AllowClose = true }, "wt:tab", childContent: "<i>tabs</i>");

            AssertContains(html, "<div", "class=\"layui-tab layui-tab-card\"", "lay-allowclose=\"true\"", "<i>tabs</i>",
                ".eq(1).addClass('layui-this')", ".layui-tab-item", "layui.element.on('tab(");
        }

        [TestMethod]
        public void Tab_SimpleStyle_UsesBriefClass()
        {
            AssertContains(Render(new TabTagHelper { TabStyle = TabStyleEnum.Simple }, "wt:tab"), "layui-tab layui-tab-brief");
        }

        [TestMethod]
        public void TabParts_RenderLayuiTabStructure()
        {
            var headers = Render(new TabHeadersTagHelper(), "wt:tabheaders", childContent: "<li/>");
            var header = Render(new TabHeaderTagHelper { Title = "First" }, "wt:tabheader");
            var contents = Render(new TabContentsTagHelper(), "wt:tabcontents", childContent: "x");
            var content = Render(new TabContentTagHelper(), "wt:tabcontent", childContent: "body");

            AssertContains(headers, "<ul", "class=\"layui-tab-title\"");
            AssertContains(header, "<li", ">First</li>");
            AssertContains(contents, "class=\"layui-tab-content\"");
            AssertContains(content, "class=\"layui-tab-item\"", "body");
        }

        [TestMethod]
        public void Card_White_RendersLayuiCardWithHeader()
        {
            var html = Render(new CardTagHelper { Title = "Info", CardTheme = CardThemeEnum.White }, "wt:card", childContent: "card body");

            AssertContains(html, "class=\"layui-card\"", "layui-card-header", ">Info</div>", "layui-card-body", "card body");
        }

        [TestMethod]
        public void Card_Dark_RendersBorderedCard()
        {
            var html = Render(new CardTagHelper { Title = "Dark", CardTheme = CardThemeEnum.Dark, Align = AlignEnum.Center }, "wt:card", childContent: "x");

            AssertContains(html, "layui-card", "background-color:#fafafa", "text-align:center", "border: solid 1px #eee;");
        }

        [TestMethod]
        public void Panel_Collapse_RendersCollapseItem()
        {
            var html = Render(new PanelTagHelper { Title = "Panel title", PanelType = PanelType.Collapse }, "wt:panel", childContent: "panel body");

            AssertContains(html, "layui-collapse", "lay-accordion", "layui-colla-item", "layui-colla-title", "Panel title",
                "layui-colla-content layui-show", "panel body", "element.on('collapse(");
        }

        [TestMethod]
        public void Panel_CollapsedFlag_DropsShowClass()
        {
            var html = Render(new PanelTagHelper { PanelType = PanelType.Collapse, Collapsed = true }, "wt:panel", childContent: "b");

            AssertNotContains(html, "layui-show");
        }

        [TestMethod]
        public void Panel_Card_RendersCardHeaderAndBody()
        {
            var html = Render(new PanelTagHelper { Title = "T", PanelType = PanelType.Card }, "wt:panel", childContent: "b");

            AssertContains(html, "class=\"layui-card\"", "layui-card-header", "layui-card-body");
        }

        [TestMethod]
        public void Row_RendersLayuiRowAndPublishesItemsPerRow()
        {
            var items = new Dictionary<object, object>();

            var html = Render(new RowTagHelper { ItemsPerRow = ItemsPerRowEnum.Three, Space = 10, Id = "row1" }, "wt:row", items, "cells");

            AssertContains(html, "class=\"layui-row layui-col-space10\"", "id=\"row1\"", "cells");
            Assert.AreEqual(3, items["ipr"]);
        }

        [TestMethod]
        public void Row_ChildrenAreWrappedInLayuiGridColumns()
        {
            var vm = new LayuiTestVM();
            var items = new Dictionary<object, object> { ["ipr"] = (int?)3 };

            var html = Render(new ContainerTagHelper { Id = "c1" }, "wt:container", items);
            var field = Render(new TextBoxTagHelper { Field = Field(vm, x => x.Memo) }, "wt:textbox", new Dictionary<object, object> { ["ipr"] = (int?)3 });

            AssertContains(html, "layui-col-md4");
            AssertContains(field, "layui-col-md4");
        }

        [TestMethod]
        public void Container_RendersAlignedDiv()
        {
            var html = Render(new ContainerTagHelper { Id = "box", Align = AlignEnum.Right }, "wt:container", childContent: "inner");

            AssertContains(html, "<div", "id=\"box\"", "lay-filter=\"boxfilter\"", "text-align:right", "inner");
        }

        [TestMethod]
        public void Chart_RendersEchartsContainerAndInitScript()
        {
            var html = Render(new ChartTagHelper { Id = "salesChart", Title = "Sales", Type = ChartTypeEnum.Bar, TriggerUrl = "/Chart/Data" }, "wt:chart");

            AssertContains(html, "<div", "id=\"salesChart\"", "ischart=\"1\"",
                "echarts.init(document.getElementById('salesChart')", "salesChartChartUrl = '/Chart/Data'",
                "title:{text: 'Sales'}", "ff.RefreshChart('salesChart')", "\"type\":\"bar\"");
        }

        [TestMethod]
        public void Chart_Pie_HasNoCartesianAxes()
        {
            var html = Render(new ChartTagHelper { Id = "pie1", Type = ChartTypeEnum.Pie }, "wt:chart");

            AssertContains(html, "\"type\":\"pie\"");
            AssertNotContains(html, "xAxis:");
        }

        [TestMethod]
        public void Chart_Scatter_TooltipEncodesDataValuesBeforeBuildingHtml()
        {
            var html = Render(new ChartTagHelper { Id = "sc1", Type = ChartTypeEnum.Scatter, NameX = "X", NameCategory = "Cat" }, "wt:chart");

            AssertContains(html, "formatter: function (params)",
                "var enc = function (v)", ".replace(/&/g, '&amp;')", ".replace(/</g, '&lt;')", ".replace(/>/g, '&gt;')", ".replace(/\"/g, '&quot;')", ".replace(/'/g, '&#39;')",
                "return enc(params.seriesName) + ' <br/>'",
                "+ xl + enc(params.value[0])", "+ yl + enc(params.value[1])", "+ al + enc(params.value[2])", "+ cl + enc(params.value[3])");
            AssertNotContains(html, "return params.seriesName", "+ params.value[", "echarts.format.encodeHTML");
        }

        [TestMethod]
        public void Chart_NonScatter_KeepsDefaultTooltip()
        {
            AssertContains(Render(new ChartTagHelper { Id = "b1", Type = ChartTypeEnum.Bar }, "wt:chart"), "tooltip: {},");
            AssertContains(Render(new ChartTagHelper { Id = "l1", Type = ChartTypeEnum.Line }, "wt:chart"), "tooltip: {trigger: 'axis'},");
        }

        [TestMethod]
        public void TreeContainer_RendersTreeAndContentPane()
        {
            var vm = new LayuiTestVM
            {
                Menus = new List<TreeSelectListItem>
                {
                    new TreeSelectListItem { Text = "root", Value = "1", Url = "/a", Children = new List<TreeSelectListItem> { new TreeSelectListItem { Text = "leaf", Value = "2", Url = "/b" } } }
                }
            };

            var html = Render(new TreeContainerTagHelper { Id = "tc1", Items = Field(vm, x => x.Menus), Title = "Menu" }, "wt:treecontainer", childContent: "");

            AssertContains(html, "id=\"toptc1\"", "wtm-ctype=\"tc\"", "layui-row donotuse_fill", "layui.use(['tree']", "root");
        }

        [TestMethod]
        public void FieldSet_RendersLegendAndBox()
        {
            var html = Render(new FieldSetTagHelper { Title = "Group" }, "wt:fieldset", childContent: "<p>x</p>");

            AssertContains(html, "<fieldset", "layui-elem-field", "<legend>Group</legend>", "layui-field-box", "<p>x</p>");
        }

        [TestMethod]
        public void Image_RendersImgWithFileUrl()
        {
            var vm = new LayuiTestVM { PhotoId = Guid.Parse("11111111-2222-3333-4444-555555555555") };
            var helper = new ImageTagHelper { Field = Field(vm, x => x.PhotoId) };

            var html = Render(helper, "wt:image", FormItems(vm));

            AssertContains(html, "<img", "src=\"/_Framework/GetFile/11111111-2222-3333-4444-555555555555", "id=\"LayuiTestVM_PhotoIdimg\"");
        }

        [TestMethod]
        public void FlowInfo_WithoutWorkflowVm_RendersEmptyTimeline()
        {
            var html = Render(new FlowInfoTagHelper(), "wt:flowinfo");

            AssertContains(html, "<ul", "class=\"layui-timeline\"");
        }

        [TestMethod]
        public void UEditor_RendersEditorContainerAndLoaderScript()
        {
            var vm = new LayuiTestVM { Html = "hi", Wtm = MockWtmContext.CreateWtmContext() };
            var helper = new UEditorTagHelper { Field = Field(vm, x => x.Html) };

            var html = Render(helper, "wt:ueditor", FormItems(vm));

            AssertContains(html, "isrich=\"1\"", "layui.use(['ueditorconfig']", "ueditor.loadEditor('LayuiTestVM_Html')");
        }

        [TestMethod]
        public void SmallContentHelpers_RenderExpectedElements()
        {
            AssertContains(Render(new QuoteTagHelper(), "wt:quote", childContent: "note"), "<blockquote", "layui-elem-quote");
            AssertContains(Render(new CodeTagHelper(), "wt:code", childContent: "x=1"), "<pre", "layui-code");
        }
    }
}
