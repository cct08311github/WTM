using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// A record whose text contains characters that need HTML / JavaScript encoding goes through the real generated Gadget module:
    /// typed into the create form, listed in the grid, shown in the details dialog, filled back into the edit form and saved again.
    /// The text has to come out the way it went in, and must never be interpreted as markup.
    /// </summary>
    [TestClass]
    public class SpecialCharacterRoundTripTests : E2ETest
    {
        protected override bool StartSignedIn => true;

        // The framework HTML-encodes every string it binds from a request before it checks the length, so these have to fit the 50 and 20
        // character limits of the Gadget fields after encoding (an ampersand counts 5, a quote 6).
        private const string Name = "A&B <i id=\"e2e-probe-n\">";
        private const string Code = "a&b<c>";

        private GadgetListPage _list;

        [TestInitialize]
        public async Task OpenList()
        {
            // the default setting (UIOptions:EncodeHtmlContent off): values the forms saved are already HTML-encoded by the framework
            await SetHtmlContentEncodingAsync(false);
            await new HomeFrame(Page).GotoAsync();
            _list = new GadgetListPage(Page);
            await _list.OpenAsync();
        }

        private async Task CreateAsync(string suffix)
        {
            await _list.ToolbarButton("Create").ClickAsync();
            await Expect(_list.DialogField("Name")).ToBeVisibleAsync();
            await _list.DialogField("Name").FillAsync(Name + suffix);
            await _list.DialogField("Code").FillAsync(Code);
            await _list.DialogField("Price").FillAsync("3");
            await _list.SubmitDialogAsync();
        }

        private async Task SearchAsync(string text)
        {
            await _list.SearchByNameAsync(text);
        }

        [TestMethod]
        public async Task Grid_ShowsTheSavedTextAsTyped_WithoutCreatingElements()
        {
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            await CreateAsync(suffix);

            await SearchAsync(suffix);

            await Expect(_list.Rows).ToHaveCountAsync(1);
            await Expect(_list.Rows.First.Locator("td[data-field=Name]")).ToHaveTextAsync(Name + suffix);
            await Expect(_list.Rows.First.Locator("td[data-field=Code]")).ToHaveTextAsync(Code);
            await Expect(Page.Locator("[id^='e2e-probe']")).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Grid_WithEncodeHtmlContentOn_ShowsTheSavedTextAsTyped()
        {
            await SetHtmlContentEncodingAsync(true);
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            await CreateAsync(suffix);

            await SearchAsync(suffix);

            await Expect(_list.Rows.First.Locator("td[data-field=Name]")).ToHaveTextAsync(Name + suffix);
        }

        [TestMethod]
        public async Task DetailsAndEditForm_ShowTheSavedTextAsTyped()
        {
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            await CreateAsync(suffix);
            await SearchAsync(suffix);

            await _list.RowButton(Name + suffix, "Details").ClickAsync();
            await Expect(_list.Dialog.Locator("#GadgetVM_Entity_Name")).ToHaveTextAsync(Name + suffix);
            await Expect(_list.Dialog.Locator("#GadgetVM_Entity_Code")).ToHaveTextAsync(Code);
            await Expect(Page.Locator("[id^='e2e-probe']")).ToHaveCountAsync(0);
            await _list.DialogButton("Close").ClickAsync();

            await _list.RowButton(Name + suffix, "Edit").ClickAsync();
            await Expect(_list.DialogField("Name")).ToHaveValueAsync(Name + suffix);
            await Expect(_list.DialogField("Code")).ToHaveValueAsync(Code);
        }

        [TestMethod]
        public async Task Edit_SavingWithoutChangingTheText_KeepsItUnchanged()
        {
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            await CreateAsync(suffix);
            await SearchAsync(suffix);

            // open, change only the price, save: the text fields go through the form once more
            await _list.RowButton(Name + suffix, "Edit").ClickAsync();
            await Expect(_list.DialogField("Name")).ToHaveValueAsync(Name + suffix);
            await _list.DialogField("Price").FillAsync("8");
            await _list.SubmitDialogAsync();

            await Expect(_list.Row(suffix)).ToContainTextAsync("8");
            await Expect(_list.Rows.First.Locator("td[data-field=Name]")).ToHaveTextAsync(Name + suffix);
            await Expect(_list.Rows.First.Locator("td[data-field=Code]")).ToHaveTextAsync(Code);
        }
    }
}
