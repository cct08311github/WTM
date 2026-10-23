using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Chart;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Second batch: more values taken from data or configuration that are embedded in a JavaScript
    /// string literal of the generated script (tree, link button, submit url, grid urls, search
    /// autocomplete, chart, ueditor, selector, upload, date, slider and the UI service handlers) must be
    /// escaped, while ordinary values must be rendered exactly as before.
    /// </summary>
    [TestClass]
    public class JsStringLiteralEscapingMoreTests : LayUITagHelperTestBase
    {
        // generic value that would terminate a quoted literal and a script element
        private const string Hostile = "a'b\"c</script>";
        // its escaped form inside a JavaScript string literal
        private const string HostileEscaped = "a\\u0027b\\u0022c\\u003C/script\\u003E";

        private static LayuiTestVM NewVm() => new LayuiTestVM();

        // grid actions that point to a controller are only rendered when the url is accessible
        private static WTMContext QuickDebugContext()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.ConfigInfo.IsQuickDebug = true;
            return wtm;
        }

        private static void AssertEscaped(string html, string before, string after)
        {
            AssertContains(html, before + HostileEscaped + after);
            AssertNotContains(html, before + "a'b", before + "a\"b");
            AssertNotContains(html, "c</script>" + after);
        }

        #region data / configuration values are escaped

        [TestMethod]
        public void Tree_ItemUrlEmptyTextAndTriggerUrl_AreEscaped()
        {
            var vm = NewVm();
            vm.Names = new List<string>();
            var helper = new TreeTagHelper(Options())
            {
                Field = Field(vm, x => x.Names),
                ItemUrl = Hostile,
                EmptyText = Hostile,
                LinkId = "other",
                TriggerUrl = Hostile,
            };

            var html = Render(helper, "wt:tree");

            AssertContains(html,
                "ff.LoadComboItems('tree','" + HostileEscaped + "'",
                "tips:'" + HostileEscaped + "'",
                "var u = \"" + HostileEscaped + "\";");
            AssertNotContains(html, "'tree','a'b", "tips:'a'b", "var u = \"a'b");
        }

        [TestMethod]
        public void Tree_TriggerUrlWithDefaultValue_IsEscaped()
        {
            var vm = NewVm();
            vm.Names = new List<string> { "1" };
            var helper = new TreeTagHelper(Options())
            {
                Field = Field(vm, x => x.Names),
                LinkId = "other",
                TriggerUrl = Hostile,
            };

            AssertEscaped(Render(helper, "wt:tree"), "LayuiTestVM_Namesu = \"", "\";");
        }

        [TestMethod]
        public void LinkButton_Layer_UrlAndWindowTitle_AreEscaped()
        {
            var helper = new LinkButtonTagHelper { Id = "lb1", Text = "Edit", Url = Hostile, WindowTitle = Hostile };

            var html = Render(helper, "wt:linkbutton");

            AssertContains(html, "ff.OpenDialog('" + HostileEscaped + "', '");
            AssertContains(html, "', '" + HostileEscaped + "',");
            AssertNotContains(html, "OpenDialog('a'b", "', 'a'b");
        }

        [TestMethod]
        public void LinkButton_Layer_PostCurrentForm_UrlAndWindowTitle_AreEscaped()
        {
            var vm = NewVm();
            var helper = new LinkButtonTagHelper { Id = "lb1", Text = "Edit", Url = Hostile, WindowTitle = Hostile, PostCurrentForm = true };

            var html = Render(helper, "wt:linkbutton", FormItems(vm, "wtForm_x"));

            AssertContains(html, "ff.OpenDialog('" + HostileEscaped + "', '", "', '" + HostileEscaped + "',");
            AssertNotContains(html, "OpenDialog('a'b", "', 'a'b");
        }

        [TestMethod]
        public void LinkButton_Self_Url_IsEscaped()
        {
            var helper = new LinkButtonTagHelper { Id = "lb3", Text = "Run", Url = Hostile, Target = ButtonTargetEnum.self };

            AssertEscaped(Render(helper, "wt:linkbutton"), "ff.BgRequest('", "')");
        }

        [TestMethod]
        public void LinkButton_SelfPostCurrentForm_Url_IsEscaped()
        {
            var vm = NewVm();
            var helper = new LinkButtonTagHelper { Id = "lb3", Text = "Run", Url = Hostile, Target = ButtonTargetEnum.self, PostCurrentForm = true };

            AssertEscaped(Render(helper, "wt:linkbutton", FormItems(vm, "wtForm_x")), "ff.BgRequest('", "',ff.GetPostData(");
        }

        [TestMethod]
        public void LinkButton_NewWindow_UrlAndWindowTitle_AreEscaped()
        {
            var helper = new LinkButtonTagHelper { Id = "lb4", Text = "Open", Url = Hostile, WindowTitle = Hostile, Target = ButtonTargetEnum.newwindow };

            var html = Render(helper, "wt:linkbutton");

            AssertContains(html,
                "ff.SetCookie('#" + HostileEscaped + "','" + HostileEscaped + "',true);window.open('/Home/PIndex#" + HostileEscaped + "')");
            AssertNotContains(html, "SetCookie('#a'b", "window.open('/Home/PIndex#a'b");
        }

        [TestMethod]
        public void LinkButton_NewWindowWithTilde_UrlAndWindowTitle_AreEscaped()
        {
            var helper = new LinkButtonTagHelper { Id = "lb4", Text = "Open", Url = "~" + Hostile, WindowTitle = Hostile, Target = ButtonTargetEnum.newwindow };

            var html = Render(helper, "wt:linkbutton");

            AssertContains(html, "ff.SetCookie('#" + HostileEscaped + "','" + HostileEscaped + "',true);window.open('" + HostileEscaped + "')");
            AssertNotContains(html, "SetCookie('#a'b", "window.open('a'b");
        }

        [TestMethod]
        public void LinkButton_CurrentWindow_UrlAndWindowTitle_AreEscaped()
        {
            var helper = new LinkButtonTagHelper { Id = "lb2", Text = "Home", Url = Hostile, WindowTitle = Hostile, IsLink = true, Target = ButtonTargetEnum.currentwindow };

            var html = Render(helper, "wt:linkbutton");

            AssertContains(html, "ff.LoadPage('" + HostileEscaped + "',false,'" + HostileEscaped + "');");
            AssertNotContains(html, "LoadPage('a'b");
        }

        [TestMethod]
        public void SubmitButton_SubmitUrl_IsEscaped()
        {
            var vm = NewVm();
            var helper = new SubmitButtonTagHelper { Id = "sb1", SubmitUrl = Hostile };

            AssertEscaped(Render(helper, "wt:submitbutton", FormItems(vm, "wtForm_x")), ".attr('action','", "');");
        }

        [TestMethod]
        public void Grid_Url_IsEscaped()
        {
            var listVm = new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = Hostile }, "wt:grid");

            AssertContains(html, "url = '" + HostileEscaped + "';", "{url:'" + HostileEscaped + "',where:");
            AssertNotContains(html, "url = 'a'b", "{url:'a'b");
        }

        private class QueryStringListVM : LayuiSchoolListVM
        {
            public string Query { get; set; }

            protected override List<GridAction> InitGridAction()
            {
                return new List<GridAction>
                {
                    new GridAction
                    {
                        Name = "Edit",
                        ControllerName = "School",
                        ActionName = "doEdit",
                        QueryString = Query,
                        ParameterType = GridActionParameterTypesEnum.NoId,
                    },
                };
            }
        }

        [TestMethod]
        public void Grid_ActionUrl_IsEscaped()
        {
            var listVm = new QueryStringListVM { Wtm = QuickDebugContext(), Query = Hostile };

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            AssertContains(html, "var tempUrl = '/School/doEdit?" + HostileEscaped + "',whereStr=");
            AssertNotContains(html, "var tempUrl = '/School/doEdit?a'b");
        }

        [TestMethod]
        public void TextBox_SearchUrlAndTriggerUrl_AreEscaped()
        {
            var helper = new TextBoxTagHelper { Field = Field(NewVm(), x => x.Name), SearchUrl = Hostile, TriggerUrl = Hostile };

            var html = Render(helper, "wt:textbox");

            AssertContains(html, "url: '" + HostileEscaped + "',", "ff.ChainChange('" + HostileEscaped + "/'+encodeURIComponent(data.Value)");
            AssertNotContains(html, "url: 'a'b", "ChainChange('a'b");
        }

        [TestMethod]
        public void TextBox_SearchUrlWithoutTrigger_IsEscaped()
        {
            var helper = new TextBoxTagHelper { Field = Field(NewVm(), x => x.Name), SearchUrl = Hostile };

            AssertEscaped(Render(helper, "wt:textbox"), "url: '", "',");
        }

        [TestMethod]
        public void Chart_TitleAxisNamesAndTriggerUrl_AreEscaped()
        {
            var helper = new ChartTagHelper
            {
                Id = "c1",
                Type = ChartTypeEnum.Bar,
                Title = Hostile,
                NameX = Hostile,
                NameY = Hostile,
                TriggerUrl = Hostile,
            };

            var html = Render(helper, "wt:chart");

            AssertContains(html,
                "title:{text: '" + HostileEscaped + "'}",
                "xAxis: {name:'" + HostileEscaped + "',type: 'category'}",
                "yAxis: {name:'" + HostileEscaped + "'}",
                "c1ChartUrl = '" + HostileEscaped + "';");
            AssertNotContains(html, "text: 'a'b", "name:'a'b", "ChartUrl = 'a'b");
        }

        [TestMethod]
        public void Chart_HorizontalAxisNames_AreEscaped()
        {
            var helper = new ChartTagHelper { Id = "c1", Type = ChartTypeEnum.Bar, IsHorizontal = true, NameX = Hostile, NameY = Hostile };

            var html = Render(helper, "wt:chart");

            AssertContains(html,
                "xAxis: {name:'" + HostileEscaped + "'}",
                "yAxis: {name:'" + HostileEscaped + "',type: 'category'}");
            AssertNotContains(html, "name:'a'b");
        }

        [TestMethod]
        public void Chart_ScatterNames_AreEscaped()
        {
            var helper = new ChartTagHelper
            {
                Id = "c1",
                Type = ChartTypeEnum.Scatter,
                NameX = Hostile,
                NameY = Hostile,
                NameAddition = Hostile,
                NameCategory = Hostile,
            };

            var html = Render(helper, "wt:chart");

            AssertContains(html,
                "var xl = '" + HostileEscaped + ":';",
                "var yl = '" + HostileEscaped + ":';",
                "var al = '" + HostileEscaped + ":';",
                "var cl = '" + HostileEscaped + ":';",
                "xAxis: { name:'" + HostileEscaped + "',type: 'value'",
                "yAxis:{name:'" + HostileEscaped + "',splitLine");
            AssertNotContains(html, "var xl = 'a'b", "name:'a'b");
        }

        [TestMethod]
        public void UEditor_DefaultValue_IsEscaped()
        {
            var helper = new UEditorTagHelper { Field = Field(new LayuiTestVM { Html = "hi" }, x => x.Html), DefaultValue = Hostile };

            AssertEscaped(Render(helper, "wt:ueditor"), "this.setContent('", "')");
        }

        [TestMethod]
        public void UEditor_FieldValue_IsEscaped()
        {
            var helper = new UEditorTagHelper { Field = Field(new LayuiTestVM { Html = Hostile }, x => x.Html) };

            AssertEscaped(Render(helper, "wt:ueditor"), "this.setContent('", "')");
        }

        [TestMethod]
        public void Selector_WindowTitle_IsEscaped()
        {
            var vm = NewVm();
            var schoolVm = new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() };
            var helper = new SelectorTagHelper
            {
                Field = Field(vm, x => x.SchoolId),
                ListVM = Self(schoolVm, "Selector"),
                TextBind = Field(new School(), x => x.SchoolName),
                WindowTitle = Hostile,
            };

            var html = Render(helper, "wt:selector", FormItems(vm, "wtForm_x"));

            AssertContains(html, "', '" + HostileEscaped + "',800, 600");
            AssertNotContains(html, "', 'a'b");
        }

        [TestMethod]
        public void Upload_CustomTypeAndExtraQuery_AreEscaped()
        {
            var helper = new UploadTagHelper
            {
                Field = Field(NewVm(), x => x.PhotoId),
                CustomType = Hostile,
                ExtraQuery = Hostile,
            };

            var html = Render(helper, "wt:upload");

            AssertContains(html, ", exts: '" + HostileEscaped + "'", ",url: '/_Framework/Upload?1=1\\u0026" + HostileEscaped + "'");
            AssertNotContains(html, "exts: 'a'b", "?1=1&a'b", "?1=1\\u0026a'b");
        }

        [TestMethod]
        public void Upload_PreviewAndExistingFileUrls_AreEscaped()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.CurrentCS = Hostile;
            var vm = new LayuiTestVM { Wtm = wtm, PhotoId = Guid.NewGuid() };
            var helper = new UploadTagHelper { Field = Field(vm, x => x.PhotoId), ShowPreview = true };

            var html = Render(helper, "wt:upload", FormItems(vm));

            AssertContains(html,
                "'/_Framework/GetFile/'+ fileid+'?_DONOT_USE_CS=" + HostileEscaped + "'",
                "_DONOT_USE_CS=" + HostileEscaped + "',");
            AssertNotContains(html, "_DONOT_USE_CS=a'b");
        }

        [TestMethod]
        public void MultiUpload_CustomTypeAndExtraQuery_AreEscaped()
        {
            var vm = new LayuiTestVM { Files = new List<TestSubFile> { new TestSubFile { FileId = Guid.NewGuid() } } };
            var helper = new MultiUploadTagHelper
            {
                Field = Field(vm, x => x.Files),
                CustomType = Hostile,
                ExtraQuery = Hostile,
            };

            var html = Render(helper, "wt:multiupload");

            AssertContains(html, ", exts: '" + HostileEscaped + "'", ",url: '/_Framework/Upload?1=1\\u0026" + HostileEscaped + "'");
            AssertNotContains(html, "exts: 'a'b", "?1=1&a'b", "?1=1\\u0026a'b");
        }

        [TestMethod]
        public void MultiUpload_ConnectionStringInUrls_IsEscaped()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.CurrentCS = Hostile;
            var vm = new LayuiTestVM { Wtm = wtm, Files = new List<TestSubFile> { new TestSubFile { FileId = Guid.NewGuid() } } };
            var helper = new MultiUploadTagHelper { Field = Field(vm, x => x.Files) };

            var html = Render(helper, "wt:multiupload", FormItems(vm));

            AssertContains(html, "_DONOT_USE_CS=" + HostileEscaped);
            AssertNotContains(html, "_DONOT_USE_CS=a'b");
        }

        [TestMethod]
        public void DateTime_FormatRangeSplitMinAndMax_AreEscaped()
        {
            var helper = new DateTimeTagHelper(Options())
            {
                Field = Field(NewVm(), x => x.Birthday),
                Format = Hostile,
                Range = true,
                RangeSplit = Hostile,
                Min = Hostile,
                Max = Hostile,
            };

            var html = Render(helper, "wt:datetime");

            AssertContains(html,
                ",range:'" + HostileEscaped + "'",
                ",format: '" + HostileEscaped + "'",
                ",min: '" + HostileEscaped + "'",
                ",max: '" + HostileEscaped + "'");
            AssertNotContains(html, ",range:'a'b", ",format: 'a'b", ",min: 'a'b", ",max: 'a'b");
        }

        [TestMethod]
        public void Slider_Theme_IsEscaped()
        {
            var helper = new SliderTagHelper { Field = Field(NewVm(), x => x.Score), Theme = Hostile };

            AssertEscaped(Render(helper, "wt:slider"), ",theme: '", "'");
        }

        [TestMethod]
        public void UIService_DialogButton_UrlAndTitle_AreEscaped()
        {
            var html = new LayuiUIService().MakeDialogButton(ButtonTypesEnum.Button, Hostile, "t", 100, 200, title: Hostile, buttonID: "b1");

            AssertContains(html, "ff.OpenDialog('" + HostileEscaped + "','", ",'" + HostileEscaped + "',100,200,");
            AssertNotContains(html, "OpenDialog('a'b", ",'a'b");
        }

        [TestMethod]
        public void UIService_DialogButtonWithoutDialog_Url_IsEscaped()
        {
            var html = new LayuiUIService().MakeDialogButton(ButtonTypesEnum.Link, Hostile, "t", null, null, buttonID: "b1", showDialog: false);

            AssertEscaped(html, "type: 'GET',url: '", "',async");
        }

        [TestMethod]
        public void UIService_Button_UrlTitleAndDivId_AreEscaped()
        {
            var service = new LayuiUIService();

            var layer = service.MakeButton(ButtonTypesEnum.Button, Hostile, "t", 1, 2, title: Hostile, buttonID: "b1", rtype: RedirectTypesEnum.Layer);
            var self = service.MakeButton(ButtonTypesEnum.Button, Hostile, "t", 1, 2, buttonID: "b1", currentdivid: Hostile, rtype: RedirectTypesEnum.Self);
            var newWindow = service.MakeButton(ButtonTypesEnum.Link, Hostile, "t", 1, 2, title: Hostile, buttonID: "b1", rtype: RedirectTypesEnum.NewWindow);
            var newTab = service.MakeButton(ButtonTypesEnum.Link, Hostile, "t", 1, 2, title: Hostile, buttonID: "b1", rtype: RedirectTypesEnum.NewTab);

            AssertContains(layer, "ff.OpenDialog('" + HostileEscaped + "','", ",'" + HostileEscaped + "',1,2,");
            AssertContains(self, "ff.BgRequest('" + HostileEscaped + "',undefined,'" + HostileEscaped + "');");
            AssertContains(newWindow, "ff.LoadPage('" + HostileEscaped + "',true,'" + HostileEscaped + "');");
            AssertContains(newTab, "ff.LoadPage('" + HostileEscaped + "',false,'" + HostileEscaped + "');");
            AssertNotContains(layer + self + newWindow + newTab, "('a'b", ",'a'b");
        }

        [TestMethod]
        public void UIService_ViewButton_ConnectionString_IsEscaped()
        {
            var html = new LayuiUIService().MakeViewButton(ButtonTypesEnum.Button, Guid.NewGuid(), "t", _DONOT_USE_CS: Hostile);

            AssertEscaped(html, "_DONOT_USE_CS=", "'}]}");
        }

        [TestMethod]
        public void UIService_DateTime_Id_IsEscaped()
        {
            var html = new LayuiUIService().MakeDateTime(Hostile);

            AssertContains(html, "ff.SetGridCellDate(\"" + HostileEscaped + "\"");
            AssertNotContains(html, "ff.SetGridCellDate(\"a'b");
        }

        #endregion

        #region ordinary values are unchanged

        [TestMethod]
        public void OrdinaryValues_AreRenderedExactlyAsBefore()
        {
            var vm = NewVm();
            vm.Names = new List<string>();

            var tree = new TreeTagHelper(Options())
            {
                Field = Field(vm, x => x.Names),
                ItemUrl = "/api/menus",
                EmptyText = "請選擇",
                LinkId = "other",
                TriggerUrl = "/School/Trigger",
            };
            AssertContains(Render(tree, "wt:tree"),
                "ff.LoadComboItems('tree','/api/menus'", "tips:'請選擇'", "var u = \"/School/Trigger\";");

            var link = new LinkButtonTagHelper { Id = "lb1", Text = "Edit", Url = "/School/Edit", WindowTitle = "編輯學校", WindowWidth = 500 };
            AssertContains(Render(link, "wt:linkbutton"), "ff.OpenDialog('/School/Edit', '", "', '編輯學校',500, null,undefined,false)");

            var self = new LinkButtonTagHelper { Id = "lb3", Text = "Run", Url = "/Job/Run", Target = ButtonTargetEnum.self };
            AssertContains(Render(self, "wt:linkbutton"), "ff.BgRequest('/Job/Run')");

            var current = new LinkButtonTagHelper { Id = "lb2", Text = "Home", Url = "/Home", WindowTitle = "首頁", IsLink = true, Target = ButtonTargetEnum.currentwindow };
            AssertContains(Render(current, "wt:linkbutton"), "ff.LoadPage('/Home',false,'首頁');");

            var newWindow = new LinkButtonTagHelper { Id = "lb4", Text = "Open", Url = "/School/Index", WindowTitle = "學校", Target = ButtonTargetEnum.newwindow };
            AssertContains(Render(newWindow, "wt:linkbutton"), "ff.SetCookie('#/School/Index','學校',true);window.open('/Home/PIndex#/School/Index')");

            var submit = new SubmitButtonTagHelper { Id = "sb1", SubmitUrl = "/School/Add" };
            AssertContains(Render(submit, "wt:submitbutton", FormItems(NewVm(), "wtForm_x")), "$('#wtForm_x').attr('action','/School/Add');");

            var listVm = new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() };
            AssertContains(Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid"),
                "url = '/School/Search';", "{url:'/School/Search',where:");

            var queryVm = new QueryStringListVM { Wtm = QuickDebugContext(), Query = "a=1" };
            AssertContains(Render(new DataTableTagHelper { Vm = Self(queryVm, "Vm"), Url = "/School/Search" }, "wt:grid"),
                "var tempUrl = '/School/doEdit?a=1',whereStr=");

            var textBox = new TextBoxTagHelper { Field = Field(NewVm(), x => x.Name), SearchUrl = "/School/Search", TriggerUrl = "/School/Trigger" };
            AssertContains(Render(textBox, "wt:textbox"), "url: '/School/Search',", "ff.ChainChange('/School/Trigger/'+encodeURIComponent(data.Value)");

            var chart = new ChartTagHelper { Id = "salesChart", Type = ChartTypeEnum.Bar, Title = "銷售", NameX = "月份", NameY = "金額", TriggerUrl = "/Chart/Data" };
            AssertContains(Render(chart, "wt:chart"),
                "title:{text: '銷售'}", "xAxis: {name:'月份',type: 'category'}", "yAxis: {name:'金額'}", "salesChartChartUrl = '/Chart/Data';");

            var scatter = new ChartTagHelper { Id = "s1", Type = ChartTypeEnum.Scatter, NameX = "X", NameY = "Y" };
            AssertContains(Render(scatter, "wt:chart"), "var xl = 'X:';", "var yl = 'Y:';", "var al = '';", "var cl = '';");

            var editor = new UEditorTagHelper { Field = Field(new LayuiTestVM { Html = "你好 world" }, x => x.Html) };
            AssertContains(Render(editor, "wt:ueditor"), "this.setContent('你好 world')");

            var selector = new SelectorTagHelper
            {
                Field = Field(vm, x => x.SchoolId),
                ListVM = Self(new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext() }, "Selector"),
                TextBind = Field(new School(), x => x.SchoolName),
                WindowTitle = "選擇學校",
            };
            AssertContains(Render(selector, "wt:selector", FormItems(vm, "wtForm_x")), "', '選擇學校',800, 600");

            var upload = new UploadTagHelper { Field = Field(NewVm(), x => x.PhotoId), CustomType = "pdf|docx" };
            // the '&' of the query string is written as \u0026, which the browser decodes back to '&'
            AssertContains(Render(upload, "wt:upload"), ", exts: 'pdf|docx'", ",url: '/_Framework/Upload?1=1\\u0026'");

            var date = new DateTimeTagHelper(Options())
            {
                Field = Field(NewVm(), x => x.Birthday),
                Format = "yyyy/MM/dd",
                Range = true,
                RangeSplit = " - ",
                Min = "2020-01-01",
                Max = "30",
            };
            AssertContains(Render(date, "wt:datetime"),
                ",range:' - '", ",format: 'yyyy/MM/dd'", ",min: '2020-01-01'", ",max: 30");

            var slider = new SliderTagHelper { Field = Field(NewVm(), x => x.Score), Theme = "#009688" };
            AssertContains(Render(slider, "wt:slider"), ",theme: '#009688'");

            var service = new LayuiUIService();
            AssertContains(service.MakeDialogButton(ButtonTypesEnum.Button, "/School/Edit", "t", 100, 200, title: "編輯", buttonID: "b1"),
                "ff.OpenDialog('/School/Edit','", ",'編輯',100,200,undefined,false);");
            AssertContains(service.MakeButton(ButtonTypesEnum.Button, "/Job/Run", "t", 1, 2, buttonID: "b1", currentdivid: "div1", rtype: RedirectTypesEnum.Self),
                "ff.BgRequest('/Job/Run',undefined,'div1');");
            AssertContains(service.MakeDateTime("Entity.Birthday"), "ff.SetGridCellDate(\"Entity_Birthday\",\"datetime\")");
        }

        #endregion
    }
}
