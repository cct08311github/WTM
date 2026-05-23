#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Grid
{
    // ─── Model used by GridColumn tests ─────────────────────────────────────────

    public class GcTestItem : TopBasePoco
    {
        [Display(Name = "Item Name")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Score")]
        public int Score { get; set; }

        [Display(Name = "Date")]
        public DateTime? EventDate { get; set; }

        [Display(Name = "Is Active")]
        public bool IsActive { get; set; }

        public GenderEnum? Gender { get; set; }
    }

    // ─── Tests ──────────────────────────────────────────────────────────────────

    [TestClass]
    public class GridColumnTests
    {
        // ─── Constructor: default ────────────────────────────────────────────────

        [TestMethod]
        public void DefaultConstructor_SetsAllowMultiLineTrue_And_SortableTrue()
        {
            var col = new GridColumn<GcTestItem>();

            col.AllowMultiLine.Should().BeTrue();
            col.Sortable.Should().BeTrue();
        }

        // ─── Constructor: expression + width ────────────────────────────────────

        [TestMethod]
        public void ExpressionWidthConstructor_SetsColumnExpAndWidth()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, 200);

            col.ColumnExp.Should().NotBeNull();
            col.Width.Should().Be(200);
        }

        // ─── Constructor: full ───────────────────────────────────────────────────

        [TestMethod]
        public void FullConstructor_SetsAllProvidedValues()
        {
            var format = (ColumnFormatCallBack<GcTestItem>)((item, val)
                => new ColumnFormatInfo { Html = "formatted" });
            Func<GcTestItem, string> fg = _ => "red";
            Func<GcTestItem, string> bg = _ => "blue";

            var col = new GridColumn<GcTestItem>(
                ColumnExp: x => x.Name,
                Format: format,
                Header: "My Header",
                Width: 150,
                Flex: 2,
                AllowMultiLine: false,
                NeedGroup: true,
                ForeGroundFunc: fg,
                BackGroundFunc: bg,
                sortable: false);

            col.Title.Should().Be("My Header");
            col.Width.Should().Be(150);
            col.Flex.Should().Be(2);
            col.AllowMultiLine.Should().BeFalse();
            col.NeedGroup.Should().BeTrue();
            col.Sortable.Should().BeFalse();
            col.ForeGroundFunc.Should().NotBeNull();
            col.BackGroundFunc.Should().NotBeNull();
            col.Format.Should().NotBeNull();
        }

        // ─── Field property ─────────────────────────────────────────────────────

        [TestMethod]
        public void Field_FromExpression_ReturnsPropertyName()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            col.Field.Should().Be("Name");
        }

        [TestMethod]
        public void Field_CanBeSetManually()
        {
            var col = new GridColumn<GcTestItem>();
            col.Field = "CustomField";
            col.Field.Should().Be("CustomField");
        }

        // ─── Title property ──────────────────────────────────────────────────────

        [TestMethod]
        public void Title_FromExpression_ReturnsDisplayName()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            // Display(Name="Item Name") should be resolved
            col.Title.Should().Be("Item Name");
        }

        [TestMethod]
        public void Title_CanBeSetManually()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            col.Title = "Override Title";
            col.Title.Should().Be("Override Title");
        }

        // ─── FieldType ───────────────────────────────────────────────────────────

        [TestMethod]
        public void FieldType_FromExpression_ReturnsCorrectType()
        {
            var col = new GridColumn<GcTestItem>(x => x.Score, null);
            col.FieldType.Should().Be(typeof(int));
        }

        [TestMethod]
        public void FieldType_CanBeSetManually()
        {
            var col = new GridColumn<GcTestItem>();
            col.FieldType = typeof(string);
            col.FieldType.Should().Be(typeof(string));
        }

        // ─── FieldName ───────────────────────────────────────────────────────────

        [TestMethod]
        public void FieldName_ReturnsPropertyInfoName()
        {
            var col = new GridColumn<GcTestItem>(x => x.Score, null);
            col.FieldName.Should().Be("Score");
        }

        [TestMethod]
        public void FieldName_WithNoExpression_ReturnsNull()
        {
            var col = new GridColumn<GcTestItem>();
            col.FieldName.Should().BeNull();
        }

        // ─── MaxLevel ────────────────────────────────────────────────────────────

        [TestMethod]
        public void MaxLevel_SingleColumn_Returns1()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            col.MaxLevel.Should().Be(1);
        }

        [TestMethod]
        public void MaxLevel_WithChildren_ReturnsDepthPlusOne()
        {
            var child = new GridColumn<GcTestItem>(x => x.Score, null);
            var parent = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                Children = [child]
            };
            parent.MaxLevel.Should().Be(2);
        }

        // ─── MaxChildrenCount ────────────────────────────────────────────────────

        [TestMethod]
        public void MaxChildrenCount_SingleColumn_Returns1()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            col.MaxChildrenCount.Should().Be(1);
        }

        [TestMethod]
        public void MaxChildrenCount_WithTwoChildren_Returns2()
        {
            var c1 = new GridColumn<GcTestItem>(x => x.Score, null);
            var c2 = new GridColumn<GcTestItem>(x => x.IsActive, null);
            var parent = new GridColumn<GcTestItem>
            {
                Children = [c1, c2]
            };
            parent.MaxChildrenCount.Should().Be(2);
        }

        // ─── ChildrenLength ──────────────────────────────────────────────────────

        [TestMethod]
        public void ChildrenLength_NoChildren_ReturnsZero()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            col.ChildrenLength.Should().Be(0);
        }

        [TestMethod]
        public void ChildrenLength_WithLeafChildren_CountsLeaves()
        {
            var c1 = new GridColumn<GcTestItem>(x => x.Score, null);
            var c2 = new GridColumn<GcTestItem>(x => x.IsActive, null);
            var parent = new GridColumn<GcTestItem>
            {
                Children = [c1, c2]
            };
            parent.ChildrenLength.Should().Be(2);
        }

        // ─── MaxDepth ────────────────────────────────────────────────────────────

        [TestMethod]
        public void MaxDepth_SingleColumn_Returns1()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            col.MaxDepth.Should().Be(1);
        }

        [TestMethod]
        public void MaxDepth_WithChild_Returns2()
        {
            var child = new GridColumn<GcTestItem>(x => x.Score, null);
            var parent = new GridColumn<GcTestItem>
            {
                Children = [child]
            };
            parent.MaxDepth.Should().Be(2);
        }

        // ─── BottomChildren ──────────────────────────────────────────────────────

        [TestMethod]
        public void BottomChildren_LeafColumn_ReturnsSelf()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            var bottom = col.BottomChildren.ToList();
            bottom.Should().ContainSingle().Which.Should().BeSameAs(col);
        }

        [TestMethod]
        public void BottomChildren_ParentWithChildren_ReturnsLeaves()
        {
            var c1 = new GridColumn<GcTestItem>(x => x.Score, null);
            var c2 = new GridColumn<GcTestItem>(x => x.IsActive, null);
            var parent = new GridColumn<GcTestItem>
            {
                Children = [c1, c2]
            };
            var bottom = parent.BottomChildren.ToList();
            bottom.Should().HaveCount(2);
            bottom.Should().Contain(c1);
            bottom.Should().Contain(c2);
        }

        // ─── HasFormat ───────────────────────────────────────────────────────────

        [TestMethod]
        public void HasFormat_NoFormatSet_ReturnsFalse()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            col.HasFormat().Should().BeFalse();
        }

        [TestMethod]
        public void HasFormat_FormatSet_ReturnsTrue()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name,
                (item, val) => new ColumnFormatInfo { Html = "x" });
            col.HasFormat().Should().BeTrue();
        }

        // ─── GetForeGroundColor ──────────────────────────────────────────────────

        [TestMethod]
        public void GetForeGroundColor_NoFuncSet_ReturnsEmpty()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            var item = new GcTestItem { Name = "Alice" };
            col.GetForeGroundColor(item).Should().BeEmpty();
        }

        [TestMethod]
        public void GetForeGroundColor_FuncSet_ReturnsResult()
        {
            var col = new GridColumn<GcTestItem>(x => x.Score, null,
                ForeGroundFunc: item => item.Score > 50 ? "red" : "green");
            var high = new GcTestItem { Score = 80 };
            var low = new GcTestItem { Score = 20 };

            col.GetForeGroundColor(high).Should().Be("red");
            col.GetForeGroundColor(low).Should().Be("green");
        }

        // ─── GetBackGroundColor ──────────────────────────────────────────────────

        [TestMethod]
        public void GetBackGroundColor_NoFuncSet_ReturnsEmpty()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            var item = new GcTestItem { Name = "Alice" };
            col.GetBackGroundColor(item).Should().BeEmpty();
        }

        [TestMethod]
        public void GetBackGroundColor_FuncSet_ReturnsResult()
        {
            var col = new GridColumn<GcTestItem>(x => x.IsActive, null,
                BackGroundFunc: item => item.IsActive ? "lightgreen" : "");
            var active = new GcTestItem { IsActive = true };
            var inactive = new GcTestItem { IsActive = false };

            col.GetBackGroundColor(active).Should().Be("lightgreen");
            col.GetBackGroundColor(inactive).Should().BeEmpty();
        }

        // ─── GetText ────────────────────────────────────────────────────────────

        [TestMethod]
        public void GetText_StringColumn_ReturnsValue()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null);
            var item = new GcTestItem { Name = "Alice" };
            var result = col.GetText(item);
            result.Should().Be("Alice");
        }

        [TestMethod]
        public void GetText_NullValue_ReturnsEmpty()
        {
            var col = new GridColumn<GcTestItem>(x => x.EventDate, null);
            var item = new GcTestItem { EventDate = null };
            var result = col.GetText(item);
            result.Should().Be("");
        }

        [TestMethod]
        public void GetText_DateTimeDateOnly_FormatsAsDate()
        {
            var col = new GridColumn<GcTestItem>(x => x.EventDate, null);
            var item = new GcTestItem { EventDate = new DateTime(2024, 3, 15, 0, 0, 0) };
            var result = col.GetText(item).ToString();
            result.Should().Be("2024-03-15");
        }

        [TestMethod]
        public void GetText_DateTimeWithTime_FormatsWithTime()
        {
            var col = new GridColumn<GcTestItem>(x => x.EventDate, null);
            var item = new GcTestItem { EventDate = new DateTime(2024, 3, 15, 10, 30, 45) };
            var result = col.GetText(item).ToString();
            result.Should().Be("2024-03-15 10:30:45");
        }

        [TestMethod]
        public void GetText_WithFormat_InvokesFormat()
        {
            var col = new GridColumn<GcTestItem>(x => x.Score,
                (item, val) => new ColumnFormatInfo { Html = "SCORE:" + item.Score });
            var item = new GcTestItem { Score = 42 };
            var result = col.GetText(item) as ColumnFormatInfo;
            result.Should().NotBeNull();
            result!.Html.Should().Be("SCORE:42");
        }

        [TestMethod]
        public void GetText_WithFormatAndNeedFormatFalse_ReturnsFormatResultIfNotColumnFormatInfo()
        {
            var col = new GridColumn<GcTestItem>(x => x.Score,
                (item, val) => (object)("str_" + item.Score));
            var item = new GcTestItem { Score = 7 };
            var result = col.GetText(item, needFormat: false);
            result.Should().Be("str_7");
        }

        [TestMethod]
        public void GetText_EnumColumn_ReturnsEnumString()
        {
            var col = new GridColumn<GcTestItem>(x => x.Gender, null);
            var item = new GcTestItem { Gender = GenderEnum.Male };
            var result = col.GetText(item).ToString();
            result.Should().NotBeNullOrEmpty();
        }

        // ─── GetObject ──────────────────────────────────────────────────────────

        [TestMethod]
        public void GetObject_ReturnsPropertyValue()
        {
            var col = new GridColumn<GcTestItem>(x => x.Score, null);
            var item = new GcTestItem { Score = 99 };
            var result = col.GetObject(item);
            result.Should().Be(99);
        }

        // ─── Property setters ────────────────────────────────────────────────────

        [TestMethod]
        public void Sort_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                Sort = true
            };
            col.Sort.Should().BeTrue();
        }

        [TestMethod]
        public void Fixed_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                Fixed = GridColumnFixedEnum.Left
            };
            col.Fixed.Should().Be(GridColumnFixedEnum.Left);
        }

        [TestMethod]
        public void Align_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                Align = GridColumnAlignEnum.Center
            };
            col.Align.Should().Be(GridColumnAlignEnum.Center);
        }

        [TestMethod]
        public void Hide_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                Hide = true
            };
            col.Hide.Should().BeTrue();
        }

        [TestMethod]
        public void DisableExport_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                DisableExport = true
            };
            col.DisableExport.Should().BeTrue();
        }

        [TestMethod]
        public void UnResize_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                UnResize = true
            };
            col.UnResize.Should().BeTrue();
        }

        [TestMethod]
        public void ShowTotal_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Score, null)
            {
                ShowTotal = true
            };
            col.ShowTotal.Should().BeTrue();
        }

        [TestMethod]
        public void EditType_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                EditType = EditTypeEnum.TextBox
            };
            col.EditType.Should().Be(EditTypeEnum.TextBox);
        }

        [TestMethod]
        public void IsReadOnly_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                IsReadOnly = true
            };
            col.IsReadOnly.Should().BeTrue();
        }

        [TestMethod]
        public void ColumnType_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                ColumnType = GridColumnTypeEnum.Action
            };
            col.ColumnType.Should().Be(GridColumnTypeEnum.Action);
        }

        [TestMethod]
        public void Event_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                Event = "myEvent"
            };
            col.Event.Should().Be("myEvent");
        }

        [TestMethod]
        public void Id_CanBeSetAndRead()
        {
            var col = new GridColumn<GcTestItem>(x => x.Name, null)
            {
                Id = "col-1"
            };
            col.Id.Should().Be("col-1");
        }
    }
}
