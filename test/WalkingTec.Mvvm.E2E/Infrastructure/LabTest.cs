using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;
using WalkingTec.Mvvm.E2E.Pages;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Synthetic text of the Lab fixture pages (the same values the test host seeds, see LabText in the host). The host cannot be
    /// referenced from here: it runs as its own process.
    /// </summary>
    public static class LabData
    {
        /// <summary>Characters that need HTML / JavaScript encoding. Must be shown as text and posted back unchanged.</summary>
        public const string Special = "Tom & \"Jerry\" 'it' <i id=\"e2e-probe\">tag</i> </textarea></script> \\ ${x} {{y}} 100% 測試";

        /// <summary>The same idea for the text of list items; the matching element id is "e2e-probe-item".</summary>
        public const string SpecialItem = "A&B <i id=\"e2e-probe-item\">x</i> \"q\" 'q'";

        /// <summary>Another value with the characters, used for typed input.</summary>
        public const string Typed = "Typed & \"two\" 'three' <b id=\"e2e-probe-typed\">four</b> \\ 100%";
    }

    /// <summary>Base class of the tests that run against the Lab fixture pages: starts signed in and exposes the page helpers.</summary>
    public abstract class LabTest : E2ETest
    {
        protected override bool StartSignedIn => true;

        protected LabPage Lab { get; private set; }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestInitialize]
        public void CreateLabPage()
        {
            Lab = new LabPage(Page);
        }

        protected ILocator Body => Lab.Body;

        /// <summary>The xm-select based combo box / tree of a Lab form field.</summary>
        protected ILocator Combo(string property) => Body.Locator($"#LabFormVM_{property} xm-select");

        /// <summary>Opens an xm-select, clicks the option with the given text and closes the list.</summary>
        protected async Task PickComboAsync(string property, string optionText)
        {
            var combo = Combo(property);
            await combo.ClickAsync();
            await combo.Locator(".xm-option", new() { HasTextString = optionText }).First.ClickAsync();
            // single selects close themselves; multi selects stay open, click on the label of the form to close
            await Body.Locator($"label[for=LabFormVM_{property}]").ClickAsync();
        }

        /// <summary>The raw values of a form field in the hidden input(s) / inputs xm-select writes (name = property).</summary>
        protected ILocator SelectedValueInputs(string name) => Body.Locator($"input[name='{name}']:not([type=text])");

        /// <summary>Waits for the framework's alert with the text and closes it.</summary>
        protected async Task AcceptAlertAsync(string text)
        {
            var alert = Page.Locator(".layui-layer-dialog");
            await Expect(alert).ToContainTextAsync(text);
            await alert.Locator(".layui-layer-btn0").ClickAsync();
            await Expect(alert).ToHaveCountAsync(0);
        }

        /// <summary>
        /// Asserts that no element was created from the markup-like fragments of the fixture text (they carry ids that start with
        /// "e2e-probe"). Pass the part of the page the check is about; without it the whole page is searched.
        /// </summary>
        protected async Task ExpectNoProbeElementsAsync(ILocator scope = null)
        {
            await Expect((scope ?? Page.Locator("body")).Locator("[id^='e2e-probe']")).ToHaveCountAsync(0);
        }

        /// <summary>Waits until a JavaScript expression evaluates to true in the page.</summary>
        protected async Task ExpectScriptAsync(string expression)
        {
            await Page.WaitForFunctionAsync(expression);
        }
    }
}
