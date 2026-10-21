using System.Collections.Generic;
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
    /// Choice tag helpers on /Lab/Choices: wt:checkbox, wt:radio, wt:switch, wt:combobox (enum, list items, linked, loaded from a url),
    /// wt:datetime (date, date time, range) and wt:transfer.
    /// </summary>
    [TestClass]
    public class LayUiChoiceTests : LabTest
    {
        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Choices");
        }

        private ILocator CheckBoxItem(string property, string title) => Body.Locator($"#LabFormVM_{property} .layui-form-checkbox", new() { HasTextString = title });
        private ILocator RadioItem(string property, string title) => Body.Locator($"#LabFormVM_{property} .layui-form-radio", new() { HasTextString = title });

        // the days of the month on show; the grid also holds trailing days of the previous and next month
        private const string CurrentMonthDay = "td[lay-ymd]:not(.laydate-day-prev):not(.laydate-day-next)";

        private static string[] Values(Dictionary<string, string[]> form, string name) => form.TryGetValue(name, out var v) ? v : new string[0];

        // ---- check box, radio, switch ---------------------------------------------------------------------------

        [TestMethod]
        public async Task CheckBox_ListItems_PostsTheCheckedValuesUnchanged()
        {
            // the item with the special characters starts checked (its value is the special text)
            await Expect(Body.Locator($"#LabFormVM_Flags input[type=checkbox]:checked")).ToHaveCountAsync(1);
            await CheckBoxItem("Flags", "Third choice").ClickAsync();

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            var flags = Values(await Lab.LastPostAsync("Choices"), "Flags");
            CollectionAssert.AreEquivalent(new[] { LabData.SpecialItem, "third" }, flags);
        }

        [TestMethod]
        public async Task Radio_EnumAndListItems_PostTheSelectedValue()
        {
            await RadioItem("Kind", "Toy").ClickAsync();
            await RadioItem("Choice", "Third choice").ClickAsync();

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            var form = await Lab.LastPostAsync("Choices");
            CollectionAssert.AreEqual(new[] { "Toy" }, Values(form, "Kind"));
            CollectionAssert.AreEqual(new[] { "third" }, Values(form, "Choice"));
        }

        [TestMethod]
        public async Task Radio_PreselectedItemWithSpecialValue_IsCheckedAndSubmittedUnchanged()
        {
            await Expect(Body.Locator("#LabFormVM_Choice input[type=radio]:checked")).ToHaveValueAsync(LabData.SpecialItem);

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            CollectionAssert.AreEqual(new[] { LabData.SpecialItem }, Values(await Lab.LastPostAsync("Choices"), "Choice"));
        }

        [TestMethod]
        [Ignore("Pending issue: after layui renders a check box / radio, the item title (attribute value) is inserted as HTML, so markup characters in the text become elements.")]
        public async Task CheckBoxAndRadio_ItemText_IsShownAsText()
        {
            await Expect(CheckBoxItem("Flags", "A&B").Locator("span")).ToHaveTextAsync(LabData.SpecialItem);
            await Expect(RadioItem("Choice", "A&B").Locator("div")).ToHaveTextAsync(LabData.SpecialItem);
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task Switch_Toggle_PostsTrueWhenOn()
        {
            var toggle = Body.Locator("#LabFormVM_Enabled + .layui-form-switch");
            await Expect(toggle).ToContainTextAsync("Off");
            await toggle.ClickAsync();
            await Expect(toggle).ToContainTextAsync("On");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            CollectionAssert.AreEqual(new[] { "true" }, Values(await Lab.LastPostAsync("Choices"), "Enabled"));
        }

        // ---- combo box ------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task ComboBox_EnumAndMultiSelect_PostTheSelection()
        {
            await PickComboAsync("Combo", "Appliance");
            // the multi select starts with the special item selected; add a second one
            await Expect(Combo("MultiCombo").Locator(".xm-label-block")).ToHaveCountAsync(1);
            await Combo("MultiCombo").ClickAsync();
            await Combo("MultiCombo").Locator(".xm-option", new() { HasTextString = "Third choice" }).ClickAsync();
            await Body.Locator("label[for=LabFormVM_MultiCombo]").ClickAsync();
            await Expect(Combo("MultiCombo").Locator(".xm-label-block")).ToHaveCountAsync(2);

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            var form = await Lab.LastPostAsync("Choices");
            CollectionAssert.AreEqual(new[] { "Appliance" }, Values(form, "Combo"));
            CollectionAssert.AreEquivalent(new[] { LabData.SpecialItem, "third" }, Values(form, "MultiCombo"));
        }

        [TestMethod]
        public async Task ComboBox_Linked_LoadsTheDependentListAfterTheFirstChoice()
        {
            await Combo("City").ClickAsync();
            await Expect(Combo("City").Locator(".xm-option")).ToHaveCountAsync(0);
            await Body.Locator("label[for=LabFormVM_City]").ClickAsync();

            await PickComboAsync("Region", "North");

            // the dependent combo box asked /Lab/Cities for its items
            await Combo("City").ClickAsync();
            await Expect(Combo("City").Locator(".xm-option")).ToHaveCountAsync(2);
            await Combo("City").Locator(".xm-option", new() { HasTextString = "North city" }).ClickAsync();
            await Body.Locator("label[for=LabFormVM_City]").ClickAsync();

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");
            var form = await Lab.LastPostAsync("Choices");
            CollectionAssert.AreEqual(new[] { "north" }, Values(form, "Region"));
            CollectionAssert.AreEqual(new[] { "n1" }, Values(form, "City"));
        }

        [TestMethod]
        public async Task ComboBox_Linked_ChangingTheFirstChoiceReplacesTheDependentItems()
        {
            await PickComboAsync("Region", "North");
            await Combo("City").ClickAsync();
            await Expect(Combo("City").Locator(".xm-option")).ToHaveCountAsync(2);
            await Body.Locator("label[for=LabFormVM_City]").ClickAsync();

            await PickComboAsync("Region", "South");

            await Combo("City").ClickAsync();
            await Expect(Combo("City").Locator(".xm-option")).ToHaveCountAsync(1);
            await Expect(Combo("City").Locator(".xm-option")).ToHaveTextAsync("South city");
        }

        [TestMethod]
        public async Task LoadComboItems_FromUrl_FillsComboRadioAndCheckBox()
        {
            // item-url: the three controls load their items from /Lab/Remote after the page is shown
            await Combo("Remote").ClickAsync();
            await Expect(Combo("Remote").Locator(".xm-option")).ToHaveCountAsync(3);
            await Combo("Remote").Locator(".xm-option", new() { HasTextString = "Third choice" }).ClickAsync();
            await Expect(RadioItem("RemoteRadio", "Plain choice")).ToBeVisibleAsync();
            await RadioItem("RemoteRadio", "Plain choice").ClickAsync();
            await CheckBoxItem("RemoteCheck", "Plain choice").ClickAsync();
            await CheckBoxItem("RemoteCheck", "Third choice").ClickAsync();

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            var form = await Lab.LastPostAsync("Choices");
            CollectionAssert.AreEqual(new[] { "third" }, Values(form, "Remote"));
            CollectionAssert.AreEqual(new[] { "plain" }, Values(form, "RemoteRadio"));
            CollectionAssert.AreEquivalent(new[] { "plain", "third" }, Values(form, "RemoteCheck"));
        }

        [TestMethod]
        [Ignore("Pending issue: xm-select writes the option text into its label as HTML, so markup characters in the text of a combo box item become elements.")]
        public async Task ComboBox_ItemText_IsShownAsText()
        {
            await Expect(Combo("MultiCombo").Locator(".xm-label-block span")).ToHaveTextAsync(LabData.SpecialItem);
            await ExpectNoProbeElementsAsync();
        }

        [TestMethod]
        public async Task ComboBox_SearchEnabled_FiltersTheOptions()
        {
            await Combo("MultiCombo").ClickAsync();
            await Combo("MultiCombo").Locator(".xm-search-input").FillAsync("Third");
            await Expect(Combo("MultiCombo").Locator(".xm-option:visible")).ToHaveCountAsync(1);
            await Expect(Combo("MultiCombo").Locator(".xm-option:visible")).ToContainTextAsync("Third choice");
        }

        // ---- date and time --------------------------------------------------------------------------------------

        [TestMethod]
        public async Task DateTime_PickDayAndMoment_PostsFormattedValues()
        {
            await Lab.Field("Day").ClickAsync();
            var panel = Page.Locator(".layui-laydate");
            await Expect(panel).ToBeVisibleAsync();
            await panel.Locator(CurrentMonthDay).First.ClickAsync();
            await Expect(Lab.Field("Day")).ToHaveValueAsync(new Regex(@"^\d{4}-\d{2}-01$"));
            // the date only picker closes itself
            await Expect(panel).ToHaveCountAsync(0);

            await Lab.Field("Moment").ClickAsync();
            var panel2 = Page.Locator(".layui-laydate");
            await panel2.Locator(CurrentMonthDay).First.ClickAsync();
            await panel2.Locator(".laydate-btns-confirm").ClickAsync();
            await Expect(Lab.Field("Moment")).ToHaveValueAsync(new Regex(@"^\d{4}-\d{2}-01 \d{2}:\d{2}:\d{2}$"));

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");
            var form = await Lab.LastPostAsync("Choices");
            Assert.IsTrue(Regex.IsMatch(Values(form, "Day").Single(), @"^\d{4}-\d{2}-01$"));
            Assert.IsTrue(Regex.IsMatch(Values(form, "Moment").Single(), @"^\d{4}-\d{2}-01 \d{2}:\d{2}:\d{2}$"));
        }

        [TestMethod]
        public async Task DateTime_Range_PicksTwoDaysAndPostsThemSeparatedByTheRangeMark()
        {
            await Lab.Field("Period").ClickAsync();
            var panel = Page.Locator(".layui-laydate-range");
            await Expect(panel).ToBeVisibleAsync();
            await panel.Locator(".layui-laydate-main").First.Locator(CurrentMonthDay).Nth(2).ClickAsync();
            await panel.Locator(".layui-laydate-main").First.Locator(CurrentMonthDay).Nth(8).ClickAsync();
            await panel.Locator(".laydate-btns-confirm").ClickAsync();

            await Expect(Lab.Field("Period")).ToHaveValueAsync(new Regex(@"^\d{4}-\d{2}-\d{2} ~ \d{4}-\d{2}-\d{2}$"));
            var shown = await Lab.Field("Period").InputValueAsync();
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");
            CollectionAssert.AreEqual(new[] { shown }, Values(await Lab.LastPostAsync("Choices"), "Period"));
        }

        [TestMethod]
        public async Task DateTime_TypedText_IsPostedAsTyped()
        {
            await Lab.Field("Day").FillAsync("2024-02-29");
            await Lab.Field("Day").PressAsync("Escape");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            CollectionAssert.AreEqual(new[] { "2024-02-29" }, Values(await Lab.LastPostAsync("Choices"), "Day"));
        }

        // ---- transfer -------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Transfer_MoveItemsBetweenTheLists_PostsTheItemsOnTheRight()
        {
            var transfer = Body.Locator("#LabFormVM_Picked");
            var left = transfer.Locator(".layui-transfer-box").First;
            var right = transfer.Locator(".layui-transfer-box").Nth(1);
            // p2 starts on the right: send it back, then move p1 over
            await Expect(right.Locator("li")).ToHaveCountAsync(1);
            await right.Locator("li .layui-form-checkbox").ClickAsync();
            await transfer.Locator(".layui-transfer-active .layui-btn[data-index='1']").ClickAsync();
            await Expect(right.Locator("li")).ToHaveCountAsync(0);
            await Expect(left.Locator("li")).ToHaveCountAsync(3);

            await left.Locator("li", new() { HasTextString = "First item" }).Locator(".layui-form-checkbox").ClickAsync();
            await transfer.Locator(".layui-transfer-active .layui-btn[data-index='0']").ClickAsync();
            await Expect(right.Locator("li")).ToHaveCountAsync(1);

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            CollectionAssert.AreEqual(new[] { "p1" }, Values(await Lab.LastPostAsync("Choices"), "Picked"));
        }

        [TestMethod]
        [Ignore("Pending issue: once any item is moved, the transfer posts only the items moved since the page was shown; the values that were preselected are dropped.")]
        public async Task Transfer_PreselectedValues_AreKeptWhenAnotherItemIsMoved()
        {
            var transfer = Body.Locator("#LabFormVM_Picked");
            await transfer.Locator(".layui-transfer-box").First.Locator("li", new() { HasTextString = "First item" }).Locator(".layui-form-checkbox").ClickAsync();
            await transfer.Locator(".layui-transfer-active .layui-btn[data-index='0']").ClickAsync();
            await Expect(transfer.Locator(".layui-transfer-box").Nth(1).Locator("li")).ToHaveCountAsync(2);

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Choices");

            CollectionAssert.AreEquivalent(new[] { "p1", "p2" }, Values(await Lab.LastPostAsync("Choices"), "Picked"));
        }

        [TestMethod]
        public async Task Transfer_Search_FiltersTheLeftList()
        {
            var left = Body.Locator("#LabFormVM_Picked .layui-transfer-box").First;
            await left.Locator(".layui-transfer-search input").PressSequentiallyAsync("Third");
            await Expect(left.Locator("li:visible")).ToHaveCountAsync(1);
        }

        [TestMethod]
        [Ignore("Pending issue: layui transfer writes the item title as HTML, so markup characters in the text of an item become elements.")]
        public async Task Transfer_ItemText_IsShownAsText()
        {
            var right = Body.Locator("#LabFormVM_Picked .layui-transfer-box").Nth(1);
            await Expect(right.Locator("li span")).ToHaveTextAsync(LabData.SpecialItem);
            await ExpectNoProbeElementsAsync();
        }
    }
}
