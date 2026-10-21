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
    /// A grid with local data and editable cells inside a form (the detail rows of a master / detail record) on /Lab/Detail:
    /// rows are added and removed in the browser, text, combo box and date cells are edited in place, and the rows are posted with the form
    /// as DetailRows[n].Field.
    /// </summary>
    [TestClass]
    public class LayUiEditableGridTests : LabTest
    {
        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Detail");
            await Expect(Body.Locator(".layui-table-tool")).ToBeVisibleAsync();
        }

        private ILocator Rows => Body.Locator(".layui-table-main tr");
        private ILocator AddRow => Body.Locator(".layui-table-tool a.layui-btn", new() { HasTextString = "Create" });
        private ILocator Cell(int row, string field) => Body.Locator($"[name='DetailRows[{row}].{field}']");

        [TestMethod]
        public async Task Grid_StartsEmpty_AndAddRowAppendsEditableRows()
        {
            await Expect(Rows).ToHaveCountAsync(0);

            await AddRow.ClickAsync();
            await AddRow.ClickAsync();

            await Expect(Rows).ToHaveCountAsync(2);
            await Expect(Cell(0, "Name")).ToBeVisibleAsync();
            await Expect(Cell(1, "Kind")).ToBeVisibleAsync();
            await Expect(Cell(1, "Day")).ToBeVisibleAsync();
        }

        [TestMethod]
        public async Task EditedCells_ArePostedWithTheForm_AsDetailRows()
        {
            await Lab.Field("Title").FillAsync("master");
            await AddRow.ClickAsync();
            await AddRow.ClickAsync();
            await Cell(0, "Name").FillAsync(LabData.Typed);
            await Cell(1, "Name").FillAsync("second");
            await Cell(1, "Kind").SelectOptionAsync("Toy");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Detail");

            var form = await Lab.LastPostAsync("Detail");
            CollectionAssert.AreEqual(new[] { LabData.Typed }, form["DetailRows[0].Name"]);
            CollectionAssert.AreEqual(new[] { "second" }, form["DetailRows[1].Name"]);
            CollectionAssert.AreEqual(new[] { "Toy" }, form["DetailRows[1].Kind"]);
        }

        [TestMethod]
        public async Task EditedPlainText_SurvivesTheGridBeingDrawnAgain()
        {
            await AddRow.ClickAsync();
            await Cell(0, "Name").FillAsync("plain & simple");
            await Cell(0, "Kind").SelectOptionAsync("Appliance");

            // adding a row draws the grid again from the values the browser keeps for the cells
            await AddRow.ClickAsync();

            await Expect(Rows).ToHaveCountAsync(2);
            await Expect(Cell(0, "Name")).ToHaveValueAsync("plain & simple");
            await Expect(Cell(0, "Kind")).ToHaveValueAsync("Appliance");
        }

        [TestMethod]
        [Ignore("#279: the text of a cell that contains a single quote is cut at the quote when the grid is drawn again (a row is added or removed), because the browser keeps the cell as an HTML string and writes the new value into it without encoding.")]
        public async Task EditedText_WithQuotes_SurvivesTheGridBeingDrawnAgain()
        {
            await AddRow.ClickAsync();
            await Cell(0, "Name").FillAsync(LabData.Typed);

            // adding a row draws the grid again from the values the browser keeps for the cells
            await AddRow.ClickAsync();

            await Expect(Rows).ToHaveCountAsync(2);
            await Expect(Cell(0, "Name")).ToHaveValueAsync(LabData.Typed);
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Detail");
            CollectionAssert.AreEqual(new[] { LabData.Typed }, (await Lab.LastPostAsync("Detail"))["DetailRows[0].Name"]);
        }

        [TestMethod]
        public async Task DateCell_OpensTheDatePicker_AndStoresThePickedDay()
        {
            await AddRow.ClickAsync();

            await Cell(0, "Day").ClickAsync();
            var panel = Page.Locator(".layui-laydate");
            await Expect(panel).ToBeVisibleAsync();
            await panel.Locator("td[lay-ymd]:not(.laydate-day-prev):not(.laydate-day-next)").First.ClickAsync();

            await Expect(Cell(0, "Day")).ToHaveValueAsync(new Regex(@"^\d{4}-\d{2}-01$"));
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Detail");
            Assert.IsTrue(Regex.IsMatch((await Lab.LastPostAsync("Detail"))["DetailRows[0].Day"].Single(), @"^\d{4}-\d{2}-01$"));
        }

        [TestMethod]
        public async Task RemoveRow_DeletesTheRow_AndRenumbersTheRemainingOnes()
        {
            await AddRow.ClickAsync();
            await AddRow.ClickAsync();
            await AddRow.ClickAsync();
            await Cell(0, "Name").FillAsync("first");
            await Cell(1, "Name").FillAsync("second");
            await Cell(2, "Name").FillAsync("third");

            // remove the middle row with its own button
            await Rows.Nth(1).Locator("a", new() { HasTextString = "Delete" }).ClickAsync();

            await Expect(Rows).ToHaveCountAsync(2);
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Detail");
            var form = await Lab.LastPostAsync("Detail");
            CollectionAssert.AreEqual(new[] { "first" }, form["DetailRows[0].Name"]);
            CollectionAssert.AreEqual(new[] { "third" }, form["DetailRows[1].Name"]);
            Assert.IsFalse(form.ContainsKey("DetailRows[2].Name"));
        }
    }
}
