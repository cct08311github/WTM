#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    /// <summary>
    /// Tests for ITreeDataExtension — covers GetAllChildren, GetLevel,
    /// FlatTree (list and single), MakeTree, FlatTreeSelectList,
    /// GetTreeSelectChildren.
    /// GetAllChildrenIDs requires a real DataContext and is excluded from
    /// this unit test file (would be an integration test).
    /// </summary>
    [TestClass]
    public class ITreeDataExtensionTests
    {
        // ─── Fixture: simple concrete TreePoco<T> ─────────────────────────────

        private sealed class Category : TreePoco<Category>
        {
            public string? Name { get; set; }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static Category Node(string name, Guid? parentId = null)
        {
            return new Category
            {
                ID = Guid.NewGuid(),
                Name = name,
                ParentId = parentId,
                Children = []
            };
        }

        // ─── GetAllChildren ───────────────────────────────────────────────────

        [TestMethod]
        public void GetAllChildren_NoChildren_ReturnsEmpty()
        {
            var root = Node("Root");
            root.GetAllChildren().Should().BeEmpty();
        }

        [TestMethod]
        public void GetAllChildren_SingleLevel_ReturnsDirectChildren()
        {
            var root = Node("Root");
            var child1 = Node("A", root.ID);
            var child2 = Node("B", root.ID);
            root.Children = [child1, child2];

            var result = root.GetAllChildren();
            result.Should().HaveCount(2);
            result.Should().Contain(child1);
            result.Should().Contain(child2);
        }

        [TestMethod]
        public void GetAllChildren_TwoLevels_ReturnsAllDescendants()
        {
            var root = Node("Root");
            var child = Node("Child", root.ID);
            var grandchild = Node("GrandChild", child.ID);
            child.Children = [grandchild];
            root.Children = [child];

            var result = root.GetAllChildren();
            result.Should().HaveCount(2);
            result.Should().ContainInOrder(child, grandchild);
        }

        [TestMethod]
        public void GetAllChildren_WithOrder_SortsChildren()
        {
            var root = Node("Root");
            var b = Node("B", root.ID);
            var a = Node("A", root.ID);
            root.Children = [b, a]; // deliberately unordered

            var result = root.GetAllChildren(order: x => x.Name!);
            result.First().Name.Should().Be("A");
            result.Last().Name.Should().Be("B");
        }

        // ─── GetLevel ─────────────────────────────────────────────────────────

        [TestMethod]
        public void GetLevel_RootNode_ReturnsZero()
        {
            var root = Node("Root");
            root.GetLevel().Should().Be(0);
        }

        [TestMethod]
        public void GetLevel_ChildNode_ReturnsOne()
        {
            var root = Node("Root");
            var child = Node("Child", root.ID);
            child.Parent = root;

            child.GetLevel().Should().Be(1);
        }

        [TestMethod]
        public void GetLevel_GrandchildNode_ReturnsTwo()
        {
            var root = Node("Root");
            var child = Node("Child", root.ID);
            child.Parent = root;
            var grand = Node("Grand", child.ID);
            grand.Parent = child;

            grand.GetLevel().Should().Be(2);
        }

        // ─── FlatTree (single instance) ───────────────────────────────────────

        [TestMethod]
        public void FlatTree_Single_NoChildren_ReturnsSelf()
        {
            var root = Node("Root");
            var result = root.FlatTree();
            result.Should().HaveCount(1);
            result[0].Should().BeSameAs(root);
        }

        [TestMethod]
        public void FlatTree_Single_WithChildren_ReturnsAllIncludingSelf()
        {
            var root = Node("Root");
            var child = Node("Child", root.ID);
            var grand = Node("Grand", child.ID);
            child.Children = [grand];
            root.Children = [child];

            var result = root.FlatTree();
            result.Should().HaveCount(3);
            result[0].Should().BeSameAs(root);
        }

        // ─── FlatTree (list) ──────────────────────────────────────────────────

        [TestMethod]
        public void FlatTree_EmptyList_ReturnsEmpty()
        {
            var list = new List<Category>();
            list.FlatTree().Should().BeEmpty();
        }

        [TestMethod]
        public void FlatTree_List_WithNestedChildren_FlattensAll()
        {
            var root1 = Node("R1");
            var root2 = Node("R2");
            var child = Node("C", root1.ID);
            root1.Children = [child];

            var list = new List<Category> { root1, root2 };
            var result = list.FlatTree();
            result.Should().HaveCount(3);
        }

        [TestMethod]
        public void FlatTree_List_WithOrder_SortsTopLevel()
        {
            var b = Node("B");
            var a = Node("A");
            var list = new List<Category> { b, a };

            var result = list.FlatTree(order: x => x.Name!);
            result[0].Name.Should().Be("A");
            result[1].Name.Should().Be("B");
        }

        // ─── MakeTree ─────────────────────────────────────────────────────────

        [TestMethod]
        public void MakeTree_EmptyList_ReturnsEmpty()
        {
            var list = new List<Category>();
            list.MakeTree().Should().BeEmpty();
        }

        [TestMethod]
        public void MakeTree_FlatList_BuildsHierarchy()
        {
            var root = Node("Root");
            var child = Node("Child", root.ID);
            var sibling = Node("Sibling", root.ID);

            var list = new List<Category> { root, child, sibling };
            var tree = list.MakeTree();

            // Only roots (ParentId == null) at top level
            tree.Should().HaveCount(1);
            tree[0].Should().BeSameAs(root);
            tree[0].Children.Should().HaveCount(2);
        }

        [TestMethod]
        public void MakeTree_WithOrder_SortsBeforeBuilding()
        {
            var root = Node("Z");
            var rootA = Node("A");
            var list = new List<Category> { root, rootA };

            var tree = list.MakeTree(order: x => x.Name!);
            tree[0].Name.Should().Be("A");
        }

        [TestMethod]
        public void MakeTree_StandaloneNode_ItsOwnRoot()
        {
            var node = Node("Standalone");
            var tree = new List<Category> { node }.MakeTree();
            tree.Should().HaveCount(1);
            tree[0].Should().BeSameAs(node);
        }

        // ─── FlatTreeSelectList ───────────────────────────────────────────────

        [TestMethod]
        public void FlatTreeSelectList_EmptyList_ReturnsEmpty()
        {
            var list = new List<TreeSelectListItem>();
            var result = list.FlatTreeSelectList();
            result.Should().BeEmpty();
        }

        [TestMethod]
        public void FlatTreeSelectList_WithChildren_FlattensAll()
        {
            var parent = new TreeSelectListItem
            {
                Value = "1",
                Text = "Parent",
                Children =
                [
                    new TreeSelectListItem { Value = "2", Text = "Child" }
                ]
            };

            var list = new List<TreeSelectListItem> { parent };
            var result = list.FlatTreeSelectList().ToList();

            result.Should().HaveCount(2);
            result[0].Value.Should().Be("1");
            result[1].Value.Should().Be("2");
        }

        [TestMethod]
        public void FlatTreeSelectList_WithOrder_SortsTopLevel()
        {
            var b = new TreeSelectListItem { Value = "2", Text = "B" };
            var a = new TreeSelectListItem { Value = "1", Text = "A" };

            var list = new List<TreeSelectListItem> { b, a };
            var result = list.FlatTreeSelectList(order: x => x.Text!).ToList();
            result[0].Text.Should().Be("A");
            result[1].Text.Should().Be("B");
        }

        // ─── GetTreeSelectChildren ────────────────────────────────────────────

        [TestMethod]
        public void GetTreeSelectChildren_NullChildren_ReturnsEmpty()
        {
            var item = new TreeSelectListItem { Value = "1", Children = null };
            item.GetTreeSelectChildren().Should().BeEmpty();
        }

        [TestMethod]
        public void GetTreeSelectChildren_EmptyChildren_ReturnsEmpty()
        {
            var item = new TreeSelectListItem { Value = "1", Children = [] };
            item.GetTreeSelectChildren().Should().BeEmpty();
        }

        [TestMethod]
        public void GetTreeSelectChildren_SingleChild_ReturnsIt()
        {
            var child = new TreeSelectListItem { Value = "2", Text = "Child" };
            var parent = new TreeSelectListItem
            {
                Value = "1",
                Children = [child]
            };

            var result = parent.GetTreeSelectChildren();
            result.Should().HaveCount(1);
            result[0].Should().BeSameAs(child);
        }

        [TestMethod]
        public void GetTreeSelectChildren_DeepNesting_ReturnsAllDescendants()
        {
            var grand = new TreeSelectListItem { Value = "3", Text = "Grand" };
            var child = new TreeSelectListItem
            {
                Value = "2",
                Text = "Child",
                Children = [grand]
            };
            var parent = new TreeSelectListItem
            {
                Value = "1",
                Children = [child]
            };

            var result = parent.GetTreeSelectChildren();
            result.Should().HaveCount(2);
        }

        [TestMethod]
        public void GetTreeSelectChildren_WithOrder_SortsChildren()
        {
            var b = new TreeSelectListItem { Value = "b", Text = "B" };
            var a = new TreeSelectListItem { Value = "a", Text = "A" };
            var parent = new TreeSelectListItem
            {
                Value = "p",
                Children = [b, a]
            };

            var result = parent.GetTreeSelectChildren(order: x => x.Text!);
            result[0].Text.Should().Be("A");
            result[1].Text.Should().Be("B");
        }

        [TestMethod]
        public void GetTreeSelectChildren_ExcludesSelf_DuplicateValueFiltered()
        {
            // The extension filters out children whose Value == parent.Value.
            var self = new TreeSelectListItem { Value = "1" };
            var selfRef = new TreeSelectListItem { Value = "1", Text = "SelfRef" }; // same Value as parent
            var normal = new TreeSelectListItem { Value = "2", Text = "Normal" };
            self.Children = [selfRef, normal];

            var result = self.GetTreeSelectChildren();
            // selfRef should be filtered out
            result.Should().HaveCount(1);
            result[0].Value.Should().Be("2");
        }
    }
}
