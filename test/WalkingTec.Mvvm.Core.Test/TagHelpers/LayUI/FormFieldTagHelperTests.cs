using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Smoke tests for the field-bound (wt:textbox, wt:combobox, ...) LayUI tag helpers.
    /// They assert stable markers of the CURRENT output (layui classes, attributes, element names,
    /// generated script, field id/name binding), not full-HTML snapshots.
    /// </summary>
    [TestClass]
    public class FormFieldTagHelperTests : LayUITagHelperTestBase
    {
        // Utils.GetIdByName("LayuiTestVM.Name") == "LayuiTestVM_Name"
        private const string NameId = "LayuiTestVM_Name";

        private static LayuiTestVM NewVm() => new LayuiTestVM();

        [TestMethod]
        public void TextBox_RendersLayuiInputBoundToField()
        {
            var vm = NewVm();
            vm.Name = "Alice";
            var helper = new TextBoxTagHelper { Field = Field(vm, x => x.Name), EmptyText = "type here" };

            var html = Render(helper, "wt:textbox");

            AssertContains(html,
                "<input", "type=\"text\"", "class=\"layui-input\"",
                "name=\"Name\"", "wtm-name=\"Name\"", $"id=\"{NameId}\"",
                $"lay-filter=\"{NameId}filter\"",
                "value=\"Alice\"", "placeholder=\"type here\"",
                "layui-form-item", "layui-form-label");
        }

        [TestMethod]
        public void TextBox_RequiredField_AddsLayVerifyAndLabelMark()
        {
            var helper = new TextBoxTagHelper { Field = Field(NewVm(), x => x.Name) };

            var html = Render(helper, "wt:textbox");

            AssertContains(html, "lay-verify=\"required\"", "lay-reqText=", "User Name:", "<font color='red'>*</font>");
        }

        [TestMethod]
        public void TextBox_OptionalField_HasNoLayVerify()
        {
            var helper = new TextBoxTagHelper { Field = Field(NewVm(), x => x.Memo) };

            var html = Render(helper, "wt:textbox");

            AssertContains(html, "name=\"Memo\"");
            AssertNotContains(html, "lay-verify");
        }

        [TestMethod]
        public void TextBox_Password_UsesPasswordType()
        {
            var helper = new TextBoxTagHelper { Field = Field(NewVm(), x => x.Memo), IsPassword = true };

            AssertContains(Render(helper, "wt:textbox"), "type=\"password\"");
        }

        [TestMethod]
        public void TextBox_EncodesValueInAttribute()
        {
            var vm = NewVm();
            vm.Memo = "\"><script>alert(1)</script>";
            var helper = new TextBoxTagHelper { Field = Field(vm, x => x.Memo) };

            var html = Render(helper, "wt:textbox");

            AssertNotContains(html, "<script>alert(1)</script>");
        }

        [TestMethod]
        public void TextArea_RendersLayuiTextarea()
        {
            var vm = NewVm();
            vm.Memo = "line1";
            var helper = new TextAreaTagHelper { Field = Field(vm, x => x.Memo) };

            var html = Render(helper, "wt:textarea");

            AssertContains(html, "<textarea", "class=\"layui-textarea\"", "name=\"Memo\"", "id=\"LayuiTestVM_Memo\"", "line1", "</textarea>");
        }

        [TestMethod]
        public void Hidden_RendersHiddenInputWithValue()
        {
            var vm = NewVm();
            vm.Memo = "secret";
            var helper = new HiddenTagHelper { Field = Field(vm, x => x.Memo) };

            var html = Render(helper, "wt:hidden");

            AssertContains(html, "<input", "type=\"hidden\"", "name=\"Memo\"", "value=\"secret\"", "id=\"LayuiTestVM_Memo\"");
        }

        [TestMethod]
        public void Hidden_ListValue_RendersOneInputPerItem()
        {
            var vm = NewVm();
            vm.Tags = new List<string> { "a", "b" };
            var helper = new HiddenTagHelper { Field = Field(vm, x => x.Tags) };

            var html = Render(helper, "wt:hidden");

            AssertContains(html, "value=\"a\"", "value=\"b\"", "name=\"Tags\"");
        }

        [TestMethod]
        public void Display_RendersReadonlyLabel()
        {
            var vm = NewVm();
            vm.Name = "Bob";
            vm.Birthday = new DateTime(2000, 1, 2);
            var name = new DisplayTagHelper { Field = Field(vm, x => x.Name) };
            var birthday = new DisplayTagHelper { Field = Field(vm, x => x.Birthday) };

            var nameHtml = Render(name, "wt:display");
            var birthdayHtml = Render(birthday, "wt:display");

            AssertContains(nameHtml, "<label", "layui-form-label", "Bob");
            AssertContains(birthdayHtml, "2000-01-02");
        }

        [TestMethod]
        public void Display_Boolean_UsesLocalizedYesNo()
        {
            var vm = NewVm();
            vm.IsActive = true;
            var helper = new DisplayTagHelper { Field = Field(vm, x => x.IsActive) };

            AssertContains(Render(helper, "wt:display"), "Sys.Yes");
        }

        [TestMethod]
        public void ComboBox_Enum_RendersXmSelectWithEnumItems()
        {
            var vm = NewVm();
            vm.Kind = SchoolTypeEnum.PRI;
            var helper = new ComboBoxTagHelper(Options(), null) { Field = Field(vm, x => x.Kind) };

            var html = Render(helper, "wt:combobox");

            AssertContains(html,
                "wtm-ctype=\"combo\"", "wtm-multi=\"false\"", "wtm-name=\"Kind\"", "id=\"LayuiTestVM_Kind\"",
                "name=\"_DONOTUSE_Kind\"",
                "xmSelect.render", "el: '#LayuiTestVM_Kind'", "name:'Kind'");
        }

        [TestMethod]
        public void ComboBox_ListField_IsMultiSelect()
        {
            var vm = NewVm();
            vm.AllSchools = new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Text = "first", Value = "1" },
                new ComboSelectListItem { Text = "second", Value = "2" },
            };
            vm.SchoolIds = new List<Guid>();
            var helper = new ComboBoxTagHelper(Options(), null)
            {
                Field = Field(vm, x => x.SchoolIds),
                Items = Field(vm, x => x.AllSchools),
            };

            var html = Render(helper, "wt:combobox");

            AssertContains(html, "wtm-multi=\"true\"", "xmSelect.render", "first", "second", "'ALL', 'REVERSE', 'CLEAR'");
        }

        [TestMethod]
        public void ComboBox_ItemUrl_EmitsAjaxLoadScript()
        {
            var helper = new ComboBoxTagHelper(Options(), null) { Field = Field(NewVm(), x => x.SchoolId), ItemUrl = "/api/schools" };

            AssertContains(Render(helper, "wt:combobox"), "ff.LoadComboItems('combo','/api/schools'");
        }

        [TestMethod]
        public void CheckBox_Enum_RendersCheckboxGroup()
        {
            var vm = NewVm();
            vm.Names = new List<string>();
            vm.AllSchools = new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Text = "first", Value = "1" },
                new ComboSelectListItem { Text = "second", Value = "2" },
            };
            var helper = new CheckBoxTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) };

            var html = Render(helper, "wt:checkbox");

            AssertContains(html,
                "div-for=\"checkbox\"", "wtm-ctype=\"checkbox\"", "wtm-name=\"Names\"",
                "type=\"checkbox\"", "name=\"Names\"", "title=\"first\"", "value=\"2\"",
                "name=\"_DONOTUSE_Names\"");
        }

        [TestMethod]
        public void CheckBox_ChangeFunc_AddsLayFilterAndHandlerScript()
        {
            var vm = NewVm();
            vm.Names = new List<string>();
            vm.AllSchools = new List<ComboSelectListItem> { new ComboSelectListItem { Text = "x", Value = "1" } };
            var helper = new CheckBoxTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools), ChangeFunc = "onPick" };

            var html = Render(helper, "wt:checkbox");

            AssertContains(html, "form.on('checkbox(", "onPick(data)", "wtm-cf=\"onPick\"");
        }

        [TestMethod]
        public void Radio_Enum_RendersRadioGroupWithSelection()
        {
            var vm = NewVm();
            vm.Kind = SchoolTypeEnum.PUB;
            var helper = new RadioTagHelper { Field = Field(vm, x => x.Kind) };

            var html = Render(helper, "wt:radio");

            AssertContains(html, "div-for=\"radio\"", "wtm-ctype=\"radio\"", "type=\"radio\"", "name=\"Kind\"", "checked");
        }

        [TestMethod]
        public void Switch_RendersLayuiSwitchCheckbox()
        {
            var vm = NewVm();
            vm.IsActive = true;
            var helper = new SwitchTagHelper { Field = Field(vm, x => x.IsActive) };

            var html = Render(helper, "wt:switch");

            AssertContains(html, "type=\"checkbox\"", "lay-skin=\"switch\"", "name=\"IsActive\"", "lay-text=\"Sys.Yes|Sys.No\"", "checked", "name=\"_DONOTUSE_IsActive\"");
        }

        [TestMethod]
        public void Switch_FalseValue_IsNotChecked()
        {
            var vm = NewVm();
            vm.IsActive = false;
            var helper = new SwitchTagHelper { Field = Field(vm, x => x.IsActive) };

            AssertNotContains(Render(helper, "wt:switch"), "checked");
        }

        [TestMethod]
        public void DateTime_RendersInputAndLaydateScript()
        {
            var vm = NewVm();
            vm.Birthday = new DateTime(2020, 5, 6, 7, 8, 9);
            var helper = new DateTimeTagHelper(Options()) { Field = Field(vm, x => x.Birthday), Type = DateTimeTypeEnum.DateTime };

            var html = Render(helper, "wt:datetime");

            AssertContains(html,
                "<input", "type=\"text\"", "class=\"layui-input\"", "name=\"Birthday\"",
                "value=\"2020-05-06 07:08:09\"",
                "layui.use(['laydate']", "laydate.render", "elem: '#LayuiTestVM_Birthday'", "type: 'datetime'");
        }

        [TestMethod]
        public void DateTime_DateType_FormatsDateOnly()
        {
            var vm = NewVm();
            vm.Birthday = new DateTime(2020, 5, 6, 7, 8, 9);
            var helper = new DateTimeTagHelper(Options()) { Field = Field(vm, x => x.Birthday), Type = DateTimeTypeEnum.Date };

            AssertContains(Render(helper, "wt:datetime"), "value=\"2020-05-06\"", "type: 'date'");
        }

        [TestMethod]
        public void Selector_RendersReadonlyInputHiddenValuesAndOpenDialogScript()
        {
            var vm = NewVm();
            var schoolVm = new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() };
            var schoolField = new School();
            var helper = new SelectorTagHelper
            {
                Field = Field(vm, x => x.SchoolId),
                ListVM = Self(schoolVm, "Selector"),
                TextBind = Field(schoolField, x => x.SchoolName),
            };

            var html = Render(helper, "wt:selector", FormItems(vm, "wtForm_x"));

            AssertContains(html,
                "<input", "readonly", "id=\"LayuiTestVM_SchoolId_Display\"", "name=\"SchoolId_Display\"",
                "name=\"_DONOTUSE_SchoolId\"", "id='LayuiTestVM_SchoolId_Select'",
                "ff.OpenDialog2('/_Framework/Selector'", "_DONOT_USE_VMNAME", "_DONOT_USE_KFIELD");
        }

        [TestMethod]
        public void Selector_WithoutListVm_Throws()
        {
            var helper = new SelectorTagHelper { Field = Field(NewVm(), x => x.SchoolId), TextBind = Field(new School(), x => x.SchoolName) };

            Assert.ThrowsExactly<Exception>(() => Render(helper, "wt:selector", FormItems(NewVm())));
        }

        [TestMethod]
        public void Upload_RendersSelectButtonAndLayuiUploadScript()
        {
            var vm = NewVm();
            var helper = new UploadTagHelper { Field = Field(vm, x => x.PhotoId), UploadType = UploadTypeEnum.ImageFile };

            var html = Render(helper, "wt:upload", FormItems(vm, "wtForm_x"));

            AssertContains(html, "<button", "layui-btn layui-btn-sm", "id=\"LayuiTestVM_PhotoIdbutton\"", "Sys.Select",
                "/_Framework/UploadImage", "layui.use(['upload'", "name='PhotoId'");
        }

        [TestMethod]
        public void Upload_Disabled_HidesSelectButton()
        {
            var vm = NewVm();
            var helper = new UploadTagHelper { Field = Field(vm, x => x.PhotoId), Disabled = true };

            AssertContains(Render(helper, "wt:upload", FormItems(vm)), "display:none");
        }

        [TestMethod]
        public void MultiUpload_RendersSelectButtonAndUploadScript()
        {
            var vm = NewVm();
            vm.Files = new List<TestSubFile> { new TestSubFile { FileId = Guid.NewGuid() } };
            var helper = new MultiUploadTagHelper { Field = Field(vm, x => x.Files) };

            var html = Render(helper, "wt:multiupload", FormItems(vm, "wtForm_x"));

            AssertContains(html, "<button", "layui-btn layui-btn-sm", "id=\"LayuiTestVM_Filesbutton\"", "/_Framework/Upload", "layui.use(['upload'");
        }

        [TestMethod]
        public void RichTextBox_RendersHiddenTextareaAndLayeditScript()
        {
            var vm = NewVm();
            vm.Html = "<b>hi</b>";
            var helper = new RichTextBoxTagHelper { Field = Field(vm, x => x.Html) };

            var html = Render(helper, "wt:richtextbox", FormItems(vm));

            AssertContains(html, "<textarea", "isrich=\"1\"", "display:none", "layui.use('layedit'", "layedit.build('LayuiTestVM_Html'",
                "/_framework/UploadForLayUIRichTextBox");
        }

        [TestMethod]
        public void ColorPicker_RendersHiddenInputAndColorpickerScript()
        {
            var vm = NewVm();
            vm.Color = "#ff0000";
            var helper = new ColorPickerTagHelper { Field = Field(vm, x => x.Color) };

            var html = Render(helper, "wt:colorpicker");

            AssertContains(html, "id=\"cp_LayuiTestVM_Color\"", "type='hidden'", "name='Color'", "value='#ff0000'",
                "layui.use('colorpicker'", "colorpicker.render", "elem: '#cp_LayuiTestVM_Color'");
        }

        [TestMethod]
        public void Slider_RendersSliderContainerAndScript()
        {
            var vm = NewVm();
            vm.Score = 30;
            var helper = new SliderTagHelper { Field = Field(vm, x => x.Score), Min = 0, Max = 100 };

            var html = Render(helper, "wt:slider");

            AssertContains(html, "id=\"_sliderLayuiTestVM_Score\"", "name='Score'", "value='30'",
                "layui.use(['slider']", "slider.render", ",min:0", ",max:100");
        }

        [TestMethod]
        public void Slider_RangeMode_BindsSecondField()
        {
            var vm = NewVm();
            vm.Score = 10;
            vm.ScoreMax = 90;
            var helper = new SliderTagHelper { Field = Field(vm, x => x.Score), Field1 = Field(vm, x => x.ScoreMax) };

            AssertContains(Render(helper, "wt:slider"), "name='Score'", "name='ScoreMax'", ",range:true");
        }

        [TestMethod]
        public void Transfer_RendersTransferContainerAndScript()
        {
            var vm = NewVm();
            vm.Names = new List<string> { "b" };
            vm.AllSchools = new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Text = "A", Value = "a" },
                new ComboSelectListItem { Text = "B", Value = "b" },
            };
            var helper = new TransferTagHelper { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.AllSchools) };

            var html = Render(helper, "wt:transfer");

            AssertContains(html, "wtm-ctype=\"transfer\"", "wtm-name=\"Names\"", "id=\"LayuiTestVM_Names\"",
                "layui.use(['transfer']", "transfer.render", "\"title\":\"A\"", "name=\"_DONOTUSE_Names\"");
        }

        [TestMethod]
        public void Tree_RendersXmSelectTreeContainer()
        {
            var vm = NewVm();
            vm.Menus = new List<TreeSelectListItem>
            {
                new TreeSelectListItem { Text = "root", Value = "1", Children = new List<TreeSelectListItem> { new TreeSelectListItem { Text = "leaf", Value = "2" } } },
            };
            vm.Names = new List<string>();
            var helper = new TreeTagHelper(Options()) { Field = Field(vm, x => x.Names), Items = Field(vm, x => x.Menus) };

            var html = Render(helper, "wt:tree");

            AssertContains(html, "wtm-ctype=\"tree\"", "wtm-multi=\"true\"", "id=\"LayuiTestVM_Names\"", "xmSelect.render", "tree:");
        }
    }
}
