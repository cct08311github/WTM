using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #279: the display text of several widgets (check box and radio item titles, combo box and tree option text, transfer item
    /// titles, tree container node text, panel title) is written to the page as HTML by layui / xm-select, so it is encoded where the
    /// framework hands it to the widget. Text without markup characters keeps exactly the same output.
    /// </summary>
    [TestClass]
    public class DisplayTextEncodingTests : LayUITagHelperTestBase
    {
        private const string Markup = "<i id=\"p\">x</i> & 'q'";

        private static LayuiTestVM VmWithItems(string text, string value)
        {
            var vm = new LayuiTestVM { Names = new List<string>() };
            vm.AllSchools = new List<ComboSelectListItem> { new ComboSelectListItem { Text = text, Value = value } };
            return vm;
        }

        #region check box and radio

        [TestMethod]
        public void CheckBoxAndRadio_ItemTitle_IsEncodedForTheAttributeAndForTheHtmlLayuiWritesFromIt()
        {
            var vm = VmWithItems("<b>x</b>", "v1");

            foreach (var html in new[]
            {
                Render(new CheckBoxTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:checkbox"),
                Render(new RadioTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:radio"),
            })
            {
                // attribute value: &lt;b&gt;x&lt;/b&gt;, which layui writes into the item as the text <b>x</b>
                AssertContains(html, "title=\"&amp;lt;b&amp;gt;x&amp;lt;/b&amp;gt;\"");
                AssertNotContains(html, "title=\"<b>", "title=\"&lt;b&gt;");
            }
        }

        [TestMethod]
        public void CheckBoxAndRadio_ItemTitleSavedAsEncodedText_IsNotEncodedAgain()
        {
            // text saved through the model binder is HTML-encoded (UIOptions:EncodeHtmlContent in FORK.md): it shows as the text it stands for
            var vm = VmWithItems("Tom &amp; Jerry &lt;b&gt;", "v1");

            var html = Render(new CheckBoxTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:checkbox");

            AssertContains(html, "title=\"Tom &amp;amp; Jerry &amp;lt;b&amp;gt;\"");
        }

        [TestMethod]
        public void CheckBoxAndRadio_OrdinaryTitle_IsUnchanged()
        {
            var vm = VmWithItems("第一所學校 plain item", "v1");

            AssertContains(Render(new CheckBoxTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:checkbox"),
                "<input type=\"checkbox\" name=\"Names\" value=\"v1\" title=\"第一所學校 plain item\"");
            AssertContains(Render(new RadioTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:radio"),
                "<input type=\"radio\" name=\"Names\" value=\"v1\" title=\"第一所學校 plain item\"");
        }

        [TestMethod]
        public void UIService_CheckBoxAndRadio_OrdinaryTitle_IsUnchanged_AndMarkupIsEncoded()
        {
            var service = new LayuiUIService();

            AssertContains(service.MakeCheckBox(true, "Enabled", "Entity.Flag", "1"), "title='Enabled'");
            AssertContains(service.MakeRadio(true, "Enabled", "Entity.Flag", "1"), "title='Enabled'");
            AssertContains(service.MakeCheckBox(true, "<b>", "Entity.Flag", "1"), "title='&amp;lt;b&amp;gt;'");
            AssertContains(service.MakeRadio(true, "<b>", "Entity.Flag", "1"), "title='&amp;lt;b&amp;gt;'");
        }

        #endregion

        #region combo box and tree (xm-select)

        [TestMethod]
        public void ComboBox_Templates_EncodeTheOptionTextAndTheIcon_WhileTheDataKeepsTheRawText()
        {
            var vm = new LayuiTestVM { SchoolId = null };
            vm.AllSchools = new List<ComboSelectListItem> { new ComboSelectListItem { Text = "<b>x</b>", Value = "v1", Icon = "layui-icon-a" } };
            foreach (var multi in new[] { true, false })
            {
                var helper = new ComboBoxTagHelper(Options(), null) { Field = Field(vm, x => x.SchoolId), Items = Field(vm, x => x.AllSchools), MultiSelect = multi };

                var html = Render(helper, "wt:combobox");

                AssertContains(html, "ff.EncodeHtmlOnce(item.name)", "'<i class=\"'+ff.EncodeHtml(item.icon)+'\"></i>' + ff.EncodeHtmlOnce(item.name)");
                AssertNotContains(html, "return item.name;", "+ item.name;", "+item.icon+");
                // the widget keeps the raw text (search, values), only what it writes to the page is encoded
                AssertContains(html, "\"name\":\"\\u003Cb\\u003Ex\\u003C/b\\u003E\"");
            }
            var single = Render(new ComboBoxTagHelper(Options(), null) { Field = Field(vm, x => x.SchoolId), Items = Field(vm, x => x.AllSchools), MultiSelect = false }, "wt:combobox");
            AssertContains(single, "return ff.EncodeHtmlOnce(sels[0].name);");
            AssertNotContains(single, "return sels[0].name;", "+ sels[0].name;");
        }

        [TestMethod]
        public void Tree_Templates_EncodeTheNodeTextAndTheIcon()
        {
            var vm = new LayuiTestVM { Names = new List<string>() };
            vm.Menus = new List<TreeSelectListItem> { new TreeSelectListItem { Text = "<b>x</b>", Value = "1", Icon = "layui-icon-a" } };

            // a list field is a multi select tree, a single value field a single select tree
            var multi = Render(new TreeTagHelper(Options()) { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.Menus) }, "wt:tree");
            var single = Render(new TreeTagHelper(Options()) { Field = Field(vm, x => x.Name), Items = Field(vm, x => x.Menus) }, "wt:tree");

            foreach (var html in new[] { multi, single })
            {
                AssertContains(html, "ff.EncodeHtmlOnce(item.name)", "'<i class=\"'+ff.EncodeHtml(item.icon)+'\"></i>' + ff.EncodeHtmlOnce(item.name)");
                AssertNotContains(html, "return item.name;", "+ item.name;", "+item.icon+");
            }
            AssertContains(single, "return ff.EncodeHtmlOnce(sels[0].name);");
            AssertNotContains(single, "return sels[0].name;", "+ sels[0].name;");
        }

        #endregion

        #region transfer

        private static string RenderTransfer(string text, string value, List<string> selected = null)
        {
            var vm = new LayuiTestVM { Names = selected ?? new List<string>() };
            vm.AllSchools = new List<ComboSelectListItem> { new ComboSelectListItem { Text = text, Value = value } };
            return Render(new TransferTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:transfer");
        }

        [TestMethod]
        public void Transfer_Items_AreEncodedWhereTheyAreGivenToTheWidget()
        {
            var html = RenderTransfer("<b>x</b>", "a\"b");

            // the data are handed over through ff.encodeTransferItems, which encodes the value and the title (see option-encoding.test.js)
            AssertContains(html, ",data:ff.encodeTransferItems([{", "\"title\":\"\\u003Cb\\u003Ex\\u003C/b\\u003E\"");
        }

        [TestMethod]
        public void Transfer_PostedValues_AreReadFromTheRightListNotOnlyFromTheItemsMovedLast()
        {
            var html = RenderTransfer("A", "a", new List<string> { "a" });

            AssertContains(html,
                "$('#'+_id+' .layui-transfer-box').eq(1).find('.layui-transfer-data input[type=\"checkbox\"]').each(function(){",
                "selectVals.push(this.value);",
                "container.append($('<input type=\"hidden\"/>').attr('name',name).attr('value',selectVals[i]));",
                // the values that were selected when the page was shown are posted as before
                "container.append($('<input type=\"hidden\"/>').attr('name',name).attr('value',defaultVal[i]));",
                ",value:$.map(defaultVal,function(v){ return ff.EncodeHtml(v); })");
            AssertNotContains(html, "transfer.getData(", "value=\"'+selectVals[i].value+'\"");
        }

        [TestMethod]
        public void Transfer_PreselectedValue_IsAJavaScriptStringLiteral()
        {
            var html = RenderTransfer("A", "a", new List<string> { "it's" });

            AssertContains(html, "var defaultVal = ['it\\u0027s'];");
            AssertNotContains(html, "'it's'");
        }

        [TestMethod]
        public void Transfer_OrdinaryPreselectedValue_KeepsTheDefaultValueLiteral()
        {
            var html = RenderTransfer("A", "a", new List<string> { "a", "b" });

            AssertContains(html, "var defaultVal = ['a','b'];");
        }

        #endregion

        #region tree container and panel

        private static string RenderTreeContainer(string text)
        {
            var vm = new LayuiTestVM
            {
                Menus = new List<TreeSelectListItem> { new TreeSelectListItem { Text = text, Value = "n1", Url = "/x" } }
            };
            return Render(new TreeContainerTagHelper { Items = Field(vm, x => x.Menus), Id = "tc" }, "wt:treecontainer");
        }

        [TestMethod]
        public void TreeContainer_NodeText_IsEncodedForTheHtmlLayuiWritesFromIt()
        {
            var html = RenderTreeContainer(Markup);

            // &lt;i id=&quot;p&quot;&gt;x&lt;/i&gt; &amp; &#39;q&#39; (the script data encoder writes & and < > ' as \uXXXX)
            AssertContains(html, "\"title\":\"\\u0026lt;i id=\\u0026quot;p\\u0026quot;\\u0026gt;x\\u0026lt;/i\\u0026gt; \\u0026amp; \\u0026#39;q\\u0026#39;\"");
            AssertNotContains(html, "\"title\":\"\\u003Ci");
        }

        [TestMethod]
        public void TreeContainer_OrdinaryNodeText_IsUnchanged()
        {
            AssertContains(RenderTreeContainer("Node 1 plain"), "\"title\":\"Node 1 plain\"");
        }

        [TestMethod]
        public void Panel_Title_FollowsTheEncodingOfTheOtherTitles()
        {
            foreach (var type in new[] { PanelType.Collapse, PanelType.Card })
            {
                var on = Render(new PanelTagHelper { Title = Markup, PanelType = type, ConfigsMonitor = Options(c => c.UIOptions.EncodeHtmlContent = true) }, "wt:panel");
                var off = Render(new PanelTagHelper { Title = Markup, PanelType = type, ConfigsMonitor = Options() }, "wt:panel");

                AssertContains(on, ">&lt;i id=&quot;p&quot;&gt;x&lt;/i&gt; &amp; &#39;q&#39;</");
                AssertNotContains(on, "<i id=\"p\">");
                // default setting: emitted as it is, like the titles of card, tab and fieldset
                AssertContains(off, ">" + Markup + "</");
            }
        }

        [TestMethod]
        public void Panel_OrdinaryTitle_IsUnchangedWithEitherSetting()
        {
            foreach (var type in new[] { PanelType.Collapse, PanelType.Card })
            {
                var on = Render(new PanelTagHelper { Title = "Plain 標題", PanelType = type, ConfigsMonitor = Options(c => c.UIOptions.EncodeHtmlContent = true) }, "wt:panel");
                var off = Render(new PanelTagHelper { Title = "Plain 標題", PanelType = type, ConfigsMonitor = Options() }, "wt:panel");

                // the filter id of the panel is random
                Assert.AreEqual(Regex.Replace(off, "[0-9a-f]{32}", "id"), Regex.Replace(on, "[0-9a-f]{32}", "id"));
                AssertContains(on, ">Plain 標題</");
            }
        }

        [TestMethod]
        public void Panel_NoTitle_RendersAnEmptyTitle()
        {
            AssertContains(Render(new PanelTagHelper { PanelType = PanelType.Card }, "wt:panel"), "<div class=\"layui-card-header\"></div>");
            AssertContains(Render(new PanelTagHelper { PanelType = PanelType.Collapse, ConfigsMonitor = Options(c => c.UIOptions.EncodeHtmlContent = true) }, "wt:panel"), "<h2 class=\"layui-colla-title\"></h2>");
        }

        #endregion
    }
}
