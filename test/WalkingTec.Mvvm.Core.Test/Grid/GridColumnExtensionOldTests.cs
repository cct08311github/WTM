#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Grid
{
    /// <summary>
    /// Tests for GridColumnExtension (`Old.cs) — covers all extension methods on GridColumn.
    /// </summary>
    [TestClass]
    public class GridColumnExtensionOldTests
    {
        // Minimal TopBasePoco subclass for generic constraints
        private sealed class TestPoco : TopBasePoco
        {
            public string? Name { get; set; }
            public int Count { get; set; }
        }

        private static GridColumn<TestPoco> Make() => new GridColumn<TestPoco>();

        // ─── GetAllBottomColumns ───────────────────────────────────────────────
        // BottomChildren returns [self] when no children; with children it
        // recurses into each child's BottomChildren.

        [TestMethod]
        public void GetAllBottomColumns_NoChildren_ReturnsSelfAsLeaf()
        {
            var col = Make();
            var list = new List<IGridColumn<TestPoco>> { col };
            var result = list.GetAllBottomColumns().ToList();
            // BottomChildren of a leaf column is [col] itself
            result.Should().HaveCount(1);
            result[0].Should().BeSameAs(col);
        }

        [TestMethod]
        public void GetAllBottomColumns_WithLeafChildren_ReturnsLeafChildren()
        {
            var parent = Make();
            var child1 = Make();
            var child2 = Make();
            parent.SetChildren(child1, child2);
            // child1 and child2 have no children → BottomChildren = [child1],[child2]
            // parent's BottomChildren accumulates both → 2 leaf items

            var list = new List<IGridColumn<TestPoco>> { parent };
            var result = list.GetAllBottomColumns().ToList();
            result.Should().HaveCount(2);
            result.Should().Contain(child1);
            result.Should().Contain(child2);
        }

        // ─── SetId ────────────────────────────────────────────────────────────

        [TestMethod]
        public void SetId_Sets_And_Returns_Self()
        {
            var col = Make();
            var result = col.SetId("col1");
            result.Should().BeSameAs(col);
            col.Id.Should().Be("col1");
        }

        [TestMethod]
        public void SetId_Null_ClearsId()
        {
            var col = Make();
            col.SetId("x").SetId(null);
            col.Id.Should().BeNull();
        }

        // ─── SetHeader ────────────────────────────────────────────────────────

        [TestMethod]
        public void SetHeader_Sets_Title_And_Returns_Self()
        {
            var col = Make();
            var result = col.SetHeader("Student Name");
            result.Should().BeSameAs(col);
            col.Title.Should().Be("Student Name");
        }

        // ─── SetNeedGroup ─────────────────────────────────────────────────────

        [TestMethod]
        public void SetNeedGroup_True_Sets_Flag()
        {
            var col = Make();
            var result = col.SetNeedGroup(true);
            result.Should().BeSameAs(col);
            col.NeedGroup.Should().BeTrue();
        }

        [TestMethod]
        public void SetNeedGroup_False_Clears_Flag()
        {
            var col = Make();
            col.SetNeedGroup(false);
            col.NeedGroup.Should().BeFalse();
        }

        // ─── SetLocked ────────────────────────────────────────────────────────

        [TestMethod]
        public void SetLocked_True_Sets_IsLocked()
        {
            var col = Make();
            var result = col.SetLocked(true);
            result.Should().BeSameAs(col);
            col.IsLocked.Should().BeTrue();
        }

        // ─── SetSortable ──────────────────────────────────────────────────────

        [TestMethod]
        public void SetSortable_Default_False()
        {
            var col = Make();
            var result = col.SetSortable();
            result.Should().BeSameAs(col);
            col.Sortable.Should().BeFalse();
        }

        [TestMethod]
        public void SetSortable_True()
        {
            var col = Make();
            col.SetSortable(true);
            col.Sortable.Should().BeTrue();
        }

        // ─── SetWidth ─────────────────────────────────────────────────────────

        [TestMethod]
        public void SetWidth_Sets_Width_And_Returns_Self()
        {
            var col = Make();
            var result = col.SetWidth(200);
            result.Should().BeSameAs(col);
            col.Width.Should().Be(200);
        }

        [TestMethod]
        public void SetWidth_Null_Clears_Width()
        {
            var col = Make();
            col.SetWidth((int?)null);
            col.Width.Should().BeNull();
        }

        // ─── SetAllowMultiLine ────────────────────────────────────────────────

        [TestMethod]
        public void SetAllowMultiLine_True_Sets_Flag()
        {
            var col = Make();
            var result = col.SetAllowMultiLine(true);
            result.Should().BeSameAs(col);
            col.AllowMultiLine.Should().BeTrue();
        }

        // ─── SetFlex ──────────────────────────────────────────────────────────

        [TestMethod]
        public void SetFlex_Sets_Value_And_Returns_Self()
        {
            var col = Make();
            var result = col.SetFlex(2);
            result.Should().BeSameAs(col);
            col.Flex.Should().Be(2);
        }

        // ─── SetFormat ────────────────────────────────────────────────────────

        [TestMethod]
        public void SetFormat_WithCallback_Sets_Format()
        {
            var col = Make();
            ColumnFormatCallBack<TestPoco>? callback = (item, dc) => "<b>test</b>";
            var result = col.SetFormat(callback);
            result.Should().BeSameAs(col);
            col.Format.Should().BeSameAs(callback);
        }

        [TestMethod]
        public void SetFormat_Null_Clears()
        {
            var col = Make();
            col.SetFormat((ColumnFormatCallBack<TestPoco>?)null);
            col.Format.Should().BeNull();
        }

        // ─── SetColumnExp ─────────────────────────────────────────────────────

        [TestMethod]
        public void SetColumnExp_Sets_Expression()
        {
            var col = Make();
            System.Linq.Expressions.Expression<Func<TestPoco, object>> exp = x => x.Name!;
            var result = col.SetColumnExp(exp);
            result.Should().BeSameAs(col);
            col.ColumnExp.Should().BeSameAs(exp);
        }

        // ─── SetChildren ──────────────────────────────────────────────────────

        [TestMethod]
        public void SetChildren_SetsChildrenList()
        {
            var parent = Make();
            var child1 = Make();
            var child2 = Make();
            var result = parent.SetChildren(child1, child2);
            result.Should().BeSameAs(parent);
            parent.Children.Should().HaveCount(2);
        }

        [TestMethod]
        public void SetChildren_CalledTwice_Accumulates()
        {
            var parent = Make();
            parent.SetChildren(Make());
            parent.SetChildren(Make());
            parent.Children.Should().HaveCount(2);
        }

        // ─── SetForeGroundFunc / SetBackGroundFunc ────────────────────────────

        [TestMethod]
        public void SetForeGroundFunc_Sets_And_Returns_Self()
        {
            var col = Make();
            Func<TestPoco, string> fn = _ => "red";
            var result = col.SetForeGroundFunc(fn);
            result.Should().BeSameAs(col);
            col.ForeGroundFunc.Should().BeSameAs(fn);
        }

        [TestMethod]
        public void SetBackGroundFunc_Sets_And_Returns_Self()
        {
            var col = Make();
            Func<TestPoco, string> fn = _ => "#fff";
            var result = col.SetBackGroundFunc(fn);
            result.Should().BeSameAs(col);
            col.BackGroundFunc.Should().BeSameAs(fn);
        }

        // ─── SetShowTotal ─────────────────────────────────────────────────────

        [TestMethod]
        public void SetShowTotal_DefaultTrue_Sets_ShowTotal()
        {
            var col = Make();
            var result = col.SetShowTotal();
            result.Should().BeSameAs(col);
            col.ShowTotal.Should().BeTrue();
        }

        [TestMethod]
        public void SetShowTotal_False_Clears()
        {
            var col = Make();
            col.SetShowTotal(false);
            col.ShowTotal.Should().BeFalse();
        }

        // ─── SetDisableExport ─────────────────────────────────────────────────

        [TestMethod]
        public void SetDisableExport_Sets_DisableExport_True()
        {
            var col = Make();
            var result = col.SetDisableExport();
            result.Should().BeSameAs(col);
            col.DisableExport.Should().BeTrue();
        }

        // ─── Fluent chain ─────────────────────────────────────────────────────

        [TestMethod]
        public void FluentChain_MultipleCalls_WorkCorrectly()
        {
            var col = Make()
                .SetId("col1")
                .SetHeader("Name")
                .SetWidth(150)
                .SetSortable(true)
                .SetShowTotal()
                .SetNeedGroup(true);

            col.Id.Should().Be("col1");
            col.Title.Should().Be("Name");
            col.Width.Should().Be(150);
            col.Sortable.Should().BeTrue();
            col.ShowTotal.Should().BeTrue();
            col.NeedGroup.Should().BeTrue();
        }
    }
}
