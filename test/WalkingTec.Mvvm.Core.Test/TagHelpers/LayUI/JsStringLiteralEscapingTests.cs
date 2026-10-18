using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Values taken from data or configuration that are embedded in a JavaScript string literal of the
    /// generated script must be escaped, while ordinary values must be rendered exactly as before.
    /// </summary>
    [TestClass]
    public class JsStringLiteralEscapingTests : LayUITagHelperTestBase
    {
        // generic value that would terminate a quoted literal and a script element
        private const string Hostile = "a'b\"c</script>";
        // its escaped form inside a JavaScript string literal
        private const string HostileEscaped = "a\\u0027b\\u0022c\\u003C/script\\u003E";

        public class LayuiQuotedVM : BaseVM
        {
            [Display(Name = "It's \"q\"")]
            [Required]
            public Guid? QuotedId { get; set; }
        }

        private static LayuiTestVM NewVm() => new LayuiTestVM();

        private static void AssertEscaped(string html, string before, string after)
        {
            AssertContains(html, before + HostileEscaped + after);
            AssertNotContains(html, before + "a'b", before + "a\"b");
            AssertNotContains(html, "c</script>" + after);
        }

        #region data / configuration values are escaped

        [TestMethod]
        public void Form_ServerSideError_IsEscapedAndMarkupStaysInert()
        {
            var vm = new LayuiTestVM { Wtm = MockWtmContext.CreateWtmContext() };
            vm.MSD.AddModelError("Name", Hostile);

            var html = Render(new FormTagHelper { Vm = Self(vm, "Vm") }, "wt:form");

            // markup is HTML-encoded first, then the text is escaped for the JavaScript literal
            AssertContains(html, "<label style='color:red'>a\\u0027b\\u0022c\\u0026lt;/script\\u0026gt;</label>");
            AssertNotContains(html, "c</script>");
            AssertNotContains(html, "&lg;", "&rg;");
        }

        [TestMethod]
        public void ComboBox_ItemUrlEmptyTextAndTriggerUrl_AreEscaped()
        {
            var helper = new ComboBoxTagHelper(Options(), null)
            {
                Field = Field(NewVm(), x => x.SchoolId),
                ItemUrl = Hostile,
                EmptyText = Hostile,
                LinkId = "other",
                TriggerUrl = Hostile,
            };

            var html = Render(helper, "wt:combobox");

            AssertContains(html, "ff.LoadComboItems('combo','" + HostileEscaped + "'");
            AssertContains(html, "tips:'" + HostileEscaped + "'");
            AssertContains(html, "var u = \"" + HostileEscaped + "\";");
            AssertNotContains(html, "'a'b", "tips:'a'b", "var u = \"a'b");
        }

        [TestMethod]
        public void ComboBox_RequiredDisplayName_IsEscapedInLayReqText()
        {
            var vm = new LayuiQuotedVM();
            var helper = new ComboBoxTagHelper(Options(), null) { Field = Field(vm, x => x.QuotedId) };

            var html = Render(helper, "wt:combobox");

            AssertContains(html, "layReqText:'Validate.It\\u0027s \\u0022q\\u0022required'");
        }

        [TestMethod]
        public void CheckBox_ItemUrl_IsEscaped()
        {
            var vm = NewVm();
            vm.Names = new List<string>();
            var helper = new CheckBoxTagHelper { Field = Field(vm, x => x.Names), ItemUrl = Hostile };

            AssertEscaped(Render(helper, "wt:checkbox"), "ff.LoadComboItems('checkbox','", "'");
        }

        [TestMethod]
        public void Radio_ItemUrl_IsEscaped()
        {
            var helper = new RadioTagHelper { Field = Field(NewVm(), x => x.Kind), ItemUrl = Hostile };

            AssertEscaped(Render(helper, "wt:radio"), "ff.LoadComboItems('radio','", "'");
        }

        [TestMethod]
        public void Transfer_ItemUrlTitlesAndPlaceholders_AreEscaped()
        {
            var vm = NewVm();
            vm.Names = new List<string>();
            var helper = new TransferTagHelper
            {
                Field = Field(vm, x => x.Names),
                ItemUrl = Hostile,
                LeftTitle = Hostile,
                RightTitle = Hostile,
                NonePlaceholder = Hostile,
                SearchNonePlaceholder = Hostile,
            };

            var html = Render(helper, "wt:transfer");

            AssertContains(html,
                "ff.LoadComboItems('transfer','" + HostileEscaped + "'",
                $"['{HostileEscaped}','{HostileEscaped}']",
                $"text:{{none:'{HostileEscaped}',searchNone:'{HostileEscaped}'}}");
        }

        [TestMethod]
        public void RichTextBox_UploadUrl_IsEscaped()
        {
            var vm = NewVm();
            var helper = new RichTextBoxTagHelper { Field = Field(vm, x => x.Html), UploadUrl = Hostile };

            AssertEscaped(Render(helper, "wt:richtextbox", FormItems(vm)), "url: '", "?'");
        }

        [TestMethod]
        public void Button_ConfirmText_IsEscaped()
        {
            var helper = new ButtonTagHelper { Id = "btn", Text = "Del", Click = "del()", ConfirmTxt = Hostile };

            var html = Render(helper, "wt:button");

            AssertContains(html, "layer.confirm('" + HostileEscaped + "'");
            AssertNotContains(html, "layer.confirm('a'b");
        }

        private class ActionListVM : LayuiSchoolListVM
        {
            public string DialogTitle { get; set; }
            public string Prompt { get; set; }

            protected override List<GridAction> InitGridAction()
            {
                return new List<GridAction>
                {
                    new GridAction
                    {
                        Name = "Edit",
                        ActionName = "doEdit",
                        ShowDialog = true,
                        DialogTitle = DialogTitle,
                        PromptMessage = Prompt,
                        ParameterType = GridActionParameterTypesEnum.NoId,
                    },
                };
            }
        }

        [TestMethod]
        public void Grid_ActionDialogTitleAndPromptMessage_AreEscaped()
        {
            var listVm = new ActionListVM { Wtm = MockWtmContext.CreateWtmContext(), DialogTitle = Hostile, Prompt = Hostile };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            AssertContains(html, "tempUrl,'", ",'" + HostileEscaped + "',null,null", "layer.confirm('" + HostileEscaped + "'");
            AssertNotContains(html, "layer.confirm('a'b", ",'a'b");
        }

        #endregion

        #region ordinary values are unchanged

        [TestMethod]
        public void OrdinaryValues_AreRenderedExactlyAsBefore()
        {
            var combo = new ComboBoxTagHelper(Options(), null)
            {
                Field = Field(NewVm(), x => x.SchoolId),
                ItemUrl = "/api/schools",
                EmptyText = "請選擇",
                LinkId = "other",
                TriggerUrl = "/School/Trigger",
            };
            var comboHtml = Render(combo, "wt:combobox");
            AssertContains(comboHtml,
                "ff.LoadComboItems('combo','/api/schools'",
                "tips:'請選擇'",
                "var u = \"/School/Trigger\";");

            var button = new ButtonTagHelper { Id = "btn", Text = "Del", Click = "del()", ConfirmTxt = "確定刪除嗎？" };
            AssertContains(Render(button, "wt:button"), "layer.confirm('確定刪除嗎？'");

            var vm = new LayuiTestVM { Wtm = MockWtmContext.CreateWtmContext() };
            vm.MSD.AddModelError("Name", "名稱不可重複");
            AssertContains(Render(new FormTagHelper { Vm = Self(vm, "Vm") }, "wt:form"), "<label style='color:red'>名稱不可重複</label>");

            var transfer = new TransferTagHelper { Field = Field(NewVm(), x => x.Names), LeftTitle = "待選", RightTitle = "已選" };
            AssertContains(Render(transfer, "wt:transfer"), "['待選','已選']");

            var required = new ComboBoxTagHelper(Options(), null) { Field = Field(NewVm(), x => x.SchoolId) };
            Assert.IsNotNull(Render(required, "wt:combobox"));
        }

        [TestMethod]
        public void RequiredField_PlainDisplayName_IsRenderedExactlyAsBefore()
        {
            var vm = new LayuiTestVM();
            var helper = new ComboBoxTagHelper(Options(), null) { Field = Field(vm, x => x.Name) };

            AssertContains(Render(helper, "wt:combobox"), "layReqText:'Validate.User Namerequired'");
        }

        #endregion
    }
}
