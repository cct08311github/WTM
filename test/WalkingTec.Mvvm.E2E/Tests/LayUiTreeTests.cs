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
    /// wt:tree (an xm-select tree, single and multiple selection) and wt:treecontainer (a layui tree that reports clicked nodes)
    /// on /Lab/Trees.
    /// </summary>
    [TestClass]
    public class LayUiTreeTests : LabTest
    {
        [TestInitialize]
        public async Task OpenPage()
        {
            await Lab.OpenAsync("Trees");
        }

        private ILocator TreeBox(string property) => Body.Locator($"#LabFormVM_{property} xm-select");

        private async Task CloseTreeAsync(string property)
        {
            await Body.Locator($"label[for=LabFormVM_{property}]").ClickAsync();
        }

        // ---- wt:tree --------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Tree_Single_ExpandABranchAndPickALeaf_PostsTheLeafValue()
        {
            var tree = TreeBox("Branch");
            await tree.ClickAsync();
            // the second branch starts collapsed: expand it with its arrow, then pick its leaf
            await tree.Locator(".xm-option[value=root2] .xm-tree-icon").ClickAsync();
            await tree.Locator(".xm-option[value=leaf2a]").ClickAsync();

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Trees");

            CollectionAssert.AreEqual(new[] { "leaf2a" }, (await Lab.LastPostAsync("Trees"))["Branch"]);
        }

        [TestMethod]
        public async Task Tree_Single_PreselectedValue_IsPostedWhenNothingChanges()
        {
            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Trees");

            CollectionAssert.AreEqual(new[] { "leaf1b" }, (await Lab.LastPostAsync("Trees"))["Branch"]);
        }

        [TestMethod]
        public async Task Tree_Multiple_PostsEverySelectedLeaf()
        {
            var tree = TreeBox("Branches");
            await Expect(tree.Locator(".xm-label-block")).ToHaveCountAsync(1);
            await tree.ClickAsync();
            await tree.Locator(".xm-option[value=root2] .xm-tree-icon").ClickAsync();
            await tree.Locator(".xm-option[value=leaf2a]").ClickAsync();
            await CloseTreeAsync("Branches");

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Trees");

            CollectionAssert.AreEquivalent(new[] { "leaf1a", "leaf2a" }, (await Lab.LastPostAsync("Trees"))["Branches"]);
        }

        [TestMethod]
        public async Task Tree_ItemsLoadedFromAUrl_CanBePicked()
        {
            var tree = TreeBox("RemoteBranch");
            await tree.ClickAsync();
            // the nodes come from /Lab/RemoteTree after the page is shown
            await Expect(tree.Locator(".xm-option[value=root1]")).ToBeVisibleAsync();
            await tree.Locator(".xm-option[value=root2] .xm-tree-icon").ClickAsync();
            await tree.Locator(".xm-option[value=leaf2a]").ClickAsync();

            await Lab.SubmitAsync("Save", "/Lab/Save");
            await AcceptAlertAsync("Saved Trees");

            CollectionAssert.AreEqual(new[] { "leaf2a" }, (await Lab.LastPostAsync("Trees"))["RemoteBranch"]);
        }

        [TestMethod]
        public async Task Tree_Search_ShowsOnlyMatchingNodes()
        {
            var tree = TreeBox("Branch");
            await tree.ClickAsync();
            await tree.Locator(".xm-search-input").PressSequentiallyAsync("2A");

            await Expect(tree.Locator(".xm-option:visible", new() { HasTextString = "Leaf 2A" })).ToHaveCountAsync(1);
            await Expect(tree.Locator(".xm-option:visible", new() { HasTextString = "Branch one" })).ToHaveCountAsync(0);
        }

        [TestMethod]
        public async Task Tree_NodeText_IsShownAsText()
        {
            await Expect(TreeBox("Branch").Locator(".label-content")).ToHaveTextAsync(LabData.SpecialItem);
            await ExpectNoProbeElementsAsync(TreeBox("Branch"));
        }

        [TestMethod]
        public async Task Tree_Linked_AValueThatCutsAnAddressReachesTheServerWhole()
        {
            var tree = TreeBox("LinkedBranch");
            await tree.ClickAsync();
            // the root starts collapsed: expand it with its arrow, then pick its leaf
            await tree.Locator(".xm-option[value=rootS] .xm-tree-icon").ClickAsync();
            await tree.Locator(".xm-option", new() { HasTextString = "Special node" }).ClickAsync();
            await Body.Locator("label[for=LabFormVM_LinkedBranch]").ClickAsync();

            // the dependent combo box asked /Lab/Cities for its items with the chosen value (& # + % and quotes in it) in the address
            await Combo("TreeCity").ClickAsync();
            await Expect(Combo("TreeCity").Locator(".xm-option")).ToHaveCountAsync(1);
            await Expect(Combo("TreeCity").Locator(".xm-option")).ToHaveTextAsync("Special city");
            await ExpectNoProbeElementsAsync();
        }

        // ---- wt:treecontainer -----------------------------------------------------------------------------------

        private ILocator Node(string id) => Body.Locator($"#lab-treecontainer-host .layui-tree-set[data-id={id}] > .layui-tree-entry .layui-tree-main").First;

        [TestMethod]
        public async Task TreeContainer_ClickANode_CallsTheClickFunctionWithTheNode()
        {
            await Node("leaf1a").ClickAsync();

            await ExpectScriptAsync("() => window.labTreeClickedIds.length === 1 && window.labTreeClickedIds[0] === 'leaf1a'");
            await Expect(Body.Locator("#lab-treecontainer-content")).ToHaveTextAsync("clicked leaf1a");
        }

        [TestMethod]
        public async Task TreeContainer_ClickingAnotherNode_MovesTheHighlight()
        {
            await Node("leaf1a").ClickAsync();
            await Expect(Node("leaf1a")).ToHaveCSSAsync("background-color", "rgb(95, 184, 120)");

            await Node("leaf1b").ClickAsync();

            await Expect(Node("leaf1b")).ToHaveCSSAsync("background-color", "rgb(95, 184, 120)");
            await Expect(Node("leaf1a")).Not.ToHaveCSSAsync("background-color", "rgb(95, 184, 120)");
            await ExpectScriptAsync("() => window.labTreeClickedIds.join() === 'leaf1a,leaf1b'");
        }

        [TestMethod]
        public async Task TreeContainer_ExpandAndCollapseABranch()
        {
            var branch = Body.Locator("#lab-treecontainer-host .layui-tree-set[data-id=root2]");
            await Expect(branch.Locator(".layui-tree-pack")).ToBeHiddenAsync();

            await branch.Locator("> .layui-tree-entry .layui-tree-iconClick").First.ClickAsync();
            await Expect(branch.Locator(".layui-tree-pack")).ToBeVisibleAsync();
            await Expect(branch.Locator(".layui-tree-txt", new() { HasTextString = "Leaf 2A" })).ToBeVisibleAsync();

            await branch.Locator("> .layui-tree-entry .layui-tree-iconClick").First.ClickAsync();
            await Expect(branch.Locator(".layui-tree-pack")).ToBeHiddenAsync();
        }

        [TestMethod]
        public async Task TreeContainer_WithPages_LoadsTheFirstPageAndThePageOfTheClickedNode()
        {
            var host = Body.Locator("#lab-pagetree-host");
            // auto-load: the page of the first leaf is shown without a click
            await Expect(host.Locator("#lab-pane")).ToHaveTextAsync("pane a");

            await host.Locator(".layui-tree-set[data-id=pb] .layui-tree-main").ClickAsync();

            await Expect(host.Locator("#lab-pane")).ToHaveTextAsync("pane b");
        }

        [TestMethod]
        public async Task TreeContainer_WithPages_ShowsTheFailureTextWhenAPageCannotBeLoaded()
        {
            var host = Body.Locator("#lab-pagetree-host");
            await Expect(host.Locator("#lab-pane")).ToHaveTextAsync("pane a");

            await host.Locator(".layui-tree-set[data-id=pc] .layui-tree-main").ClickAsync();

            await Expect(Page.Locator(".layui-layer-dialog")).ToContainTextAsync(await Page.EvaluateAsync<string>("() => ff.DONOTUSE_Text_LoadFailed"));
            await Expect(host.Locator("#lab-pane")).ToHaveTextAsync("pane a");
        }

        // ---- wt:treecontainer with node ids that hold quotes, brackets, & # and an element-like fragment ------------------------

        private ILocator IdTreeNode(string text) => Body.Locator("#lab-idtree-host .layui-tree-main", new() { HasTextString = text }).First;

        [TestMethod]
        public async Task TreeContainer_NodeIdsWithSpecialCharacters_AreShownAsNodesAndCreateNoElements()
        {
            var host = Body.Locator("#lab-idtree-host");

            await Expect(host.Locator(".layui-tree-set")).ToHaveCountAsync(3);
            await Expect(host.Locator(".layui-tree-set").Last).ToHaveAttributeAsync("data-id", LabData.SpecialId);
            await ExpectNoProbeElementsAsync(host);
        }

        [TestMethod]
        public async Task TreeContainer_PreselectedNodeWithASpecialId_IsHighlightedAndItsIdIsKept()
        {
            // the page is shown with the node selected: the tree finds the node by its id and the id is kept for the grid filter
            await Expect(IdTreeNode("Special node")).ToHaveCSSAsync("background-color", "rgb(95, 184, 120)");
            await Page.WaitForFunctionAsync("id => window.topidtreeselected && window.topidtreeselected.SpecialNodeId === id", LabData.SpecialId);
        }

        [TestMethod]
        public async Task TreeContainer_ClickingNodesWithSpecialIds_MovesTheHighlightAndPassesTheOriginalId()
        {
            await IdTreeNode("Plain node").ClickAsync();
            await Expect(IdTreeNode("Plain node")).ToHaveCSSAsync("background-color", "rgb(95, 184, 120)");
            await Expect(IdTreeNode("Special node")).Not.ToHaveCSSAsync("background-color", "rgb(95, 184, 120)");

            await IdTreeNode("Special node").ClickAsync();

            await Expect(IdTreeNode("Special node")).ToHaveCSSAsync("background-color", "rgb(95, 184, 120)");
            // the function of the page and the selected id see the id of the node, not its encoded form
            await Page.WaitForFunctionAsync("id => window.labIdTreeClickedIds.length === 2 && window.labIdTreeClickedIds[0] === 'plainS' && window.labIdTreeClickedIds[1] === id", LabData.SpecialId);
            await Page.WaitForFunctionAsync("id => window.topidtreeselected.SpecialNodeId === id", LabData.SpecialId);
        }

        [TestMethod]
        public async Task TreeContainer_NodeText_IsShownAsText()
        {
            await Expect(Node("leaf1b").Locator(".layui-tree-txt")).ToHaveTextAsync(LabData.SpecialItem);
            await ExpectNoProbeElementsAsync(Body.Locator("#lab-treecontainer-host"));
        }
    }
}
