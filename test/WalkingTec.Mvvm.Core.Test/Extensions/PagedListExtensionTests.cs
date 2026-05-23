#nullable enable
using System;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    /// <summary>
    /// Unit tests for <see cref="PagedListExtension.MakeGridColumn{T,V}"/> — Issue #36.
    /// <para>
    /// MakeGridColumn never dereferences its <c>self</c> parameter — it is
    /// only used as the extension method receiver to supply the generic type
    /// parameters T and V.  We therefore pass <c>null!</c> as the receiver,
    /// which avoids building a full <see cref="IBasePagedListVM{T,S}"/> stub
    /// and keeps the test free of real framework infrastructure.
    /// </para>
    /// </summary>
    [TestClass]
    public class PagedListExtensionTests
    {
        // ─── Minimal stub types ───────────────────────────────────────────────

        /// <summary>Minimal TopBasePoco subclass used as the list-VM row type.</summary>
        private class TestPoco : TopBasePoco
        {
            public string Name { get; set; } = "";
            public int Score { get; set; }
        }

        /// <summary>Minimal ISearcher marker — MakeGridColumn only needs the type.</summary>
        private class TestSearcher : BaseSearcher { }

        // ─── Helper ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a null IBasePagedListVM cast to the correct generic type.
        /// MakeGridColumn only uses 'self' to infer T and V; it never calls
        /// any member on it, so null is safe here.
        /// </summary>
        private static IBasePagedListVM<TestPoco, TestSearcher> NullVm()
            => null!;

        // ─── MakeGridColumn ───────────────────────────────────────────────────

        [TestMethod]
        public void MakeGridColumn_MinimalArgs_ReturnsGridColumnWithExpression()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;

            var col = NullVm().MakeGridColumn(exp);

            col.Should().NotBeNull();
            col.ColumnExp.Should().BeSameAs(exp);
        }

        [TestMethod]
        public void MakeGridColumn_WithHeader_SetsTitleOnColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Score;

            var col = NullVm().MakeGridColumn(exp, Header: "得点");

            col.Title.Should().Be("得点");
        }

        [TestMethod]
        public void MakeGridColumn_WithWidth_SetsWidthOnColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;

            var col = NullVm().MakeGridColumn(exp, Width: 120);

            col.Width.Should().Be(120);
        }

        [TestMethod]
        public void MakeGridColumn_WithFlex_SetsFlexOnColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;

            var col = NullVm().MakeGridColumn(exp, Flex: 2);

            col.Flex.Should().Be(2);
        }

        [TestMethod]
        public void MakeGridColumn_AllowMultiLine_DefaultTrue()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;

            var col = NullVm().MakeGridColumn(exp);

            col.AllowMultiLine.Should().BeTrue();
        }

        [TestMethod]
        public void MakeGridColumn_AllowMultiLineFalse_PropagatedToColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;

            var col = NullVm().MakeGridColumn(exp, AllowMultiLine: false);

            col.AllowMultiLine.Should().BeFalse();
        }

        [TestMethod]
        public void MakeGridColumn_NeedGroupTrue_PropagatedToColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;

            var col = NullVm().MakeGridColumn(exp, NeedGroup: true);

            col.NeedGroup.Should().BeTrue();
        }

        [TestMethod]
        public void MakeGridColumn_WithFormatCallback_PropagatedToColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;
            ColumnFormatCallBack<TestPoco> fmt = (row, val) => val;

            var col = NullVm().MakeGridColumn(exp, Format: fmt);

            col.Format.Should().BeSameAs(fmt);
        }

        [TestMethod]
        public void MakeGridColumn_WithForeGroundFunc_PropagatedToColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Name;
            Func<TestPoco, string> fg = _ => "red";

            var col = NullVm().MakeGridColumn(exp, ForeGroundFunc: fg);

            col.ForeGroundFunc.Should().BeSameAs(fg);
        }

        [TestMethod]
        public void MakeGridColumn_WithBackGroundFunc_PropagatedToColumn()
        {
            Expression<Func<TestPoco, object>> exp = p => p.Score;
            Func<TestPoco, string> bg = _ => "blue";

            var col = NullVm().MakeGridColumn(exp, BackGroundFunc: bg);

            col.BackGroundFunc.Should().BeSameAs(bg);
        }
    }
}
