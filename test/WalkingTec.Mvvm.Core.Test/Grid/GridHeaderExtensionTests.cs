#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Grid
{
    /// <summary>
    /// Tests for GridHeaderExtension — covers MakeGridHeader*, SetField, SetTitle,
    /// SetWidth, SetEvent, SetSort, SetFixed, SetAlign, SetHide, SetHidden,
    /// SetUnResize, SetEditType.
    /// MakeGridHeaderAction is skipped because it touches CoreProgram._localizer
    /// (static, unset in test context).
    /// </summary>
    [TestClass]
    public class GridHeaderExtensionTests
    {
        // ─── Fixture types ────────────────────────────────────────────────────

        private sealed class HdrPoco : TopBasePoco
        {
            public string? Name { get; set; }
            public int Age { get; set; }
        }

        private sealed class HdrSearcher : BaseSearcher { }

        private sealed class HdrListVM : BasePagedListVM<HdrPoco, HdrSearcher>
        {
            protected override System.Collections.Generic.IEnumerable<IGridColumn<HdrPoco>> InitGridHeader()
            {
                yield return this.MakeGridHeader(x => x.Name);
            }
        }

        // ─── Setup ────────────────────────────────────────────────────────────

        private HdrListVM _vm = null!;

        [TestInitialize]
        public void Setup()
        {
            _vm = new HdrListVM();
            _vm.Wtm = MockWtmContext.CreateWtmContext();
        }

        // ─── MakeGridHeader ───────────────────────────────────────────────────

        [TestMethod]
        public void MakeGridHeader_StringProperty_AlignIsLeft()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            col.Should().NotBeNull();
            col.Align.Should().Be(GridColumnAlignEnum.Left);
            col.ColumnType.Should().Be(GridColumnTypeEnum.Normal);
        }

        [TestMethod]
        public void MakeGridHeader_NonStringProperty_AlignIsCenter()
        {
            // Age is int, not string → Center
            var col = _vm.MakeGridHeader(x => x.Age);
            col.Should().NotBeNull();
            col.Align.Should().Be(GridColumnAlignEnum.Center);
        }

        [TestMethod]
        public void MakeGridHeader_WithWidth_SetsWidth()
        {
            var col = _vm.MakeGridHeader(x => x.Name, width: 200);
            col.Width.Should().Be(200);
        }

        [TestMethod]
        public void MakeGridHeader_NoWidth_WidthIsNull()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            col.Width.Should().BeNull();
        }

        // ─── MakeGridHeaderSpace ──────────────────────────────────────────────

        [TestMethod]
        public void MakeGridHeaderSpace_ReturnsSpaceColumn()
        {
            var col = _vm.MakeGridHeaderSpace();
            col.Should().NotBeNull();
            col.ColumnType.Should().Be(GridColumnTypeEnum.Space);
        }

        // ─── MakeGridHeaderParent ─────────────────────────────────────────────

        [TestMethod]
        public void MakeGridHeaderParent_SetsTitle()
        {
            var col = _vm.MakeGridHeaderParent("Personal Info");
            col.Should().NotBeNull();
            col.Title.Should().Be("Personal Info");
        }

        [TestMethod]
        public void MakeGridHeaderParent_NullTitle()
        {
            var col = _vm.MakeGridHeaderParent(null);
            col.Title.Should().BeNull();
        }

        // ─── SetField ─────────────────────────────────────────────────────────

        [TestMethod]
        public void SetField_Sets_And_Returns_Self()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetField("customField");
            result.Should().BeSameAs(col);
            col.Field.Should().Be("customField");
        }

        // ─── SetTitle ─────────────────────────────────────────────────────────

        [TestMethod]
        public void SetTitle_Sets_And_Returns_Self()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetTitle("姓名");
            result.Should().BeSameAs(col);
            col.Title.Should().Be("姓名");
        }

        // ─── SetWidth (int overload in GridHeaderExtension) ───────────────────

        [TestMethod]
        public void SetWidth_IntOverload_Sets_And_Returns_Self()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetWidth(300);
            result.Should().BeSameAs(col);
            col.Width.Should().Be(300);
        }

        // ─── SetEvent ─────────────────────────────────────────────────────────

        [TestMethod]
        public void SetEvent_Sets_And_Returns_Self()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetEvent("click");
            result.Should().BeSameAs(col);
            col.Event.Should().Be("click");
        }

        // ─── SetSort ──────────────────────────────────────────────────────────

        [TestMethod]
        public void SetSort_DefaultTrue_Sets_Sort()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetSort();
            result.Should().BeSameAs(col);
            col.Sort.Should().BeTrue();
        }

        [TestMethod]
        public void SetSort_False_Clears_Sort()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            col.SetSort(false);
            col.Sort.Should().BeFalse();
        }

        // ─── SetFixed ─────────────────────────────────────────────────────────

        [TestMethod]
        public void SetFixed_Left_Sets_Fixed()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetFixed(GridColumnFixedEnum.Left);
            result.Should().BeSameAs(col);
            col.Fixed.Should().Be(GridColumnFixedEnum.Left);
        }

        [TestMethod]
        public void SetFixed_Null_Clears_Fixed()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            col.SetFixed(null);
            col.Fixed.Should().BeNull();
        }

        // ─── SetAlign ─────────────────────────────────────────────────────────

        [TestMethod]
        public void SetAlign_Right_Sets_Align()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetAlign(GridColumnAlignEnum.Right);
            result.Should().BeSameAs(col);
            col.Align.Should().Be(GridColumnAlignEnum.Right);
        }

        // ─── SetHidden (deprecated) ───────────────────────────────────────────

        [TestMethod]
        public void SetHidden_Deprecated_ReturnsSelf_NoError()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetHidden(true);
            // Deprecated method is a no-op but must return self
            result.Should().BeSameAs(col);
        }

        // ─── SetHide ──────────────────────────────────────────────────────────

        [TestMethod]
        public void SetHide_DefaultTrue_Sets_Hide()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetHide();
            result.Should().BeSameAs(col);
            col.Hide.Should().BeTrue();
        }

        [TestMethod]
        public void SetHide_False_Clears_Hide()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            col.SetHide(false);
            col.Hide.Should().BeFalse();
        }

        // ─── SetUnResize ──────────────────────────────────────────────────────

        [TestMethod]
        public void SetUnResize_DefaultTrue_Sets_UnResize()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetUnResize();
            result.Should().BeSameAs(col);
            col.UnResize.Should().BeTrue();
        }

        // ─── SetEditType ──────────────────────────────────────────────────────

        [TestMethod]
        public void SetEditType_Default_Sets_Text()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var result = col.SetEditType();
            result.Should().BeSameAs(col);
            col.EditType.Should().Be(EditTypeEnum.Text);
        }

        [TestMethod]
        public void SetEditType_WithListItems_Sets_ListItems()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            var items = new System.Collections.Generic.List<ComboSelectListItem>
            {
                new ComboSelectListItem { Value = "1", Text = "One" }
            };
            col.SetEditType(EditTypeEnum.ComboBox, items);
            col.EditType.Should().Be(EditTypeEnum.ComboBox);
            col.ListItems.Should().HaveCount(1);
        }

        [TestMethod]
        public void SetEditType_ReadOnly_Sets_IsReadOnly()
        {
            var col = _vm.MakeGridHeader(x => x.Name);
            col.SetEditType(readOnly: true);
            col.IsReadOnly.Should().BeTrue();
        }
    }
}
