using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Issues #24 / #25 leftovers: the last generated script snippets that still embedded data or
    /// configuration values unescaped (tab chart lookup, color picker value, slider default value,
    /// HTML built inside upload scripts). Ordinary values must be rendered as before.
    /// </summary>
    [TestClass]
    public class RemainingScriptSnippetsTests : LayUITagHelperTestBase
    {
        // terminates a quoted literal / an attribute / the script element
        private const string Hostile = "a'b\"c</script>";
        // Hostile escaped for a JavaScript string literal
        private const string HostileJs = "a\\u0027b\\u0022c\\u003C/script\\u003E";
        // Hostile encoded for an HTML attribute, then escaped for a JavaScript string literal
        // (the '&' of every entity becomes &, which the JS engine turns back into '&')
        private const string HostileAttrThenJs = "a\\u0026#39;b\\u0026quot;c\\u0026lt;/script\\u0026gt;";

        private static LayuiTestVM NewVm() => new LayuiTestVM();

        private static string UnescapeJs(string s)
        {
            return Regex.Replace(s, @"\\u([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }

        #region tab

        [TestMethod]
        public void Tab_ChartResize_DoesNotUseEval()
        {
            var html = Render(new TabTagHelper { SelectedIndex = 1 }, "wt:tab", childContent: "x");

            AssertNotContains(html, "eval(");
            AssertContains(html,
                "var c = window[$(this).attr('id') + 'Chart']; if (c && c.resize) { c.resize(); }",
                "layui.element.on('tab(");
        }

        #endregion

        #region color picker

        [TestMethod]
        public void ColorPicker_ColorLiteral_IsEscaped()
        {
            var vm = NewVm();
            vm.Color = Hostile;

            var html = Render(new ColorPickerTagHelper { Field = Field(vm, x => x.Color) }, "wt:colorpicker");

            AssertContains(html, ",color:'" + HostileJs + "'");
            AssertNotContains(html, ",color:'a'b", "c</script>'\n");
        }

        [TestMethod]
        public void ColorPicker_PredefinedColors_AreEscaped()
        {
            var vm = NewVm();
            vm.Color = "#ff0000";

            var html = Render(new ColorPickerTagHelper { Field = Field(vm, x => x.Color), PredefinedColors = "#111111," + Hostile }, "wt:colorpicker");

            AssertContains(html, ",colors: ['#111111','" + HostileJs + "']");
            AssertNotContains(html, "'a'b");
        }

        [TestMethod]
        public void ColorPicker_OrdinaryValue_IsUnchanged()
        {
            var vm = NewVm();
            vm.Color = "#ff0000";

            var html = Render(new ColorPickerTagHelper { Field = Field(vm, x => x.Color), PredefinedColors = "#111111,#222222" }, "wt:colorpicker");

            AssertContains(html, ",color:'#ff0000'", ",colors: ['#111111','#222222']");
        }

        #endregion

        #region slider

        [TestMethod]
        public void Slider_HostileDefaultValue_IsDropped()
        {
            var helper = new SliderTagHelper { Field = Field(NewVm(), x => x.Score), DefaultValue = "1});alert(1);//" };

            var html = Render(helper, "wt:slider");

            AssertNotContains(html, "alert(1)", ",value:");
        }

        [TestMethod]
        public void Slider_HostileRangeDefaultValue_IsDropped()
        {
            var helper = new SliderTagHelper
            {
                Field = Field(NewVm(), x => x.Memo),
                Field1 = Field(NewVm(), x => x.Color),
                DefaultValue = "[1,2]);alert(1);//",
            };

            var html = Render(helper, "wt:slider");

            AssertNotContains(html, "alert(1)", ",value:");
        }

        [TestMethod]
        public void Slider_NumericAndArrayDefaultValues_AreUnchanged()
        {
            AssertContains(Render(new SliderTagHelper { Field = Field(NewVm(), x => x.Score), DefaultValue = "30" }, "wt:slider"),
                ",value:30\n");
            AssertContains(Render(new SliderTagHelper { Field = Field(NewVm(), x => x.Score), DefaultValue = "-1.5" }, "wt:slider"),
                ",value:-1.5\n");
            AssertContains(Render(new SliderTagHelper
            {
                Field = Field(NewVm(), x => x.Memo),
                Field1 = Field(NewVm(), x => x.Color),
                DefaultValue = "[10, 20]",
            }, "wt:slider"), ",value:[10, 20]\n");
        }

        [TestMethod]
        public void Slider_NoDefaultValue_OmitsValue()
        {
            AssertNotContains(Render(new SliderTagHelper { Field = Field(NewVm(), x => x.Score) }, "wt:slider"), ",value:");
        }

        #endregion

        #region upload

        private static LayuiTestVM VmWithCs(string cs)
        {
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.CurrentCS = cs;
            return new LayuiTestVM { Wtm = wtm };
        }

        [TestMethod]
        public void Upload_ExistingFilePreviewUrls_AreAttributeEncodedThenEscaped()
        {
            var vm = VmWithCs(Hostile);
            vm.PhotoId = Guid.NewGuid();
            var helper = new UploadTagHelper { Field = Field(vm, x => x.PhotoId), ShowPreview = true };

            var html = Render(helper, "wt:upload", FormItems(vm));

            AssertContains(html,
                "src=\"/_Framework/GetFile?id=" + vm.PhotoId + "&stream=true&width=64&height=64&_DONOT_USE_CS=" + HostileAttrThenJs + "\"");
            // once the JavaScript engine has decoded the literal, the value is still entity encoded in the attribute
            var decoded = UnescapeJs(html);
            Assert.IsTrue(decoded.Contains("&_DONOT_USE_CS=a&#39;b&quot;c&lt;/script&gt;\""), decoded);
            Assert.IsFalse(decoded.Contains("_DONOT_USE_CS=a'b\"c</script>\""), decoded);
        }

        [TestMethod]
        public void Upload_ExistingFileDownloadLink_IsAttributeEncodedThenEscaped()
        {
            var vm = VmWithCs(Hostile);
            vm.PhotoId = Guid.NewGuid();
            var helper = new UploadTagHelper { Field = Field(vm, x => x.PhotoId), Disabled = true };

            var html = Render(helper, "wt:upload", FormItems(vm));

            AssertContains(html, "href='/_Framework/GetFile/" + vm.PhotoId + "?_DONOT_USE_CS=" + HostileAttrThenJs + "'");
            Assert.IsTrue(UnescapeJs(html).Contains("?_DONOT_USE_CS=a&#39;b&quot;c&lt;/script&gt;'>"));
        }

        [TestMethod]
        public void Upload_HostileFileId_IsNeutralisedInAttributesAndLiterals()
        {
            var vm = VmWithCs("cs");
            vm.Memo = Hostile;
            var helper = new UploadTagHelper { Field = Field(vm, x => x.Memo), ShowPreview = true };

            var html = Render(helper, "wt:upload", FormItems(vm));

            AssertContains(html, "?id=" + HostileAttrThenJs + "&stream=true", "DoPreview('" + HostileJs + "')", "DoDelete('" + HostileJs + "')");
            AssertNotContains(html, "id=a'b", "DoPreview('a'b", "DoDelete('a'b");
        }

        [TestMethod]
        public void Upload_OrdinaryValues_AreUnchanged()
        {
            var vm = VmWithCs("default");
            vm.PhotoId = Guid.NewGuid();

            var preview = Render(new UploadTagHelper { Field = Field(vm, x => x.PhotoId), ShowPreview = true }, "wt:upload", FormItems(vm));
            var link = Render(new UploadTagHelper { Field = Field(vm, x => x.PhotoId), Disabled = true }, "wt:upload", FormItems(vm));

            AssertContains(preview,
                "src=\"/_Framework/GetFile?id=" + vm.PhotoId + "&stream=true&width=64&height=64&_DONOT_USE_CS=default\"",
                "DoPreview('" + vm.PhotoId + "')");
            AssertContains(link, "href='/_Framework/GetFile/" + vm.PhotoId + "?_DONOT_USE_CS=default'");
        }

        #endregion

        #region multi upload

        private static LayuiTestVM VmWithFile(string cs, Guid id)
        {
            var vm = VmWithCs(cs);
            vm.Files = new List<TestSubFile> { new TestSubFile { FileId = id } };
            return vm;
        }

        [TestMethod]
        public void MultiUpload_ExistingFileUrls_AreAttributeEncodedThenEscaped()
        {
            var id = Guid.NewGuid();
            var vm = VmWithFile(Hostile, id);

            var preview = Render(new MultiUploadTagHelper { Field = Field(vm, x => x.Files), ShowPreview = true }, "wt:multiupload", FormItems(vm));
            var link = Render(new MultiUploadTagHelper { Field = Field(vm, x => x.Files), Disabled = true }, "wt:multiupload", FormItems(vm));

            AssertContains(preview,
                // markup created after a new file has been uploaded
                "layer-src=\"/_Framework/GetFile?id='+ res.Data.Id+'&_DONOT_USE_CS=" + HostileAttrThenJs + "\"",
                "src=\"/_Framework/GetFile?id='+ res.Data.Id+'&stream=true&width=64&height=64&_DONOT_USE_CS=" + HostileAttrThenJs + "\"",
                "layer-src=\"/_Framework/GetFile/" + id + "?_DONOT_USE_CS=" + HostileAttrThenJs + "\"",
                "src=\"/_Framework/GetFile?id=" + id + "&stream=true&width=64&height=64&_DONOT_USE_CS=" + HostileAttrThenJs + "\"");
            AssertContains(link, "href='/_Framework/GetFile/" + id + "?_DONOT_USE_CS=" + HostileAttrThenJs + "'");
            foreach (var html in new[] { preview, link })
            {
                var decoded = UnescapeJs(html);
                Assert.IsTrue(decoded.Contains("_DONOT_USE_CS=a&#39;b&quot;c&lt;/script&gt;"), decoded);
                Assert.IsFalse(decoded.Contains("_DONOT_USE_CS=a'b\"c</script>\""), decoded);
            }
        }

        [TestMethod]
        public void MultiUpload_OrdinaryValues_AreUnchanged()
        {
            var id = Guid.NewGuid();
            var vm = VmWithFile("default", id);

            var preview = Render(new MultiUploadTagHelper { Field = Field(vm, x => x.Files), ShowPreview = true }, "wt:multiupload", FormItems(vm));
            var link = Render(new MultiUploadTagHelper { Field = Field(vm, x => x.Files), Disabled = true }, "wt:multiupload", FormItems(vm));

            AssertContains(preview,
                "layer-src=\"/_Framework/GetFile/" + id + "?_DONOT_USE_CS=default\"",
                "src=\"/_Framework/GetFile?id=" + id + "&stream=true&width=64&height=64&_DONOT_USE_CS=default\"",
                "label id=\"label" + id + "\"",
                "$('#preview" + id + "').on('click'",
                "var LayuiTestVM_Filesselected = [\"" + id + "\"];");
            AssertContains(link, "href='/_Framework/GetFile/" + id + "?_DONOT_USE_CS=default'");
        }

        #endregion
    }
}
