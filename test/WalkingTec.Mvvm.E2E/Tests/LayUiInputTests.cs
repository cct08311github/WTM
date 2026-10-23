using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// Text input tag helpers on /Lab/Inputs: wt:textbox (also as password), wt:textarea, wt:richtextbox, wt:hidden, wt:display,
    /// wt:fieldset, wt:slider, wt:colorpicker, wt:form, wt:submitbutton, wt:resetbutton. The seeded values contain characters that
    /// need HTML / JavaScript encoding; they have to be shown as text, and posted back unchanged.
    /// </summary>
    [TestClass]
    public class LayUiInputTests : LabTest
    {
        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Inputs");
        }

        [TestMethod]
        public async Task TextFields_Render_SpecialCharactersAreShownAsText()
        {
            await Expect(Lab.Field("Title")).ToHaveValueAsync(LabData.Special);
            await Expect(Lab.Field("Title")).ToHaveAttributeAsync("placeholder", "Type a title");
            await Expect(Lab.Field("Memo")).ToHaveValueAsync(LabData.Special);
            await Expect(Lab.Field("Shown")).ToHaveTextAsync(LabData.Special);
            await Expect(Lab.Field("ShownAmount")).ToHaveTextAsync("1234.50");
            await Expect(Body.Locator("input[type=hidden][name=Keep]")).ToHaveValueAsync(LabData.Special);
            // the fieldset legend and the text next to the password box are tag helper text, not field values
            await Expect(Body.Locator("fieldset legend").First).ToHaveTextAsync(LabData.Special);
            await Expect(Body.Locator(".layui-word-aux")).ToHaveTextAsync(LabData.Special);
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task TextBox_Password_IsMaskedAndRequiredFieldIsMarked()
        {
            await Expect(Lab.Field("Secret")).ToHaveAttributeAsync("type", "password");
            await Expect(Lab.Field("Title")).ToHaveAttributeAsync("type", "text");
            // [Required] adds the red asterisk and a client side check
            await Expect(Body.Locator("label[for=LabFormVM_Title] font")).ToHaveTextAsync("*");
            await Expect(Lab.Field("Title")).ToHaveAttributeAsync("lay-verify", "required");
            await Expect(Body.Locator("label[for=LabFormVM_Secret] font")).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Submit_PostsTypedValuesUnchanged()
        {
            await Lab.Field("Title").FillAsync(LabData.Typed);
            await Lab.Field("Secret").FillAsync("s3cret & <x>");
            await Lab.Field("Memo").FillAsync("line one\nline <two> & \"three\"");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Inputs");

            var posted = await Lab.LastPostAsync("Inputs");
            CollectionAssert.AreEqual(new[] { LabData.Typed }, posted["Title"]);
            CollectionAssert.AreEqual(new[] { "s3cret & <x>" }, posted["Secret"]);
            // the line break may arrive as LF or CRLF depending on how the form is serialised; the text itself must be unchanged
            Assert.AreEqual("line one\nline <two> & \"three\"", posted["Memo"].Single().Replace("\r\n", "\n"));
            // the hidden field is posted as it was rendered
            CollectionAssert.AreEqual(new[] { LabData.Special }, posted["Keep"]);
            // slider and colour values travel with the form
            CollectionAssert.AreEqual(new[] { "30" }, posted["Level"]);
            CollectionAssert.AreEqual(new[] { "20" }, posted["Low"]);
            CollectionAssert.AreEqual(new[] { "70" }, posted["High"]);
            CollectionAssert.AreEqual(new[] { "#1e9fff" }, posted["Color"]);
        }

        [TestMethod]
        public async Task Submit_WithEmptyRequiredField_IsBlockedOnTheClient()
        {
            var posts = 0;
            Page.Request += (_, r) => { if (r.Url.Contains("/Lab/Save")) posts++; };
            await Lab.Field("Title").FillAsync("");

            await Body.Locator("button[type=submit]", new() { HasTextString = "Save" }).ClickAsync();

            // layui shows its own message and marks the field; nothing is sent
            await Expect(Page.Locator(".layui-layer-msg")).ToContainTextAsync("Title");
            await Expect(Lab.Field("Title")).ToHaveClassAsync(new Regex("layui-form-danger"));
            Assert.AreEqual(0, posts, "The form must not be posted while a required field is empty.");
        }

        [TestMethod]
        public async Task Reflect_RendersThePostedValuesAgainWithoutChangingThem()
        {
            await Lab.Field("Title").FillAsync(LabData.Typed);
            await Lab.Field("Memo").FillAsync("memo & <b>");

            await Lab.SubmitAsync("Reflect", "/Lab/Reflect");

            // the server rendered the page again from the posted view model (same path as a validation failure of an edit form)
            await Expect(Lab.Field("Title")).ToHaveValueAsync(LabData.Typed);
            await Expect(Lab.Field("Memo")).ToHaveValueAsync("memo & <b>");
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task ResetButton_RestoresTheRenderedValues()
        {
            await Lab.Field("Title").FillAsync("changed");
            await Lab.Field("Memo").FillAsync("changed too");

            await Body.Locator("button[type=reset]").ClickAsync();

            await Expect(Lab.Field("Title")).ToHaveValueAsync(LabData.Special);
            await Expect(Lab.Field("Memo")).ToHaveValueAsync(LabData.Special);
        }

        [TestMethod]
        public async Task Slider_Drag_UpdatesTheValueThatIsPosted()
        {
            var track = Body.Locator("#_sliderLabFormVM_Level .layui-slider");
            var box = await track.BoundingBoxAsync();
            await track.ClickAsync(new() { Position = new Position { X = (float)(box.Width * 0.5), Y = box.Height / 2 } });

            await Expect(Body.Locator("input[type=hidden][name=Level]")).ToHaveValueAsync("50");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Inputs");
            CollectionAssert.AreEqual(new[] { "50" }, (await Lab.LastPostAsync("Inputs"))["Level"]);
        }

        [TestMethod]
        public async Task Slider_RangeType_PostsBothEnds()
        {
            // the range slider writes its lower end to field and its upper end to field1
            var low = Body.Locator("input[type=hidden][name=Low]");
            var high = Body.Locator("input[type=hidden][name=High]");
            await Expect(low).ToHaveValueAsync("20");
            await Expect(high).ToHaveValueAsync("70");
            await Expect(Body.Locator("#_sliderLabFormVM_Low .layui-slider-wrap")).ToHaveCountAsync(2);
        }

        [TestMethod]
        public async Task Slider_DecimalValue_IsRenderedAndPostedUnchanged()
        {
            // Rate is a decimal view model property; layui snaps the handle to the step (1), the hidden input keeps the bound value
            await Expect(Body.Locator("input[type=hidden][name=Rate]")).ToHaveValueAsync("30.5");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Inputs");
            CollectionAssert.AreEqual(new[] { "30.5" }, (await Lab.LastPostAsync("Inputs"))["Rate"]);
        }

        [TestMethod]
        public async Task Slider_InitialPosition_ShowsTheBoundValue()
        {
            // Level is 30 on a 0..100 scale; the handle is expected at 30 percent
            await Expect(Body.Locator("#_sliderLabFormVM_Level .layui-slider-wrap").First).ToHaveAttributeAsync("style", new Regex("left:\\s*30%"));
        }

        [TestMethod]
        public async Task ColorPicker_Choose_UpdatesTheHiddenValue()
        {
            await Expect(Body.Locator("#LabFormVM_Color")).ToHaveValueAsync("#1e9fff");

            await Body.Locator("#cp_LabFormVM_Color .layui-colorpicker").ClickAsync();
            var panel = Page.Locator(".layui-colorpicker-main");
            await panel.Locator(".layui-colorpicker-main-input input").FillAsync("#ff5722");
            await panel.Locator("[colorpicker-events=confirm]").ClickAsync();

            await Expect(Body.Locator("#LabFormVM_Color")).ToHaveValueAsync("#ff5722");
        }

        [TestMethod]
        public async Task RichTextBox_Edit_IsPostedAsHtml()
        {
            var editor = Page.FrameLocator("#LAY_layedit_1");
            await Expect(editor.Locator("body")).ToContainTextAsync("Rich bold");

            await editor.Locator("body").ClickAsync();
            await editor.Locator("body").PressAsync("Control+A");
            await editor.Locator("body").PressSequentiallyAsync("edited text");
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Inputs");

            // the rich text box is the one field whose value is HTML by design
            StringAssert.Contains((await Lab.LastPostAsync("Inputs"))["Rich"].Single(), "edited text");
        }
    }
}
