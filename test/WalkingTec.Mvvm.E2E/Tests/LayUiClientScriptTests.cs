using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// The request helpers of framework_layui.js that the tag helpers generate calls to: a form posted into a dialog (ff.GetPostData),
    /// a failing form submit, background calls that fail or answer with a message, and the dialog window buttons.
    /// </summary>
    [TestClass]
    public class LayUiClientScriptTests : LabTest
    {
        private ILocator Dialog => Page.Locator(".layui-layer-page");

        [TestMethod]
        public async Task LinkButton_PostCurrentForm_SendsTheFormValuesIntoTheDialog()
        {
            await Lab.OpenAsync("Inputs");
            await Lab.Field("Title").FillAsync(LabData.Typed);

            await Body.Locator("#lab-post-form").ClickAsync();

            // the dialog page received the form as it is (the title with the characters), and shows it as text
            await Expect(Dialog).ToContainTextAsync("Echo");
            await Expect(Dialog.Locator("#lab-echo-title")).ToHaveTextAsync(LabData.Typed);
            await ExpectNoProbeElementsAsync(Dialog);
        }

        [TestMethod]
        public async Task LinkButton_PostCurrentForm_ChecksTheRequiredFieldsFirst()
        {
            await Lab.OpenAsync("Inputs");
            await Lab.Field("Title").FillAsync("");

            await Body.Locator("#lab-post-form").ClickAsync();

            await Expect(Page.Locator(".layui-layer-msg")).ToContainTextAsync("Title");
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Submit_ToAnUrlThatFails_TellsTheUser()
        {
            await Lab.OpenAsync("Inputs");
            var dialogs = new List<string>();
            Page.Dialog += (_, d) => { dialogs.Add(d.Message); d.AcceptAsync(); };

            await Body.Locator("button[type=submit]", new() { HasTextString = "Fail" }).ClickAsync();

            // PostForm shows the browser alert with the framework's "submit failed" text
            await Expect(Page.Locator(".layui-layer-loading")).ToHaveCountAsync(0);
            await ExpectScriptAsync("() => true");
            Assert.AreEqual(1, dialogs.Count, "Expected the failure alert of PostForm.");
            Assert.AreEqual(await Page.EvaluateAsync<string>("() => ff.DONOTUSE_Text_SubmitFailed"), dialogs[0]);
        }

        [TestMethod]
        public async Task BackgroundCall_ThatAnswersWithAMessage_ShowsIt()
        {
            await Lab.OpenAsync("Layout");

            await Body.Locator("#lab-link-message").ClickAsync();

            await Expect(Page.Locator(".layui-layer-msg")).ToContainTextAsync("hello message");
        }

        [TestMethod]
        public async Task BackgroundCall_ThatFails_ShowsTheErrorTextOfTheServer()
        {
            await Lab.OpenAsync("Layout");

            await Body.Locator("#lab-link-fail").ClickAsync();

            // the framework's error handler answers with the exception message (details are not exposed)
            await Expect(Page.Locator(".layui-layer-dialog")).ToContainTextAsync("Lab: simulated failure");
        }

        [TestMethod]
        public async Task Dialog_WhoseContentFailsToLoad_ShowsTheErrorAndOpensNoWindow()
        {
            await Lab.OpenAsync("Layout");

            await Body.Locator("#lab-link-faildialog").ClickAsync();

            await Expect(Page.Locator(".layui-layer-dialog")).ToContainTextAsync("Lab: simulated failure");
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Dialog_ResizeHandle_ChangesTheSizeOfTheWindow()
        {
            await Lab.OpenAsync("Layout");
            await Body.Locator("#lab-link-dialog").ClickAsync();
            await Expect(Dialog).ToContainTextAsync("popup content");
            const string width = "() => document.querySelector('.layui-layer-page').offsetWidth";
            await ExpectScriptAsync(width + " === 500");
            // the window opens with a scale animation: wait until it has its real size before aiming at the handle
            await ExpectScriptAsync("() => { const t = getComputedStyle(document.querySelector('.layui-layer-page')).transform; return t === 'none' || t === 'matrix(1, 0, 0, 1, 0, 0)'; }");

            var handle = await Page.Locator(".layui-layer-resize").BoundingBoxAsync();
            var x = handle.X + handle.Width / 2;
            var y = handle.Y + handle.Height / 2;
            await Page.Mouse.MoveAsync(x, y);
            await Page.Mouse.DownAsync();
            await Page.Mouse.MoveAsync(x + 100, y + 60, new() { Steps = 5 });
            await Page.Mouse.UpAsync();

            await ExpectScriptAsync(width + " > 550");
        }

        [TestMethod]
        public async Task Dialog_MaximizeAndRestore_ChangeTheWindowSize()
        {
            await Lab.OpenAsync("Layout");
            await Body.Locator("#lab-link-dialog").ClickAsync();
            await Expect(Dialog).ToContainTextAsync("popup content");
            // offsetWidth ignores the scale animation of the opening window
            const string width = "() => document.querySelector('.layui-layer-page').offsetWidth";
            await ExpectScriptAsync(width.Replace("() =>", "() =>") + " === 500");

            await Page.Locator(".layui-layer-max").ClickAsync();
            await ExpectScriptAsync(width + " > 1000");

            // after maximizing, the same icon restores the window
            await Page.Locator(".layui-layer-maxmin").ClickAsync();
            await ExpectScriptAsync(width + " === 500");
        }
    }
}
