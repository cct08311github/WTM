using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages;
using WalkingTec.Mvvm.E2E.Security;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests.Security
{
    /// <summary>
    /// LayUI buttons follow the privileges of the signed-in user (#273): a grid action whose URL the user may not use is not written to the
    /// page at all (IsAccessable in the grid tag helper). The limited account's role may open the Gadget list, search, show details and
    /// export, nothing else; the administrator sees every action. The matrix tests check what the server answers when such a URL is
    /// requested anyway, this class checks what the page offers.
    /// </summary>
    [TestClass]
    [TestCategory("permission-matrix")]
    public class ButtonPrivilegeTests : E2ETest
    {
        private async Task<GadgetListPage> OpenListAsync(string account, string password)
        {
            await new LoginPage(Page).LoginAsync(account, password);
            var list = new GadgetListPage(Page);
            await list.OpenAsync();
            return list;
        }

        private async Task<string[]> ToolbarButtonsAsync(GadgetListPage list)
        {
            var texts = await list.ToolbarButtons.AllInnerTextsAsync();
            return texts.Select(x => x.Trim()).Where(x => x.Length > 0).OrderBy(x => x).ToArray();
        }

        [TestMethod]
        public async Task Administrator_SeesEveryGadgetAction()
        {
            var list = await OpenListAsync(Site.AdminAccount, Site.AdminPassword);

            CollectionAssert.AreEquivalent(
                new[] { "Batch delete", "Batch edit", "Create", "Export Excel", "Import" },
                await ToolbarButtonsAsync(list));
            var first = list.Rows.First;
            await Expect(first.Locator("a.layui-btn")).ToHaveCountAsync(3);
            foreach (var text in new[] { "Edit", "Delete", "Details" })
            {
                await Expect(first.Locator("a.layui-btn", new() { HasTextString = text })).ToHaveCountAsync(1);
            }
        }

        [TestMethod]
        public async Task LimitedRole_SeesOnlyTheActionsItMayUse()
        {
            var list = await OpenListAsync(MatrixAccounts.Limited, Site.MatrixLimitedPassword);

            // export is allowed, create, batch edit, batch delete and import are not written to the page
            CollectionAssert.AreEquivalent(new[] { "Export Excel" }, await ToolbarButtonsAsync(list));
            // details is allowed, edit and delete are not
            var first = list.Rows.First;
            await Expect(first.Locator("a.layui-btn")).ToHaveCountAsync(1);
            await Expect(first.Locator("a.layui-btn", new() { HasTextString = "Details" })).ToHaveCountAsync(1);
        }

        [TestMethod]
        public async Task AccountWithoutRole_HasNoGadgetMenu()
        {
            await new LoginPage(Page).LoginAsync(MatrixAccounts.NoRole, Site.MatrixNoRolePassword);

            await Expect(new HomeFrame(Page).UserName).ToHaveTextAsync("E2E No Role");
            await Expect(new HomeFrame(Page).MenuLink("Inventory")).ToHaveCountAsync(0);
        }
    }
}
