using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.WorkFlow;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issue #24 (part C): opt-in HTML encoding of text rendered as HTML content. With the default
    /// setting (UIOptions.EncodeHtmlContent = false) the text is emitted as is, so pages that
    /// intentionally render HTML keep working; with the option on it is encoded.
    /// </summary>
    [TestClass]
    public class HtmlContentEncodingTests : LayUITagHelperTestBase
    {
        private const string Raw = "<b>x</b>";
        private const string Encoded = "&lt;b&gt;x&lt;/b&gt;";

        private static IOptionsMonitor<Configs> Off() => Options();

        private static IOptionsMonitor<Configs> On() => Options(c => c.UIOptions.EncodeHtmlContent = true);

        private static LayuiTestVM NewVm() => new LayuiTestVM();

        #region option

        [TestMethod]
        public void Option_DefaultsToFalse()
        {
            Assert.IsFalse(new Configs().UIOptions.EncodeHtmlContent);
            Assert.IsFalse(new UIOptions().EncodeHtmlContent);
        }

        [TestMethod]
        public void Option_IsReadFromRequestServices_WhenNoExplicitSource()
        {
            var services = new ServiceCollection().AddSingleton(On()).BuildServiceProvider();
            var viewContext = new ViewContext { HttpContext = new DefaultHttpContext { RequestServices = services } };

            var on = Render(new CardTagHelper { Title = Raw, ViewContext = viewContext }, "wt:card");
            var off = Render(new CardTagHelper { Title = Raw }, "wt:card");

            AssertContains(on, ">" + Encoded + "</div>");
            AssertContains(off, ">" + Raw + "</div>");
        }

        #endregion

        #region display

        [TestMethod]
        public void Display_Default_EmitsValueAsHtml()
        {
            var vm = NewVm();
            vm.Memo = Raw;

            var html = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo), ConfigsMonitor = Off() }, "wt:display");
            var noConfig = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo) }, "wt:display");

            AssertContains(html, ">" + Raw + "</label>");
            Assert.AreEqual(html, noConfig);
        }

        [TestMethod]
        public void Display_OptionOn_EncodesValue()
        {
            var vm = NewVm();
            vm.Memo = Raw;

            var html = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo), ConfigsMonitor = On() }, "wt:display");

            AssertContains(html, ">" + Encoded + "</label>");
            AssertNotContains(html, Raw);
        }

        [TestMethod]
        public void Display_OptionOn_KeepsCjkAndPlainText()
        {
            var vm = NewVm();
            vm.Memo = "第一所學校 & 測試";

            var html = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo), ConfigsMonitor = On() }, "wt:display");

            AssertContains(html, ">第一所學校 &amp; 測試</label>");
        }

        [TestMethod]
        public void Display_DisplayText_IsEncodedWhenOn()
        {
            var vm = NewVm();
            var off = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo), DisplayText = Raw, ConfigsMonitor = Off() }, "wt:display");
            var on = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo), DisplayText = Raw, ConfigsMonitor = On() }, "wt:display");

            AssertContains(off, ">" + Raw + "</label>");
            AssertContains(on, ">" + Encoded + "</label>");
        }

        [TestMethod]
        public void Display_EncodeHtmlAttribute_OverridesTheGlobalOption()
        {
            var vm = NewVm();
            vm.Memo = Raw;

            var forceOn = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo), ConfigsMonitor = Off(), EncodeHtml = true }, "wt:display");
            var forceOff = Render(new DisplayTagHelper { Field = Field(vm, x => x.Memo), ConfigsMonitor = On(), EncodeHtml = false }, "wt:display");

            AssertContains(forceOn, ">" + Encoded + "</label>");
            AssertContains(forceOff, ">" + Raw + "</label>");
        }

        #endregion

        #region selector

        private class NoDbSchoolListVM : LayuiSchoolListVM
        {
            public override void DoSearch()
            {
                EntityList = new List<School> { new School { SchoolName = Raw } };
                IsSearched = true;
            }
        }

        private static SelectorTagHelper NewSelector(LayuiTestVM vm, IOptionsMonitor<Configs> configs, bool display, string buttonText = null)
        {
            var schoolVm = new NoDbSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() };
            return new SelectorTagHelper
            {
                Field = Field(vm, x => x.SchoolId),
                ListVM = Self(schoolVm, "Selector"),
                TextBind = Field(new School(), x => x.SchoolName),
                Display = display,
                SelectButtonText = buttonText,
                ConfigsMonitor = configs,
            };
        }

        [TestMethod]
        public void Selector_DisplayText_DefaultIsHtml_OnIsEncoded()
        {
            var vm = NewVm();
            vm.SchoolId = Guid.NewGuid();

            var off = Render(NewSelector(vm, Off(), true), "wt:selector", FormItems(vm, "wtForm_x"));
            var on = Render(NewSelector(vm, On(), true), "wt:selector", FormItems(vm, "wtForm_x"));

            AssertContains(off, ">" + Raw + "</label>");
            AssertContains(on, ">" + Encoded + "</label>");
        }

        [TestMethod]
        public void Selector_SelectButtonText_DefaultIsHtml_OnIsEncoded()
        {
            var vm = NewVm();

            var off = Render(NewSelector(vm, Off(), false, Raw), "wt:selector", FormItems(vm, "wtForm_x"));
            var on = Render(NewSelector(vm, On(), false, Raw), "wt:selector", FormItems(vm, "wtForm_x"));
            var onDefaultText = Render(NewSelector(vm, On(), false), "wt:selector", FormItems(vm, "wtForm_x"));

            AssertContains(off, "right:0px'>" + Raw + "</button>");
            AssertContains(on, "right:0px'>" + Encoded + "</button>");
            // the built-in default text is not data and is never changed
            AssertContains(onDefaultText, "right:0px'> . . . </button>");
        }

        #endregion

        #region titles / labels / buttons

        [TestMethod]
        public void Card_Title_DefaultIsHtml_OnIsEncoded()
        {
            foreach (var theme in new[] { CardThemeEnum.White, CardThemeEnum.Dark })
            {
                var off = Render(new CardTagHelper { Title = Raw, CardTheme = theme, ConfigsMonitor = Off() }, "wt:card");
                var on = Render(new CardTagHelper { Title = Raw, CardTheme = theme, ConfigsMonitor = On() }, "wt:card");

                AssertContains(off, ">" + Raw + "</div>");
                AssertContains(on, ">" + Encoded + "</div>");
                AssertNotContains(on, Raw);
            }
        }

        [TestMethod]
        public void TabHeader_Title_DefaultIsHtml_OnIsEncoded()
        {
            var off = Render(new TabHeaderTagHelper { Title = Raw, ConfigsMonitor = Off() }, "wt:tabheader");
            var on = Render(new TabHeaderTagHelper { Title = Raw, ConfigsMonitor = On() }, "wt:tabheader");

            AssertContains(off, ">" + Raw + "</li>");
            AssertContains(on, ">" + Encoded + "</li>");
        }

        [TestMethod]
        public void FieldSet_Legend_DefaultIsHtml_OnIsEncoded()
        {
            var off = Render(new FieldSetTagHelper { Title = Raw, ConfigsMonitor = Off() }, "wt:fieldset");
            var on = Render(new FieldSetTagHelper { Title = Raw, ConfigsMonitor = On() }, "wt:fieldset");

            AssertContains(off, "<legend>" + Raw + "</legend>");
            AssertContains(on, "<legend>" + Encoded + "</legend>");
        }

        [TestMethod]
        public void Button_Text_DefaultIsHtml_OnIsEncoded()
        {
            var off = Render(new ButtonTagHelper { Id = "b1", Text = Raw, ConfigsMonitor = Off() }, "wt:button");
            var on = Render(new ButtonTagHelper { Id = "b1", Text = Raw, ConfigsMonitor = On() }, "wt:button");
            var offIcon = Render(new ButtonTagHelper { Id = "b1", Text = Raw, Icon = "layui-icon-add", ConfigsMonitor = Off() }, "wt:button");
            var onIcon = Render(new ButtonTagHelper { Id = "b1", Text = Raw, Icon = "layui-icon-add", ConfigsMonitor = On() }, "wt:button");

            AssertContains(off, ">" + Raw + "</button>");
            AssertContains(on, ">" + Encoded + "</button>");
            AssertContains(offIcon, "</i> " + Raw + "</button>");
            AssertContains(onIcon, "</i> " + Encoded + "</button>");
        }

        [TestMethod]
        public void LinkButton_Text_DefaultIsHtml_OnIsEncoded()
        {
            var off = Render(new LinkButtonTagHelper { Id = "l1", Text = Raw, ConfigsMonitor = Off() }, "wt:linkbutton");
            var on = Render(new LinkButtonTagHelper { Id = "l1", Text = Raw, ConfigsMonitor = On() }, "wt:linkbutton");

            AssertContains(off, Raw);
            AssertContains(on, Encoded);
            AssertNotContains(on, Raw);
        }

        [TestMethod]
        public void FieldLabelAndPaddingText_DefaultIsHtml_OnIsEncoded()
        {
            var vm = NewVm();
            var off = Render(new TextBoxTagHelper { Field = Field(vm, x => x.Memo), LabelText = Raw, PaddingText = Raw, ConfigsMonitor = Off() }, "wt:textbox");
            var on = Render(new TextBoxTagHelper { Field = Field(vm, x => x.Memo), LabelText = Raw, PaddingText = Raw, ConfigsMonitor = On() }, "wt:textbox");

            AssertContains(off, Raw + ":</label>", "layui-word-aux\">" + Raw + "</div>");
            AssertContains(on, Encoded + ":</label>", "layui-word-aux\">" + Encoded + "</div>");
            AssertNotContains(on, Raw);
        }

        [TestMethod]
        public void FieldLabel_OptionOn_KeepsOrdinaryLabelUnchanged()
        {
            var vm = NewVm();
            var off = Render(new TextBoxTagHelper { Field = Field(vm, x => x.Name), ConfigsMonitor = Off() }, "wt:textbox");
            var on = Render(new TextBoxTagHelper { Field = Field(vm, x => x.Name), ConfigsMonitor = On() }, "wt:textbox");

            // generated ids differ between renders, everything else must be identical
            Assert.AreEqual(Normalize(off), Normalize(on));
        }

        private static string Normalize(string html)
            => System.Text.RegularExpressions.Regex.Replace(html, "[0-9a-f]{32}", "ID");

        #endregion

        #region flow info

        private static List<ApproveTimeLine> Timeline() => new List<ApproveTimeLine>
        {
            new ApproveTimeLine { Time = Raw, Action = "_finish", Remark = Raw },
            new ApproveTimeLine { Time = "t2", Action = "等待审批", Approvers = Raw },
        };

        private static FlowInfoTagHelper NewFlowInfo(IOptionsMonitor<Configs> configs)
        {
            var vm = new Mock<IBaseCRUDVM<TopBasePoco>>();
            vm.Setup(x => x.GetWorkflowTimeLineAsync(It.IsAny<string>())).Returns(Task.FromResult(Timeline()));
            return new FlowInfoTagHelper { Vm = Self(vm.Object, "Vm"), ConfigsMonitor = configs };
        }

        [TestMethod]
        public void FlowInfo_Default_EmitsTextAsHtml()
        {
            var html = Render(NewFlowInfo(Off()), "wt:flowinfo");

            AssertContains(html, "layui-timeline-title\">" + Raw + "</h3>", "备注：" + Raw + "</p>", "等待" + Raw + "审批");
        }

        [TestMethod]
        public void FlowInfo_OptionOn_EncodesTimeMessageAndRemark()
        {
            var html = Render(NewFlowInfo(On()), "wt:flowinfo");

            AssertContains(html, "layui-timeline-title\">" + Encoded + "</h3>", "备注：" + Encoded + "</p>", "等待" + Encoded + "审批");
            AssertNotContains(html, Raw);
        }

        #endregion

        #region grid action buttons

        private class ActionNameListVM : LayuiSchoolListVM
        {
            protected override List<GridAction> InitGridAction()
            {
                return new List<GridAction>
                {
                    new GridAction
                    {
                        Name = Raw,
                        ControllerName = "School",
                        ActionName = "doEdit",
                        ShowInRow = true,
                        ParameterType = GridActionParameterTypesEnum.NoId,
                    },
                };
            }
        }

        private static string RenderGrid(bool encode)
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.ConfigInfo.IsQuickDebug = true;
            wtm.ConfigInfo.UIOptions.EncodeHtmlContent = encode;
            var listVm = new ActionNameListVM { Wtm = wtm };
            return Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");
        }

        [TestMethod]
        public void Grid_ActionButtonName_DefaultIsHtml_OnIsEncoded()
        {
            var off = RenderGrid(false);
            var on = RenderGrid(true);

            // row button and toolbar button
            AssertContains(off, "\">" + Raw + "</a>", "</i>" + Raw + "</a>");
            AssertContains(on, "\">" + Encoded + "</a>", "</i>" + Encoded + "</a>");
            AssertNotContains(on, Raw);
        }

        #endregion

        #region grid cells (server side)

        private class CellListVM : LayuiSchoolListVM
        {
            public string Fore { get; set; }

            protected override IEnumerable<IGridColumn<School>> InitGridHeader()
            {
                return new List<GridColumn<School>>
                {
                    this.MakeGridHeader(x => x.SchoolCode).SetForeGroundFunc(x => Fore),
                    this.MakeGridHeader(x => x.SchoolName).SetFormat((e, v) => ColumnFormatInfo.MakeHtml("<i>fmt</i>")),
                };
            }
        }

        private static string CellJson(bool encode, string fore = null)
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.ConfigInfo.UIOptions.EncodeHtmlContent = encode;
            var listVm = new CellListVM { Wtm = wtm, Fore = fore };
            return listVm.GetSingleDataJson(new School { SchoolCode = Raw, SchoolName = "n" }, false);
        }

        [TestMethod]
        public void GridCell_Default_RawValueIsEmittedAsIs()
        {
            var json = CellJson(false);

            AssertContains(json, "\"SchoolCode\":\"" + Raw + "\"", "\"SchoolName\":\"<i>fmt</i><script>");
        }

        [TestMethod]
        public void GridCell_OptionOn_EncodesRawValueButNotCustomFormatOutput()
        {
            var json = CellJson(true);

            AssertContains(json, "\"SchoolCode\":\"" + Encoded + "\"", "\"SchoolName\":\"<i>fmt</i><script>");
            AssertNotContains(json, Raw);
        }

        // #286: values saved through a form are already HTML-encoded by the model binder; with the option on, the grid must not encode them again
        private class CodeOnlyListVM : LayuiSchoolListVM
        {
            protected override IEnumerable<IGridColumn<School>> InitGridHeader()
            {
                return new List<GridColumn<School>> { this.MakeGridHeader(x => x.SchoolCode) };
            }
        }

        private static string CellJsonOf(bool encode, string schoolCode)
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.ConfigInfo.UIOptions.EncodeHtmlContent = encode;
            var listVm = new CodeOnlyListVM { Wtm = wtm };
            return listVm.GetSingleDataJson(new School { SchoolCode = schoolCode, SchoolName = "n" }, false);
        }

        [TestMethod]
        public void GridCell_OptionOn_AlreadyEncodedValueIsEncodedOnlyOnce()
        {
            // what the binder stores for the typed text:  A&B <i> "q" 'q'
            var json = CellJsonOf(true, "A&amp;B &lt;i&gt; &quot;q&quot; &#39;q&#39;");

            AssertContains(json, "\"SchoolCode\":\"A&amp;B &lt;i&gt; &quot;q&quot; &#39;q&#39;\"");
            AssertNotContains(json, "&amp;amp;", "&amp;lt;", "&amp;quot;", "&amp;#39;");
        }

        [TestMethod]
        public void GridCell_OptionOn_TextThatLooksLikeAnEntityIsStillShownAsTyped()
        {
            // the user typed the characters &lt; (the binder stored &amp;lt;), which must still display as &lt; (encoded once: &amp;lt;)
            var json = CellJsonOf(true, "&amp;lt;");

            AssertContains(json, "\"SchoolCode\":\"&amp;lt;\"");
        }

        [TestMethod]
        public void GridCell_OptionOn_MixOfEncodedAndRawMarkupIsStillSafe()
        {
            var json = CellJsonOf(true, "A&amp;B <script>alert(1)</script> \"x\" 'y' &lt;i&gt;");

            AssertContains(json, "A&amp;B &lt;script&gt;alert(1)&lt;/script&gt; &quot;x&quot; &#39;y&#39; &lt;i&gt;");
            AssertNotContains(json, "<script", "</script", "<i", "\"x\"", "'y'");
        }

        [TestMethod]
        public void GridCell_OptionOn_NumericEntityForMarkupIsNotLeftActive()
        {
            var json = CellJsonOf(true, "&#60;img src=x&#62; &lt;b&gt;");

            AssertContains(json, "&lt;img src=x&gt; &lt;b&gt;");
            AssertNotContains(json, "<img", "&#60;", "&#62;");
        }

        [TestMethod]
        public void GridCell_OptionOn_PlainValueIsUnchanged()
        {
            AssertContains(CellJsonOf(true, "plain text 123"), "\"SchoolCode\":\"plain text 123\"");
        }

        [TestMethod]
        public void GridCell_Default_AlreadyEncodedValueIsEmittedAsIs()
        {
            // with the option off nothing changes: the stored (encoded) text goes to the page untouched
            var json = CellJsonOf(false, "A&amp;B &lt;i&gt;");

            AssertContains(json, "\"SchoolCode\":\"A&amp;B &lt;i&gt;\"");
        }

        [TestMethod]
        public void GridCell_Color_DefaultIsEmittedAsIs()
        {
            var json = CellJson(false, "red';alert(1);'");

            AssertContains(json, "__forecolor\":\"#red';alert(1);'\"");
        }

        [TestMethod]
        public void GridCell_Color_OptionOn_OnlyKeepsColorNotation()
        {
            AssertContains(CellJson(true, "ff0000"), "\"SchoolCode__forecolor\":\"#ff0000\"");
            AssertContains(CellJson(true, "rgb(1, 2, 3)"), "\"SchoolCode__forecolor\":\"#rgb(1, 2, 3)\"");
            AssertNotContains(CellJson(true, "red';alert(1);'"), "__forecolor");
            AssertNotContains(CellJson(true, "red\"}</script>"), "__forecolor");
        }

        [TestMethod]
        public void GridCellTemplate_IsTheSameWhateverTheOption()
        {
            // cells are encoded on the server (a client template can not tell a custom format's HTML from data)
            AssertContains(RenderGrid(true), "d.SchoolCode.replace(");
            Assert.AreEqual(
                System.Text.RegularExpressions.Regex.Matches(Normalize(RenderGrid(false)), "function\\(d\\)\\{var sty").Count,
                System.Text.RegularExpressions.Regex.Matches(Normalize(RenderGrid(true)), "function\\(d\\)\\{var sty").Count);
        }

        #endregion

        #region UIService

        [TestMethod]
        public void UIService_ButtonText_DefaultIsHtml_OnIsEncoded()
        {
            foreach (var service in new[] { new LayuiUIService(), new LayuiUIService(Off()) })
            {
                AssertContains(service.MakeScriptButton(ButtonTypesEnum.Link, Raw, "x()", "b1"), ">" + Raw + "</a>");
                AssertContains(service.MakeScriptButton(ButtonTypesEnum.Button, Raw, "x()", "b1"), ">" + Raw + "</a>");
                AssertContains(service.MakeDialogButton(ButtonTypesEnum.Link, "/a", Raw, 1, 2, buttonID: "b1"), ">" + Raw + "</a>");
                AssertContains(service.MakeButton(ButtonTypesEnum.Button, "/a", Raw, 1, 2, buttonID: "b1"), ">" + Raw + "</a>");
                AssertContains(service.MakeDownloadButton(ButtonTypesEnum.Link, Guid.NewGuid(), Raw), ">" + Raw + "</a>");
                AssertContains(service.MakeViewButton(ButtonTypesEnum.Link, Guid.NewGuid(), Raw), ">" + Raw + "</a>");
            }

            var on = new LayuiUIService(On());
            foreach (var html in new[]
            {
                on.MakeScriptButton(ButtonTypesEnum.Link, Raw, "x()", "b1"),
                on.MakeScriptButton(ButtonTypesEnum.Button, Raw, "x()", "b1"),
                on.MakeDialogButton(ButtonTypesEnum.Link, "/a", Raw, 1, 2, buttonID: "b1"),
                on.MakeButton(ButtonTypesEnum.Button, "/a", Raw, 1, 2, buttonID: "b1"),
                on.MakeDownloadButton(ButtonTypesEnum.Link, Guid.NewGuid(), Raw),
                on.MakeViewButton(ButtonTypesEnum.Link, Guid.NewGuid(), Raw),
            })
            {
                AssertContains(html, ">" + Encoded + "</a>");
                AssertNotContains(html, ">" + Raw + "</a>");
            }
        }

        [TestMethod]
        public void UIService_Combo_ItemTextAndEmptyText_DefaultIsHtml_OnIsEncoded()
        {
            var items = new List<ComboSelectListItem> { new ComboSelectListItem { Text = Raw, Value = "1" }, new ComboSelectListItem { Text = Raw, Value = "2" } };

            var off = new LayuiUIService(Off()).MakeCombo("c", items, "2", Raw);
            var on = new LayuiUIService(On()).MakeCombo("c", items, "2", Raw);

            AssertContains(off, "<option value=''>" + Raw + "</option>", "<option value='1'>" + Raw + "</option>", "selected>" + Raw + "</option>");
            AssertContains(on, "<option value=''>" + Encoded + "</option>", "<option value='1'>" + Encoded + "</option>", "selected>" + Encoded + "</option>");
            AssertNotContains(on, Raw);
        }

        [TestMethod]
        public void UIService_OptionOn_KeepsCjkText()
        {
            var html = new LayuiUIService(On()).MakeScriptButton(ButtonTypesEnum.Link, "第一所學校", "x()", "b1");

            AssertContains(html, ">第一所學校</a>");
        }

        #endregion
    }
}
