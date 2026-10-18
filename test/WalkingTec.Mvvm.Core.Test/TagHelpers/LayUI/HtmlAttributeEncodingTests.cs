using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #24 (part B): values taken from data or configuration that are written into HTML attribute
    /// values of hand-built markup must be HTML-encoded, so quotes or markup in the value cannot break
    /// out of the attribute. Ordinary values (including CJK text) must be rendered byte for byte as before.
    /// </summary>
    [TestClass]
    public class HtmlAttributeEncodingTests : LayUITagHelperTestBase
    {
        // contains both quote kinds, markup and an ampersand
        private const string Hostile = "a'b\"c<d>&e";
        // its encoded form inside an attribute value
        private const string HostileEncoded = "a&#39;b&quot;c&lt;d&gt;&amp;e";

        private const string Cjk = "第一所學校：測試";

        public class HostileLabelVM : BaseVM
        {
            [Display(Name = Hostile)]
            [Required]
            public string Title { get; set; }

            [Display(Name = Hostile)]
            [Required]
            public Guid? PhotoId { get; set; }

            [Display(Name = Hostile)]
            [Required]
            public List<TestSubFile> Files { get; set; }
        }

        public class HiddenRow
        {
            public string Title { get; set; }
            public int Order { get; set; }
        }

        public class HiddenRowsVM : BaseVM
        {
            public List<HiddenRow> Rows { get; set; }
        }

        private static LayuiTestVM NewVm() => new LayuiTestVM();

        private static WTMContext QuickDebugContext()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.ConfigInfo.IsQuickDebug = true;
            return wtm;
        }

        #region check box / radio

        private static LayuiTestVM VmWithItems(string text, string value)
        {
            var vm = NewVm();
            vm.Names = new List<string>();
            vm.AllSchools = new List<ComboSelectListItem> { new ComboSelectListItem { Text = text, Value = value } };
            return vm;
        }

        [TestMethod]
        public void CheckBox_ItemValueAndText_AreEncoded()
        {
            var vm = VmWithItems(Hostile, Hostile);

            var html = Render(new CheckBoxTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:checkbox");

            AssertContains(html, "value=\"" + HostileEncoded + "\" title=\"" + HostileEncoded + "\"");
            AssertNotContains(html, "value=\"a'b", "title=\"a'b", "c<d>");
        }

        [TestMethod]
        public void CheckBox_OrdinaryItem_IsRenderedUnchanged()
        {
            var vm = VmWithItems(Cjk, "S-01");

            var html = Render(new CheckBoxTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:checkbox");

            AssertContains(html, "<input type=\"checkbox\" name=\"Names\" value=\"S-01\" title=\"" + Cjk + "\"");
        }

        [TestMethod]
        public void Radio_ItemValueAndText_AreEncoded()
        {
            var vm = VmWithItems(Hostile, Hostile);

            var html = Render(new RadioTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:radio");

            AssertContains(html, "value=\"" + HostileEncoded + "\" title=\"" + HostileEncoded + "\"");
            AssertNotContains(html, "value=\"a'b", "title=\"a'b", "c<d>");
        }

        [TestMethod]
        public void Radio_OrdinaryItem_IsRenderedUnchanged()
        {
            var vm = VmWithItems(Cjk, "S-01");

            var html = Render(new RadioTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) }, "wt:radio");

            AssertContains(html, "<input type=\"radio\" name=\"Names\" value=\"S-01\" title=\"" + Cjk + "\"");
        }

        #endregion

        #region hidden

        [TestMethod]
        public void Hidden_ListOfValues_AreEncoded()
        {
            var vm = NewVm();
            vm.Tags = new List<string> { Hostile, Cjk };

            var html = Render(new HiddenTagHelper { Field = Field(vm, x => x.Tags), Name = Hostile }, "wt:hidden");

            AssertContains(html,
                "name=\"" + HostileEncoded + "\" value=\"" + HostileEncoded + "\"",
                "name=\"" + HostileEncoded + "\" value=\"" + Cjk + "\"");
            AssertNotContains(html, "value=\"a'b", "name=\"a'b", "c<d>");
        }

        [TestMethod]
        public void Hidden_ListOfValues_OrdinaryValuesAreUnchanged()
        {
            var vm = NewVm();
            vm.Tags = new List<string> { "a", Cjk };

            var html = Render(new HiddenTagHelper { Field = Field(vm, x => x.Tags) }, "wt:hidden");

            AssertContains(html, "<input type=\"hidden\" name=\"Tags\" value=\"a\" class=\"layui-input\"/>",
                "<input type=\"hidden\" name=\"Tags\" value=\"" + Cjk + "\" class=\"layui-input\"/>");
        }

        [TestMethod]
        public void Hidden_ListOfObjects_PropertyValuesAreEncoded()
        {
            var vm = new HiddenRowsVM { Rows = new List<HiddenRow> { new HiddenRow { Title = Hostile, Order = 3 } } };

            var html = Render(new HiddenTagHelper { Field = Field(vm, x => x.Rows) }, "wt:hidden");

            AssertContains(html,
                "name=\"Rows[0].Title\" value=\"" + HostileEncoded + "\"",
                "name=\"Rows[0].Order\" value=\"3\"");
            AssertNotContains(html, "value=\"a'b", "c<d>");
        }

        [TestMethod]
        public void Hidden_ListOfObjects_OrdinaryValuesAreUnchanged()
        {
            var vm = new HiddenRowsVM { Rows = new List<HiddenRow> { new HiddenRow { Title = Cjk, Order = 3 } } };

            var html = Render(new HiddenTagHelper { Field = Field(vm, x => x.Rows) }, "wt:hidden");

            AssertContains(html, "<input type=\"hidden\" name=\"Rows[0].Title\" value=\"" + Cjk + "\" class=\"layui-input\"/>");
        }

        #endregion

        #region form

        [TestMethod]
        public void Form_FromView_IsEncoded()
        {
            var vm = NewVm();
            vm.CurrentView = Hostile;

            var html = Render(new FormTagHelper { Vm = Self(vm, "Vm") }, "wt:form");

            AssertContains(html, "<input type='hidden' name='FromView' value='" + HostileEncoded + "' />");
            AssertNotContains(html, "value='a'b", "c<d>");
        }

        [TestMethod]
        public void Form_FromView_UrlValue_DecodesToTheSameText()
        {
            var vm = NewVm();
            vm.CurrentView = "/School/Index?tab=1&id=2";

            var html = Render(new FormTagHelper { Vm = Self(vm, "Vm") }, "wt:form");

            AssertContains(html, "<input type='hidden' name='FromView' value='/School/Index?tab=1&amp;id=2' />");
        }

        #endregion

        #region color picker / slider / tree / upload

        [TestMethod]
        public void ColorPicker_Value_IsEncoded()
        {
            var vm = NewVm();
            vm.Color = Hostile;

            var html = Render(new ColorPickerTagHelper { Field = Field(vm, x => x.Color) }, "wt:colorpicker");

            AssertContains(html, "name='Color' value='" + HostileEncoded + "'");
            AssertNotContains(html, "value='a'b", "value='a\"b");
        }

        [TestMethod]
        public void ColorPicker_OrdinaryValueIsUnchanged()
        {
            var vm = NewVm();
            vm.Color = "#ff0000";

            AssertContains(Render(new ColorPickerTagHelper { Field = Field(vm, x => x.Color) }, "wt:colorpicker"),
                "<input type='hidden' id='LayuiTestVM_Color' name='Color' value='#ff0000' />");
        }

        [TestMethod]
        public void ColorPicker_RequiredMessage_IsEncoded()
        {
            var vm = new HostileLabelVM();

            var html = Render(new ColorPickerTagHelper { Field = Field(vm, x => x.Title) }, "wt:colorpicker");

            AssertContains(html, "lay-reqText=\"Validate." + HostileEncoded + "required\"");
            AssertNotContains(html, "lay-reqText=\"Validate.a'b", "lay-reqText=\"Validate.a&#39;b\"c");
        }

        [TestMethod]
        public void Slider_Values_AreEncoded()
        {
            var vm = NewVm();
            vm.Memo = Hostile;
            vm.Color = Hostile;

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Memo), Field1 = Field(vm, x => x.Color) }, "wt:slider");

            AssertContains(html,
                "<input type='hidden' name='Memo' value='" + HostileEncoded + "' class='layui-input'>",
                "<input type='hidden' name='Color' value='" + HostileEncoded + "' class='layui-input'>");
            AssertNotContains(html, "value='a'b", "value='a\"b");
        }

        [TestMethod]
        public void Slider_OrdinaryValues_AreUnchanged()
        {
            var vm = NewVm();
            vm.Score = 20;
            vm.ScoreMax = 80;

            var html = Render(new SliderTagHelper { Field = Field(vm, x => x.Score), Field1 = Field(vm, x => x.ScoreMax) }, "wt:slider");

            AssertContains(html,
                "<input type='hidden' name='Score' value='20' class='layui-input'>",
                "<input type='hidden' name='ScoreMax' value='80' class='layui-input'>");
        }

        [TestMethod]
        public void Tree_HiddenValues_AreEncoded()
        {
            var vm = NewVm();
            vm.Menus = new List<TreeSelectListItem> { new TreeSelectListItem { Text = "root", Value = "1" } };
            vm.Names = new List<string> { Hostile, Cjk };

            var html = Render(new TreeTagHelper(Options()) { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.Menus) }, "wt:tree");

            AssertContains(html,
                "<input type='hidden' name='Names' value='" + HostileEncoded + "'/>",
                "<input type='hidden' name='Names' value='" + Cjk + "'/>");
            AssertNotContains(html, "name='Names' value='a'b");
        }

        [TestMethod]
        public void Tree_SingleSelect_HiddenValue_IsEncoded()
        {
            var vm = NewVm();
            vm.Menus = new List<TreeSelectListItem> { new TreeSelectListItem { Text = "root", Value = "1" } };
            vm.Name = Hostile;

            var html = Render(new TreeTagHelper(Options()) { Field = Field(vm, x => x.Name), Items = Field(vm, x => x.Menus) }, "wt:tree");

            AssertContains(html, "<input type='hidden' name='Name' value='" + HostileEncoded + "'/>");
            AssertNotContains(html, "name='Name' value='a'b");
        }

        // no database in these tests: the selector only needs some rows to show
        private class NoDbSchoolListVM : LayuiSchoolListVM
        {
            public override void DoSearch()
            {
                EntityList = new List<School> { new School { SchoolName = "shown" } };
                IsSearched = true;
            }
        }

        [TestMethod]
        public void Selector_HiddenValues_AreEncoded()
        {
            var vm = NewVm();
            vm.Names = new List<string> { Hostile, "S-01" };
            var schoolVm = new NoDbSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() };
            var helper = new SelectorTagHelper
            {
                Field = Field(vm, x => x.Names),
                ListVM = Self(schoolVm, "Selector"),
                TextBind = Field(new School(), x => x.SchoolName),
            };

            var html = Render(helper, "wt:selector", FormItems(vm, "wtForm_x"));

            AssertContains(html,
                "<input type='hidden' name='Names' value='" + HostileEncoded + "' />",
                "<input type='hidden' name='Names' value='S-01' />");
            AssertNotContains(html, "name='Names' value='a'b");
        }

        [TestMethod]
        public void Upload_Value_IsEncoded()
        {
            var vm = NewVm();
            vm.Memo = Hostile;

            var html = Render(new UploadTagHelper { Field = Field(vm, x => x.Memo) }, "wt:upload", FormItems(vm, "wtForm_x"));

            AssertContains(html, "name='Memo' value='" + HostileEncoded + "'");
            AssertNotContains(html, "value='a'b", "value='a\"b");
        }

        [TestMethod]
        public void Upload_RequiredMessage_IsEncoded()
        {
            var vm = new HostileLabelVM();

            var html = Render(new UploadTagHelper { Field = Field(vm, x => x.PhotoId) }, "wt:upload", FormItems(vm, "wtForm_x"));

            AssertContains(html, "lay-reqText=\"Validate." + HostileEncoded + "required\"");
            AssertNotContains(html, "lay-reqText=\"Validate.a'b");
        }

        [TestMethod]
        public void MultiUpload_RequiredMessage_IsEncoded()
        {
            var vm = new HostileLabelVM { Files = new List<TestSubFile>() };

            var html = Render(new MultiUploadTagHelper { Field = Field(vm, x => x.Files) }, "wt:multiupload", FormItems(vm, "wtForm_x"));

            AssertContains(html, "lay-reqText=\"Validate." + HostileEncoded + "required\"");
            AssertNotContains(html, "lay-reqText=\"Validate.a'b");
        }

        // set through TagHelperOutput.Attributes: the Razor runtime encodes these, nothing to double encode
        [TestMethod]
        public void TextBox_RequiredMessage_AttributeIsEncodedByOutputAndNotTwice()
        {
            var vm = new HostileLabelVM();

            var html = Render(new TextBoxTagHelper { Field = Field(vm, x => x.Title) }, "wt:textbox");

            AssertContains(html, "lay-reqText=\"Validate.a&#x27;b&quot;c&lt;d&gt;&amp;erequired\"");
            AssertNotContains(html, "&amp;amp;", "&amp;#39;", "&amp;quot;");
        }

        #endregion

        #region button / grid

        [TestMethod]
        public void Button_Icon_IsEncoded()
        {
            var html = Render(new ButtonTagHelper { Id = "btn2", Text = "Add", Icon = Hostile }, "wt:button");

            AssertContains(html, "<i class=\"" + HostileEncoded + "\"></i> Add");
            AssertNotContains(html, "<i class=\"a'b");
        }

        [TestMethod]
        public void Grid_DetailGridPrefix_IsEncoded()
        {
            var listVm = new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext(), DetailGridPrix = Hostile };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            AssertContains(html, "value=\"" + HostileEncoded + "\"/>");
            AssertNotContains(html, "value=\"a'b");
        }

        [TestMethod]
        public void Grid_DetailGridPrefix_OrdinaryValueIsUnchanged()
        {
            var listVm = new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext(), DetailGridPrix = "Entity.Items" };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            AssertContains(html, "<input type=\"hidden\" name=\"Vm.DetailGridPrix\" value=\"Entity.Items\"/>");
        }

        private class ActionAttributesListVM : LayuiSchoolListVM
        {
            public string Query { get; set; }
            public string ButtonClass { get; set; }
            public string Icon { get; set; }
            public string ButtonId { get; set; }

            protected override List<GridAction> InitGridAction()
            {
                var action = new GridAction
                {
                    Name = "Edit",
                    ControllerName = "School",
                    ActionName = "doEdit",
                    QueryString = Query,
                    ButtonClass = ButtonClass,
                    IconCls = Icon,
                    ButtonId = ButtonId,
                    ShowInRow = true,
                    ParameterType = GridActionParameterTypesEnum.NoId,
                };
                return new List<GridAction> { action };
            }
        }

        [TestMethod]
        public void Grid_ActionAttributes_AreEncoded()
        {
            var listVm = new ActionAttributesListVM
            {
                Wtm = QuickDebugContext(),
                Query = "?a='1'&b=\"2\"<x>",
                ButtonClass = Hostile,
                Icon = Hostile,
            };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            // row button: class and lay-event attributes
            AssertContains(html,
                "<a class=\"layui-btn " + HostileEncoded + " layui-btn-xs\" lay-event=\"SchooldoEdit?a=&#39;1&#39;&amp;b=&quot;2&quot;&lt;x&gt;\"",
                "<i class=\"" + HostileEncoded + "\"></i>");
            AssertNotContains(html, "class=\"layui-btn a'b", "lay-event=\"SchooldoEdit?a='1'", "<i class=\"a'b");
        }

        [TestMethod]
        public void Grid_ActionEvent_IsEscapedInToolbarHandlerAndSwitch()
        {
            var listVm = new ActionAttributesListVM { Wtm = QuickDebugContext(), Query = "?a='1'\"2\"</script>" };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            const string escaped = "SchooldoEdit?a=\\u00271\\u0027\\u00222\\u0022\\u003C/script\\u003E";
            AssertContains(html, "({event:'" + escaped + "'});", "case '" + escaped + "':{");
            AssertNotContains(html, "event:'SchooldoEdit?a='1'", "case 'SchooldoEdit?a='1'");
        }

        [TestMethod]
        public void Grid_ActionAttributes_OrdinaryValuesAreUnchanged()
        {
            var listVm = new ActionAttributesListVM
            {
                Wtm = QuickDebugContext(),
                Query = "?id={{d.ID}}",
                ButtonClass = "layui-btn-danger",
                Icon = "layui-icon layui-icon-edit",
            };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            AssertContains(html,
                "<a class=\"layui-btn layui-btn-danger layui-btn-xs\" lay-event=\"SchooldoEdit?id={{d.ID}}\">Edit</a>",
                "<i class=\"layui-icon layui-icon-edit\"></i>",
                "wtToolBarFunc_",
                "({event:'SchooldoEdit?id={{d.ID}}'});",
                "case 'SchooldoEdit?id={{d.ID}}':{");
        }

        #endregion

        #region UI service

        [TestMethod]
        public void UIService_CheckBoxAndRadio_NameValueAndTitle_AreEncoded()
        {
            var service = new LayuiUIService();

            var check = service.MakeCheckBox(true, Hostile, Hostile, Hostile);
            var radio = service.MakeRadio(false, Hostile, Hostile, Hostile, true);

            foreach (var html in new[] { check, radio })
            {
                AssertContains(html, "name='" + HostileEncoded + "'", "value='" + HostileEncoded + "'", "title='" + HostileEncoded + "'");
                AssertNotContains(html, "name='a'b", "value='a'b", "title='a'b", "c<d>");
            }
        }

        [TestMethod]
        public void UIService_CheckBoxAndRadio_OrdinaryValuesAreUnchanged()
        {
            var service = new LayuiUIService();

            AssertContains(service.MakeCheckBox(true, Cjk, "Entity.Flag", "1"),
                "<input lay-skin='primary' type='checkbox' name='Entity.Flag' id='Entity_Flag' value='1' title='" + Cjk + "'  checked");
            AssertContains(service.MakeRadio(false, Cjk, "Entity.Flag", "1"),
                "<input lay-skin='primary' type='radio' name='Entity.Flag' id='Entity_Flag' value='1' title='" + Cjk + "'");
        }

        [TestMethod]
        public void UIService_Combo_NameAndOptionValues_AreEncoded()
        {
            var items = new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Text = "plain", Value = Hostile },
                new ComboSelectListItem { Text = "other", Value = "2" },
            };

            var html = new LayuiUIService().MakeCombo(Hostile, items, selectedValue: "2");

            AssertContains(html, "<select name='" + HostileEncoded + "' id='" + HostileEncoded + "'", "<option value='" + HostileEncoded + "'>plain</option>", "<option value='2' selected>other</option>");
            AssertNotContains(html, "name='a'b", "value='a'b");
        }

        [TestMethod]
        public void UIService_TextBoxAndDateTime_NameAndValue_AreEncoded()
        {
            var service = new LayuiUIService();

            var text = service.MakeTextBox(Hostile, Hostile);
            var date = service.MakeDateTime(Hostile, "2024-03-04");

            AssertContains(text, "name='" + HostileEncoded + "'", "value='" + HostileEncoded + "'");
            AssertContains(date, "name='" + HostileEncoded + "'", "id='" + HostileEncoded + "'", "value='2024-03-04'");
            AssertNotContains(text + date, "name='a'b", "value='a'b", "id='a'b");
        }

        [TestMethod]
        public void UIService_Buttons_ClassStyleAndId_AreEncoded()
        {
            var service = new LayuiUIService();

            var dialog = service.MakeDialogButton(ButtonTypesEnum.Button, "/a", "t", 1, 2, buttonID: Hostile, buttonClass: Hostile, style: Hostile);
            var link = service.MakeScriptButton(ButtonTypesEnum.Link, "t", "x()", Hostile, buttonClass: Hostile, style: Hostile);
            var button = service.MakeScriptButton(ButtonTypesEnum.Button, "t", "x()", "b1", buttonClass: Hostile, style: Hostile);
            var download = service.MakeDownloadButton(ButtonTypesEnum.Button, Guid.NewGuid(), "t", Hostile, Hostile, Hostile);
            var downloadLink = service.MakeDownloadButton(ButtonTypesEnum.Link, Guid.NewGuid(), "t", Hostile, Hostile, Hostile);
            var view = service.MakeViewButton(ButtonTypesEnum.Img, Guid.NewGuid(), "t", _DONOT_USE_CS: Hostile, buttonClass: Hostile, style: Hostile);

            foreach (var html in new[] { dialog, link, button, download, downloadLink, view })
            {
                AssertContains(html, "class='", HostileEncoded);
                AssertNotContains(html, "class='a'b", "style='a'b", "id='a'b", "CS=a'b");
            }
            AssertContains(download, "class='layui-btn " + HostileEncoded + "' href='/_Framework/GetFile/");
            AssertContains(downloadLink, "?_DONOT_USE_CS=" + HostileEncoded + "'>");
            AssertContains(view, "?_DONOT_USE_CS=" + HostileEncoded + "&width=");
        }

        [TestMethod]
        public void UIService_Buttons_OrdinaryValuesAreUnchanged()
        {
            var service = new LayuiUIService();
            var id = Guid.NewGuid();

            Assert.AreEqual(
                "<a  style='color:blue;cursor:pointer' class='' href='/_Framework/GetFile/" + id + "?_DONOT_USE_CS=default'>" + Cjk + "</a>",
                service.MakeDownloadButton(ButtonTypesEnum.Link, id, Cjk));
            Assert.AreEqual(
                "<a  style='' class='layui-btn layui-btn-primary layui-btn-xs' href='/_Framework/GetFile/" + id + "?_DONOT_USE_CS=default'>" + Cjk + "</a>",
                service.MakeDownloadButton(ButtonTypesEnum.Button, id, Cjk));
            AssertContains(service.MakeScriptButton(ButtonTypesEnum.Button, Cjk, "x()", "b1", buttonClass: "my-class", style: "width:10px"),
                "<a id='b1' style='width:10px' class='layui-btn my-class'>" + Cjk + "</a>");
        }

        #endregion
    }
}
