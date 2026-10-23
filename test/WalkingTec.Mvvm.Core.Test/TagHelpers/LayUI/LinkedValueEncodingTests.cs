using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #326: the values the combo box, tree, tree container and transfer write into the page when they are linked or selected are
    /// encoded for the place they are written into (a JavaScript string, an HTML attribute, an address). Ordinary values (ids, GUIDs,
    /// plain text) keep the same output, and the values the forms post are not changed.
    /// </summary>
    [TestClass]
    public class LinkedValueEncodingTests : LayUITagHelperTestBase
    {
        // all five characters that need HTML encoding, a backslash, a line break and the end of a script element
        private const string Special = "a'b\"c&d<e>f\\g\nh</script>";

        #region tree container

        private static string RenderTreeContainer(string nodeId, string selectedId = null, string autoLoadUrl = null, bool autoLoad = false)
        {
            var vm = new LayuiTestVM
            {
                Name = selectedId,
                Menus = new List<TreeSelectListItem> { new TreeSelectListItem { Text = "Node", Value = nodeId, Url = "/x" } }
            };
            return Render(new TreeContainerTagHelper
            {
                Items = Field(vm, x => x.Menus),
                IdField = selectedId == null ? null : Field(vm, x => x.Name),
                Id = "tc",
                AutoLoad = autoLoad,
                AutoLoadUrl = autoLoadUrl
            }, "wt:treecontainer");
        }

        [TestMethod]
        public void TreeContainer_SelectedNodeId_IsAJavaScriptStringLiteral()
        {
            var html = RenderTreeContainer(Special, Special);

            AssertContains(html, "toptcselected.Name = 'a\\u0027b\\u0022c\\u0026d\\u003Ce\\u003Ef\\\\g\\nh\\u003C/script\\u003E';");
            AssertNotContains(html, "toptcselected.Name = 'a'b", "h</script>';");
        }

        [TestMethod]
        public void TreeContainer_OrdinarySelectedNodeId_KeepsItsOutput()
        {
            var html = RenderTreeContainer("n1", "n1");

            AssertContains(html, "toptcselected.Name = 'n1';", "\"id\":\"n1\"");
        }

        [TestMethod]
        public void TreeContainer_NodeIds_AreEncodedForTheAttributeLayuiWritesThemInto()
        {
            var html = RenderTreeContainer("a'b\"c&d<e>");

            // layui writes data-id="<id>" without encoding: the id is handed over encoded, the browser reads the original back
            // (the script data encoder writes & and < > ' " as \uXXXX)
            AssertContains(html, "\"id\":\"a\\u0026#39;b\\u0026quot;c\\u0026amp;d\\u0026lt;e\\u0026gt;\"");
            AssertNotContains(html, "\"id\":\"a\\u0027b");
        }

        [TestMethod]
        public void TreeContainer_PreselectedNode_IsFoundThroughItsEncodedId()
        {
            var html = RenderTreeContainer("a'b\"c", "a'b\"c");

            // the selected node handed to setSelected carries the same encoded id as the tree data
            AssertContains(html, "setSelected({", "\"id\":\"a\\u0026#39;b\\u0026quot;c\"");
            // the value kept for the grid filter is the original id
            AssertContains(html, "toptcselected.Name = 'a\\u0027b\\u0022c';");
        }

        [TestMethod]
        public void TreeContainer_Handlers_FindTheNodeWithoutBuildingASelectorFromTheId()
        {
            var html = RenderTreeContainer("n1");

            AssertContains(html,
                ",click: function(data){",
                "data = ff.treeNodeData(data);",
                "ele = ff.findTreeNode($('#divtc'), data).find('.layui-tree-main:first');");
            AssertNotContains(html, "div[data-id='", "+data.data.id+");
        }

        [TestMethod]
        public void TreeContainer_AutoLoadUrl_IsAJavaScriptStringLiteral()
        {
            var html = RenderTreeContainer("n1", autoLoad: true, autoLoadUrl: "/p?a=1&b='x';c");

            AssertContains(html, "ff.LoadPage1('/p?a=1\\u0026b=\\u0027x\\u0027;c','div_tc');");
            AssertNotContains(html, "LoadPage1('/p?a=1&b='x'");
        }

        [TestMethod]
        public void TreeContainer_OrdinaryAutoLoadUrl_KeepsItsOutput()
        {
            var html = RenderTreeContainer("n1", autoLoad: true, autoLoadUrl: "/Home/Page");

            AssertContains(html, "ff.LoadPage1('/Home/Page','div_tc');");
        }

        #endregion

        #region linkage of combo box and tree

        [TestMethod]
        public void ComboBox_Linkage_AppendsTheChosenValuesAsEncodedAddressParts()
        {
            var vm = new LayuiTestVM { SchoolId = null, Names = new List<string> { Special } };
            var multi = new ComboBoxTagHelper(Options(), null)
            {
                Field = Field(vm, x => x.Names),
                Items = Field(vm, x => x.AllSchools),
                MultiSelect = true,
                LinkId = "other",
                TriggerUrl = "/School/Trigger",
            };
            vm.AllSchools = new List<ComboSelectListItem>();

            var html = Render(multi, "wt:combobox");

            // a value with & # or a space must not cut or change the address
            AssertContains(html, "u += \"&id=\" + encodeURIComponent(data.arr[i].value);");
            AssertContains(html, "LayuiTestVM_Namesu += \"&id=\" + encodeURIComponent(LayuiTestVM_Namesdata[i]);");
            AssertNotContains(html, "\"&id=\" + data.arr[i].value", "\"&id=\" + LayuiTestVM_Namesdata[i]");
        }

        [TestMethod]
        public void Tree_Linkage_AppendsTheChosenValuesAsEncodedAddressParts()
        {
            var vm = new LayuiTestVM { Names = new List<string> { Special }, Menus = new List<TreeSelectListItem>() };
            var helper = new TreeTagHelper(Options())
            {
                Field = Field(vm, x => x.Names),
                Items = Field(vm, x => x.Menus),
                LinkId = "other",
                TriggerUrl = "/School/Trigger",
            };

            var html = Render(helper, "wt:tree");

            AssertContains(html, "u += \"&id=\" + encodeURIComponent(data.arr[i].value);");
            AssertContains(html, "LayuiTestVM_Namesu += \"&id=\" + encodeURIComponent(LayuiTestVM_Namesdata[i]);");
            AssertNotContains(html, "\"&id=\" + data.arr[i].value", "\"&id=\" + LayuiTestVM_Namesdata[i]");
        }

        [TestMethod]
        public void TextBox_AutocompleteTrigger_AppendsTheChosenValueAsAnEncodedAddressPart()
        {
            var textBox = new TextBoxTagHelper { Field = Field(new LayuiTestVM(), x => x.Name), SearchUrl = "/School/Search", TriggerUrl = "/School/Trigger" };

            var html = Render(textBox, "wt:textbox");

            AssertContains(html, "ff.ChainChange('/School/Trigger/'+encodeURIComponent(data.Value), data.elem);");
            AssertNotContains(html, "'+data.Value");
        }

        #endregion

        #region transfer

        private static string RenderTransfer(string left, string right, string none, bool encodeHtmlContent)
        {
            var vm = new LayuiTestVM { Names = new List<string>(), AllSchools = new List<ComboSelectListItem>() };
            return Render(new TransferTagHelper
            {
                Id = "tr",
                Field = Field(vm, x => x.Names),
                Items = Field(vm, x => x.AllSchools),
                LeftTitle = left,
                RightTitle = right,
                NonePlaceholder = none,
                SearchNonePlaceholder = none,
                ConfigsMonitor = Options(c => c.UIOptions.EncodeHtmlContent = encodeHtmlContent)
            }, "wt:transfer");
        }

        [TestMethod]
        public void Transfer_TitlesAndNoDataText_AreEncodedWhenHtmlContentIsEncoded()
        {
            var html = RenderTransfer("<b>L</b>&'", "R\"", "<i>none</i>", encodeHtmlContent: true);

            // layui writes the titles and the texts of the empty lists as HTML: &lt;b&gt;L&lt;/b&gt;&amp;&#39;
            AssertContains(html,
                "['\\u0026lt;b\\u0026gt;L\\u0026lt;/b\\u0026gt;\\u0026amp;\\u0026#39;','R\\u0026quot;']",
                "text:{none:'\\u0026lt;i\\u0026gt;none\\u0026lt;/i\\u0026gt;',searchNone:'\\u0026lt;i\\u0026gt;none\\u0026lt;/i\\u0026gt;'}");
        }

        [TestMethod]
        public void Transfer_TitlesAndNoDataText_AreEmittedAsTheyAreByDefault()
        {
            // like the titles of card, tab and fieldset: markup in a title set by the page is kept unless UIOptions:EncodeHtmlContent is on
            var html = RenderTransfer("<b>L</b>", "R", "<i>none</i>", encodeHtmlContent: false);

            AssertContains(html, "['\\u003Cb\\u003EL\\u003C/b\\u003E','R']", "text:{none:'\\u003Ci\\u003Enone\\u003C/i\\u003E'");
        }

        [TestMethod]
        public void Transfer_OrdinaryTitlesAndNoDataText_AreUnchangedWithEitherSetting()
        {
            var on = RenderTransfer("待選", "已選", "No data", encodeHtmlContent: true);
            var off = RenderTransfer("待選", "已選", "No data", encodeHtmlContent: false);

            Assert.AreEqual(off, on);
            AssertContains(on, "['待選','已選']", "text:{none:'No data',searchNone:'No data'}");
        }

        #endregion
    }
}
