#nullable enable
using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Test.Support.Json
{
    [TestClass]
    public class SimpleMenuTests
    {
        // ─── Property defaults ─────────────────────────────────────────────────

        [TestMethod]
        public void DefaultConstructor_IdIsEmpty()
        {
            var m = new SimpleMenu();
            m.ID.Should().Be(Guid.Empty);
        }

        [TestMethod]
        public void DefaultConstructor_AllNullablesAreNull()
        {
            var m = new SimpleMenu();
            m.ActionId.Should().BeNull();
            m.IsPublic.Should().BeNull();
            m.Url.Should().BeNull();
            m.ParentId.Should().BeNull();
            m.PageName.Should().BeNull();
            m.DisplayOrder.Should().BeNull();
            m.Icon.Should().BeNull();
            m.IsInside.Should().BeNull();
            m.MethodName.Should().BeNull();
            m.TenantAllowed.Should().BeNull();
        }

        [TestMethod]
        public void DefaultConstructor_BoolsAreFalse()
        {
            var m = new SimpleMenu();
            m.ShowOnMenu.Should().BeFalse();
            m.FolderOnly.Should().BeFalse();
        }

        // ─── Property setters ─────────────────────────────────────────────────

        [TestMethod]
        public void Properties_CanBeSetAndRetrieved()
        {
            var id = Guid.NewGuid();
            var parentId = Guid.NewGuid();
            var actionId = Guid.NewGuid();
            var m = new SimpleMenu
            {
                ID = id,
                ActionId = actionId,
                IsPublic = true,
                Url = "/home",
                ParentId = parentId,
                PageName = "Home",
                DisplayOrder = 5,
                Icon = "icon-home",
                ShowOnMenu = true,
                IsInside = false,
                FolderOnly = true,
                MethodName = "Index",
                TenantAllowed = true
            };

            m.ID.Should().Be(id);
            m.ActionId.Should().Be(actionId);
            m.IsPublic.Should().BeTrue();
            m.Url.Should().Be("/home");
            m.ParentId.Should().Be(parentId);
            m.PageName.Should().Be("Home");
            m.DisplayOrder.Should().Be(5);
            m.Icon.Should().Be("icon-home");
            m.ShowOnMenu.Should().BeTrue();
            m.IsInside.Should().BeFalse();
            m.FolderOnly.Should().BeTrue();
            m.MethodName.Should().Be("Index");
            m.TenantAllowed.Should().BeTrue();
        }

        // ─── IsParentShowOnMenu ───────────────────────────────────────────────

        [TestMethod]
        public void IsParentShowOnMenu_NoParent_ReturnsOwnShowOnMenu()
        {
            var menu = new SimpleMenu { ID = Guid.NewGuid(), ShowOnMenu = true, ParentId = null };
            var all = new List<SimpleMenu> { menu };
            menu.IsParentShowOnMenu(all).Should().BeTrue();
        }

        [TestMethod]
        public void IsParentShowOnMenu_NoParent_ShowOnMenuFalse_ReturnsFalse()
        {
            var menu = new SimpleMenu { ID = Guid.NewGuid(), ShowOnMenu = false, ParentId = null };
            var all = new List<SimpleMenu> { menu };
            menu.IsParentShowOnMenu(all).Should().BeFalse();
        }

        [TestMethod]
        public void IsParentShowOnMenu_ParentExists_DelegatesToParent()
        {
            var parentId = Guid.NewGuid();
            var parent = new SimpleMenu { ID = parentId, ShowOnMenu = true, ParentId = null };
            var child = new SimpleMenu { ID = Guid.NewGuid(), ShowOnMenu = false, ParentId = parentId };
            var all = new List<SimpleMenu> { parent, child };
            child.IsParentShowOnMenu(all).Should().BeTrue();
        }

        [TestMethod]
        public void IsParentShowOnMenu_ParentNotFound_ReturnsFalse()
        {
            var child = new SimpleMenu
            {
                ID = Guid.NewGuid(),
                ShowOnMenu = true,
                ParentId = Guid.NewGuid() // random ID not in list
            };
            var all = new List<SimpleMenu> { child };
            child.IsParentShowOnMenu(all).Should().BeFalse();
        }

        [TestMethod]
        public void IsParentShowOnMenu_MultiLevel_RecursesThroughHierarchy()
        {
            var rootId = Guid.NewGuid();
            var midId = Guid.NewGuid();
            var leafId = Guid.NewGuid();

            var root = new SimpleMenu { ID = rootId, ShowOnMenu = true, ParentId = null };
            var mid = new SimpleMenu { ID = midId, ShowOnMenu = false, ParentId = rootId };
            var leaf = new SimpleMenu { ID = leafId, ShowOnMenu = false, ParentId = midId };
            var all = new List<SimpleMenu> { root, mid, leaf };

            // leaf → mid → root (root.ShowOnMenu = true)
            leaf.IsParentShowOnMenu(all).Should().BeTrue();
        }

        // ─── GetLevel ─────────────────────────────────────────────────────────

        [TestMethod]
        public void GetLevel_NoParent_ReturnsZero()
        {
            var menu = new SimpleMenu { ID = Guid.NewGuid(), ParentId = null };
            var all = new List<SimpleMenu> { menu };
            menu.GetLevel(all).Should().Be(0);
        }

        [TestMethod]
        public void GetLevel_OneParent_ReturnsOne()
        {
            var parentId = Guid.NewGuid();
            var parent = new SimpleMenu { ID = parentId, ParentId = null };
            var child = new SimpleMenu { ID = Guid.NewGuid(), ParentId = parentId };
            var all = new List<SimpleMenu> { parent, child };
            child.GetLevel(all).Should().Be(1);
        }

        [TestMethod]
        public void GetLevel_TwoLevelsDeep_ReturnsTwo()
        {
            var rootId = Guid.NewGuid();
            var midId = Guid.NewGuid();
            var root = new SimpleMenu { ID = rootId, ParentId = null };
            var mid = new SimpleMenu { ID = midId, ParentId = rootId };
            var leaf = new SimpleMenu { ID = Guid.NewGuid(), ParentId = midId };
            var all = new List<SimpleMenu> { root, mid, leaf };
            leaf.GetLevel(all).Should().Be(2);
        }

        [TestMethod]
        public void GetLevel_ParentNotInList_StopsCountingAtMissingParent()
        {
            // Parent not in list — loop will hit null and stop
            var child = new SimpleMenu { ID = Guid.NewGuid(), ParentId = Guid.NewGuid() };
            var all = new List<SimpleMenu> { child };
            // Will count 1 (for child's ParentId) then stop because parent not found
            child.GetLevel(all).Should().Be(1);
        }

        // ─── SimpleMenuApi ────────────────────────────────────────────────────

        [TestMethod]
        public void SimpleMenuApi_Properties_CanBeSetAndRetrieved()
        {
            var api = new SimpleMenuApi
            {
                Id = "1",
                ParentId = "0",
                Text = "Dashboard",
                Url = "/dashboard",
                Icon = "icon-dashboard",
                ShowOnMenu = true
            };
            api.Id.Should().Be("1");
            api.ParentId.Should().Be("0");
            api.Text.Should().Be("Dashboard");
            api.Url.Should().Be("/dashboard");
            api.Icon.Should().Be("icon-dashboard");
            api.ShowOnMenu.Should().BeTrue();
        }

        // ─── LayUIMenu ────────────────────────────────────────────────────────

        [TestMethod]
        public void LayUIMenu_Name_ReturnsTitleValue()
        {
            var m = new LayUIMenu { Title = "MyMenu" };
            m.Name.Should().Be("MyMenu");
        }

        [TestMethod]
        public void LayUIMenu_Properties_CanBeSetAndRetrieved()
        {
            var id = Guid.NewGuid();
            var children = new List<LayUIMenu> { new LayUIMenu { Title = "Child" } };
            var m = new LayUIMenu
            {
                Id = id,
                Title = "Root",
                Icon = "icon-root",
                Expand = true,
                Url = "/root",
                Children = children,
                Order = 1
            };

            m.Id.Should().Be(id);
            m.Title.Should().Be("Root");
            m.Icon.Should().Be("icon-root");
            m.Expand.Should().BeTrue();
            m.Url.Should().Be("/root");
            m.Children.Should().HaveCount(1);
            m.Order.Should().Be(1);
        }

        [TestMethod]
        public void LayUIMenu_DefaultConstructor_NullablePropertiesAreNull()
        {
            var m = new LayUIMenu();
            m.Title.Should().BeNull();
            m.Icon.Should().BeNull();
            m.Expand.Should().BeNull();
            m.Url.Should().BeNull();
            m.Children.Should().BeNull();
            m.Order.Should().BeNull();
        }
    }
}
