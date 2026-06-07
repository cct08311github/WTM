#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Grid
{
    /// <summary>
    /// Verifies that [ListColumn] seeds grid column defaults via MakeGridHeader
    /// and that explicit fluent calls on the returned column always override the defaults.
    /// </summary>
    [TestClass]
    public class ListColumnAttributeRuntimeTests
    {
        // ─── Model fixtures ──────────────────────────────────────────────────

        private sealed class AttrPoco : TopBasePoco
        {
            [ListColumn(Width = 120, Sort = false)]
            public string? AnnotatedName { get; set; }

            [ListColumn(Hide = true, Fixed = GridColumnFixedEnum.Left, ShowTotal = true)]
            public int AnnotatedScore { get; set; }

            [ListColumn(Align = GridColumnAlignEnum.Right)]
            public string? AnnotatedAligned { get; set; }

            /// <summary>Property with NO [ListColumn] — must behave identically to pre-feature behavior.</summary>
            public string? PlainName { get; set; }

            /// <summary>int property with NO [ListColumn].</summary>
            public int PlainAge { get; set; }
        }

        private sealed class AttrSearcher : BaseSearcher { }

        private sealed class AttrListVM : BasePagedListVM<AttrPoco, AttrSearcher>
        {
            protected override System.Collections.Generic.IEnumerable<IGridColumn<AttrPoco>> InitGridHeader()
            {
                yield return this.MakeGridHeader(x => x.AnnotatedName);
            }
        }

        // ─── Setup ──────────────────────────────────────────────────────────

        private AttrListVM _vm = null!;

        [TestInitialize]
        public void Setup()
        {
            _vm = new AttrListVM();
            _vm.Wtm = MockWtmContext.CreateWtmContext();
        }

        // ─── [ListColumn] seeds column defaults ─────────────────────────────

        [TestMethod]
        public void ListColumn_Width120_SeedsColumnWidth()
        {
            var col = _vm.MakeGridHeader(x => x.AnnotatedName);
            col.Width.Should().Be(120);
        }

        [TestMethod]
        public void ListColumn_SortFalse_DisablesSort()
        {
            var col = _vm.MakeGridHeader(x => x.AnnotatedName);
            col.Sort.Should().BeFalse();
        }

        [TestMethod]
        public void ListColumn_HideTrue_HidesColumn()
        {
            var col = _vm.MakeGridHeader(x => x.AnnotatedScore);
            col.Hide.Should().BeTrue();
        }

        [TestMethod]
        public void ListColumn_FixedLeft_SetsFixed()
        {
            var col = _vm.MakeGridHeader(x => x.AnnotatedScore);
            col.Fixed.Should().Be(GridColumnFixedEnum.Left);
        }

        [TestMethod]
        public void ListColumn_ShowTotalTrue_SetsShowTotal()
        {
            var col = _vm.MakeGridHeader(x => x.AnnotatedScore);
            col.ShowTotal.Should().BeTrue();
        }

        [TestMethod]
        public void ListColumn_AlignRight_OverridesTypeBasedAlign()
        {
            // AnnotatedAligned is string → without attribute would be Left; attribute says Right.
            var col = _vm.MakeGridHeader(x => x.AnnotatedAligned);
            col.Align.Should().Be(GridColumnAlignEnum.Right);
        }

        // ─── Explicit fluent setter overrides the attribute default ──────────

        [TestMethod]
        public void ExplicitSetWidth_AfterAttribute_Wins()
        {
            // [ListColumn(Width=120)] seeds 120; caller overrides to 200.
            var col = _vm.MakeGridHeader(x => x.AnnotatedName).SetWidth(200);
            col.Width.Should().Be(200, "explicit .SetWidth(200) must override the attribute default of 120");
        }

        [TestMethod]
        public void ExplicitSetSort_AfterAttribute_Wins()
        {
            // [ListColumn(Sort=false)] seeds false; caller re-enables sort.
            var col = _vm.MakeGridHeader(x => x.AnnotatedName).SetSort(true);
            col.Sort.Should().BeTrue("explicit .SetSort(true) must override the attribute default of false");
        }

        [TestMethod]
        public void ExplicitWidthArg_ToMakeGridHeader_WinsOverAttribute()
        {
            // Pass width=300 directly to MakeGridHeader — this is the "caller explicit width" path.
            var col = _vm.MakeGridHeader(x => x.AnnotatedName, width: 300);
            col.Width.Should().Be(300, "explicit width parameter to MakeGridHeader must win over the attribute");
        }

        // ─── Absent attribute = current behavior (regression guard) ──────────

        [TestMethod]
        public void NoAttribute_StringProperty_AlignIsLeft()
        {
            var col = _vm.MakeGridHeader(x => x.PlainName);
            col.Align.Should().Be(GridColumnAlignEnum.Left, "string property default align must be Left (regression)");
        }

        [TestMethod]
        public void NoAttribute_IntProperty_AlignIsCenter()
        {
            var col = _vm.MakeGridHeader(x => x.PlainAge);
            col.Align.Should().Be(GridColumnAlignEnum.Center, "non-string property default align must be Center (regression)");
        }

        [TestMethod]
        public void NoAttribute_WidthIsNull()
        {
            var col = _vm.MakeGridHeader(x => x.PlainName);
            col.Width.Should().BeNull("absent attribute must not set width (regression)");
        }

        [TestMethod]
        public void NoAttribute_SortIsNull()
        {
            // Sort is nullable bool; without the attribute it should remain null (unset).
            var col = _vm.MakeGridHeader(x => x.PlainName);
            col.Sort.Should().BeNull("absent attribute must not set Sort (regression)");
        }

        [TestMethod]
        public void NoAttribute_HideIsNull()
        {
            var col = _vm.MakeGridHeader(x => x.PlainName);
            col.Hide.Should().BeNull("absent attribute must not set Hide (regression)");
        }

        [TestMethod]
        public void NoAttribute_FixedIsNull()
        {
            var col = _vm.MakeGridHeader(x => x.PlainName);
            col.Fixed.Should().BeNull("absent attribute must not set Fixed (regression)");
        }

        [TestMethod]
        public void NoAttribute_ShowTotalIsNull()
        {
            var col = _vm.MakeGridHeader(x => x.PlainName);
            col.ShowTotal.Should().BeNull("absent attribute must not set ShowTotal (regression)");
        }
    }
}
