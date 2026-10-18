using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Smoke tests for wt:form, wt:searchpanel, wt:grid and the button family.
    /// </summary>
    [TestClass]
    public class FormAndButtonTagHelperTests : LayUITagHelperTestBase
    {
        // A list VM attached to a WTMContext, like the one the framework hands to a view
        // (wt:grid reads the UI defaults, e.g. rows-per-page, from vm.ConfigInfo).
        private static LayuiSchoolListVM NewListVm() => new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() };

        private static string FormIdOf(BaseVM vm) => FormTagHelper.FORM_ID_PREFIX + vm.UniqueId;

        #region wt:form

        [TestMethod]
        public void Form_RendersLayuiFormWithSubmitScript()
        {
            var vm = new LayuiTestVM();
            var helper = new FormTagHelper { Vm = Self(vm, "Vm") };

            var html = Render(helper, "wt:form", childContent: "<span>inside</span>");

            AssertContains(html,
                "<form", "class=\"layui-form\"", "method=\"post\"", $"id=\"{FormIdOf(vm)}\"", $"lay-filter=\"{FormIdOf(vm)}\"",
                "<span>inside</span>", "</form>",
                $"ff.RenderForm('{FormIdOf(vm)}')", "layui.form.on('submit(", "ff.PostForm(",
                "name='FromView'", "lay-submit");
        }

        [TestMethod]
        public void Form_PublishesFormIdAndModelToDescendants()
        {
            var vm = new LayuiTestVM();
            var helper = new FormTagHelper { Vm = Self(vm, "Vm") };
            var items = new Dictionary<object, object>();

            Render(helper, "wt:form", items);

            Assert.AreEqual(FormIdOf(vm), items["formid"]);
            Assert.AreSame(vm, items["model"]);
        }

        [TestMethod]
        public void Form_OldPost_DoesNotEmitAjaxSubmitHandler()
        {
            var vm = new LayuiTestVM();
            var helper = new FormTagHelper { Vm = Self(vm, "Vm"), OldPost = true };

            var html = Render(helper, "wt:form");

            AssertContains(html, "<form");
            AssertNotContains(html, "layui.form.on('submit(");
        }

        [TestMethod]
        public void Form_ExplicitUrlAndLabelWidth_AreApplied()
        {
            var vm = new LayuiTestVM();
            var helper = new FormTagHelper { Vm = Self(vm, "Vm"), Url = "/Home/Save", LabelWidth = 120 };
            var items = new Dictionary<object, object>();

            var html = Render(helper, "wt:form", items);

            AssertContains(html, "action=\"/Home/Save\"");
            Assert.AreEqual(120, items["formlabelwidth"]);
        }

        [TestMethod]
        public void Form_WithoutVm_RendersHelpfulMessage()
        {
            var html = Render(new FormTagHelper(), "wt:form");

            AssertContains(html, "<div>", "VM is not set");
            AssertNotContains(html, "<form");
        }

        [TestMethod]
        public void Form_ServerSideErrors_AreEmittedAndAngleBracketsEscaped()
        {
            var vm = new LayuiTestVM { Wtm = MockWtmContext.CreateWtmContext() };
            vm.MSD.AddModelError("Name", "bad <b>value</b>");
            var helper = new FormTagHelper { Vm = Self(vm, "Vm") };

            var html = Render(helper, "wt:form");

            // angle brackets are HTML-encoded, then the text is escaped for the JavaScript string literal
            AssertContains(html, "layui-form-danger", "bad \\u0026lt;b\\u0026gt;value\\u0026lt;/b\\u0026gt;");
            AssertNotContains(html, "<b>value</b>", "&lg;b&rg;");
        }

        [TestMethod]
        public void FormFields_InsideForm_PickUpFormLabelWidth()
        {
            var vm = new LayuiTestVM();
            var items = FormItems(vm, FormIdOf(vm));
            items["formlabelwidth"] = 150;
            var helper = new TextBoxTagHelper { Field = Field(vm, x => x.Memo) };

            var html = Render(helper, "wt:textbox", items);

            AssertContains(html, "min-height:21px;width:150px");
        }

        #endregion

        #region wt:searchpanel

        [TestMethod]
        public void SearchPanel_RendersCollapsibleFormWithSearchButtonAndGridRefresh()
        {
            var listVm = NewListVm();
            var helper = new SearchPanelTagHelper(Options()) { Vm = Self(listVm, "Vm"), ResetBtn = true };

            var html = Render(helper, "wt:searchpanel", childContent: "<input name=\"x\"/>");

            AssertContains(html,
                "<form", "class=\"layui-form\"",
                "layui-collapse", "layui-colla-title", "Sys.SearchCondition",
                "IsSearchButton", $"id=\"{SearchPanelTagHelper.SEARCH_BTN_ID_PREFIX}{FormTagHelper.FORM_ID_PREFIX}{listVm.UniqueId}\"",
                "Sys.Search", "Sys.Reset",
                $"table.reload('{DataTableTagHelper.TABLE_ID_PREFIX}{listVm.UniqueId}'",
                "name='IsExpanded'");
        }

        [TestMethod]
        public void SearchPanel_DoesNotPublishFormIdToChildren()
        {
            var listVm = NewListVm();
            var helper = new SearchPanelTagHelper(Options()) { Vm = Self(listVm, "Vm") };
            var items = new Dictionary<object, object>();

            Render(helper, "wt:searchpanel", items);

            Assert.IsFalse(items.ContainsKey("formid"));
            Assert.IsTrue(items.ContainsKey("model"));
        }

        #endregion

        #region wt:grid

        [TestMethod]
        public void Grid_RendersTableAndLayuiTableScript()
        {
            var listVm = NewListVm();
            var helper = new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" };
            var gridId = DataTableTagHelper.TABLE_ID_PREFIX + listVm.UniqueId;

            var html = Render(helper, "wt:grid");

            AssertContains(html,
                "<table", $"id=\"{gridId}\"", $"lay-filter=\"{gridId}\"", "</table>",
                "layui.use(['table']", "table.render(", $"elem: '#{gridId}'", $"{gridId}url = '/School/Search'",
                "layuisearch: 'true'", "method:'post'", "Sys.NoData",
                // columns from the list VM
                "SchoolCode", "SchoolName", "SchoolType",
                // default checkbox + numbers columns
                "checkbox", "numbers",
                // sorting + paging hooks
                "table.on('sort(", "table.on('tool(", "SortInfo.Property");
        }

        [TestMethod]
        public void Grid_HiddenCheckboxAndIndex_AreOmitted()
        {
            var listVm = NewListVm();
            var helper = new DataTableTagHelper { Vm = Self(listVm, "Vm"), HiddenCheckbox = true, HiddenGridIndex = true };

            var html = Render(helper, "wt:grid");

            AssertNotContains(html, "\"type\":\"checkbox\"", "\"type\":\"numbers\"");
            AssertContains(html, "SchoolCode");
        }

        [TestMethod]
        public void Grid_ExplicitIdAndHeight_AreApplied()
        {
            var listVm = NewListVm();
            var helper = new DataTableTagHelper { Vm = Self(listVm, "Vm"), Id = "mygrid", Height = 300, Limit = 15 };

            var html = Render(helper, "wt:grid");

            AssertContains(html, "id=\"mygrid\"", "elem: '#mygrid'", ",height: 300", ",limit:15");
        }

        #endregion

        #region buttons

        [TestMethod]
        public void Button_RendersLayuiButtonWithClickHandler()
        {
            var helper = new ButtonTagHelper { Id = "btn1", Text = "Go", Click = "doIt()", Theme = ButtonThemeEnum.Primary, Size = ButtonSizeEnum.Small };

            var html = Render(helper, "wt:button");

            AssertContains(html, "<button", "type=\"button\"", "id=\"btn1\"", "layui-btn layui-btn-sm layui-btn-primary", ">Go</button>",
                "$('#btn1').on('click'", "doIt();return false;");
        }

        [TestMethod]
        public void Button_WithIcon_RendersIconElement()
        {
            var helper = new ButtonTagHelper { Id = "btn2", Text = "Add", Icon = "layui-icon layui-icon-add-1" };

            AssertContains(Render(helper, "wt:button"), "<i class=\"layui-icon layui-icon-add-1\"></i> Add");
        }

        [TestMethod]
        public void Button_Disabled_HasDisabledAttributeAndNoClickHandlerBody()
        {
            var helper = new ButtonTagHelper { Id = "btn3", Text = "No", Click = "x()", Disabled = true };

            var html = Render(helper, "wt:button");

            AssertContains(html, "disabled", "layui-btn-disabled");
            AssertNotContains(html, "x();return false;");
        }

        [TestMethod]
        public void Button_ConfirmText_WrapsClickInLayerConfirm()
        {
            var helper = new ButtonTagHelper { Id = "btn4", Text = "Del", Click = "del()", ConfirmTxt = "Sure?" };

            AssertContains(Render(helper, "wt:button"), "layer.confirm('Sure?'", "del();layer.close(index);");
        }

        [TestMethod]
        public void SubmitButton_InsideForm_UsesFormFilterAndSubmitType()
        {
            var vm = new LayuiTestVM();
            var formId = FormIdOf(vm);
            var helper = new SubmitButtonTagHelper { Id = "sb1" };

            var html = Render(helper, "wt:submitbutton", FormItems(vm, formId));

            AssertContains(html, "<button", "type=\"submit\"", "lay-submit", $"lay-filter=\"{formId}filter\"", "Sys.Submit", "layui-btn");
        }

        [TestMethod]
        public void SubmitButton_WithClick_PostsFormAfterValidation()
        {
            var vm = new LayuiTestVM();
            var formId = FormIdOf(vm);
            var helper = new SubmitButtonTagHelper { Id = "sb2", Click = "check()" };

            var html = Render(helper, "wt:submitbutton", FormItems(vm, formId));

            AssertContains(html, "function f_sb2Click()", "ff.PostForm('', '" + formId + "'", "lay-filter=\"f_sb2filter\"");
        }

        [TestMethod]
        public void ResetButton_RendersResetTypeWithDefaultText()
        {
            var html = Render(new ResetButtonTagHelper { Id = "rb1" }, "wt:resetbutton");

            AssertContains(html, "<button", "type=\"reset\"", "Sys.Reset", "layui-btn");
        }

        [TestMethod]
        public void CloseButton_ClosesDialog()
        {
            var html = Render(new CloseButtonTagHelper { Id = "cb1" }, "wt:closebutton");

            AssertContains(html, "<button", "Sys.Close", "ff.CloseDialog();return false;");
        }

        [TestMethod]
        public void LinkButton_Layer_OpensDialog()
        {
            var helper = new LinkButtonTagHelper { Id = "lb1", Text = "Edit", Url = "/School/Edit", WindowTitle = "Edit school", WindowWidth = 500 };

            var html = Render(helper, "wt:linkbutton");

            AssertContains(html, "<button", "type=\"button\"", ">Edit</button>", "ff.OpenDialog('/School/Edit'", "'Edit school'", "500");
        }

        [TestMethod]
        public void LinkButton_AsAnchor_RendersHref()
        {
            var helper = new LinkButtonTagHelper { Id = "lb2", Text = "Home", Url = "/Home", IsLink = true, Target = ButtonTargetEnum.currentwindow };

            var html = Render(helper, "wt:linkbutton");

            AssertContains(html, "<a", "href=\"#\"", "ff.LoadPage('/Home'");
        }

        [TestMethod]
        public void LinkButton_SelfTarget_UsesBackgroundRequest()
        {
            var helper = new LinkButtonTagHelper { Id = "lb3", Text = "Run", Url = "/Job/Run", Target = ButtonTargetEnum.self };

            AssertContains(Render(helper, "wt:linkbutton"), "ff.BgRequest('/Job/Run')");
        }

        [TestMethod]
        public void DownloadTemplateButton_LinksToTemplateEndpoint()
        {
            var vm = new LayuiTestVM();
            var helper = new DownloadTemplateButtonTagHelper { Id = "dt1", Vm = Self(vm, "Vm") };

            var html = Render(helper, "wt:downloadTemplateButton");

            AssertContains(html, "<a", "/_Framework/GetExcelTemplate?_DONOT_USE_VMNAME=", "LayuiTestVM", "Sys.DownloadTemplate");
        }

        #endregion
    }
}
