using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    /// <summary>
    /// GenerateExcel runs AfterDoSearcher on the rows it exports (RunAfterDoSearcherOnExport, default true), so a column that is only filled
    /// in AfterDoSearcher is exported with the same content as the grid shows (#437).
    /// </summary>
    [TestClass]
    public class ExcelExportAfterDoSearcherTests
    {
        /// <summary>Remark is only filled in AfterDoSearcher (the query never selects it).</summary>
        private class FilledAfterSearchListVM : BasePagedListVM<GoodsSpecification, BaseSearcher>
        {
            public bool RunOnExport { get; set; } = true;
            public List<ListVMSearchModeEnum> ModesSeen { get; } = new List<ListVMSearchModeEnum>();
            public List<int> RowCounts { get; } = new List<int>();

            protected override bool RunAfterDoSearcherOnExport => RunOnExport;

            protected override IEnumerable<IGridColumn<GoodsSpecification>> InitGridHeader()
            {
                return new List<GridColumn<GoodsSpecification>>
                {
                    this.MakeGridHeader(x => x.ID),
                    this.MakeGridHeader(x => x.Name),
                    this.MakeGridHeader(x => x.Remark),
                    this.MakeGridHeaderAction(width: 100),
                };
            }

            public override IOrderedQueryable<GoodsSpecification> GetSearchQuery()
            {
                return DC.Set<GoodsSpecification>()
                    .Select(x => new GoodsSpecification { ID = x.ID, Name = x.Name, OrderNum = x.OrderNum, Remark = "" })
                    .OrderBy(x => x.OrderNum);
            }

            public override void AfterDoSearcher()
            {
                ModesSeen.Add(SearcherMode);
                RowCounts.Add(EntityList.Count);
                foreach (var item in EntityList)
                {
                    item.Remark = "shown-" + item.Name;
                }
                base.AfterDoSearcher();
            }
        }

        private static (FilledAfterSearchListVM vm, List<int> ids) NewVm(ListVMSearchModeEnum mode)
        {
            var wtm = MockWtmContext.CreateWtmContext(new DataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory), "exportuser");
            var vm = new FilledAfterSearchListVM { Wtm = wtm, SearcherMode = mode };
            var added = new List<GoodsSpecification>();
            foreach (var (name, order) in new[] { ("a", 1), ("b", 2), ("c", 3) })
            {
                var g = new GoodsSpecification { Name = name, OrderNum = order, Remark = "stored" };
                vm.DC.Set<GoodsSpecification>().Add(g);
                added.Add(g);
            }
            vm.DC.SaveChanges();
            var ids = added.Select(x => x.ID).ToList();
            return (vm, ids);
        }

        [TestMethod]
        public void Export_ColumnFilledInAfterDoSearcher_IsExported()
        {
            var (vm, _) = NewVm(ListVMSearchModeEnum.Export);

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(4, rows.Count, "header + 3 data rows");
            CollectionAssert.AreEqual(new[] { "a", "shown-a" }, rows[1]);
            CollectionAssert.AreEqual(new[] { "b", "shown-b" }, rows[2]);
            CollectionAssert.AreEqual(new[] { "c", "shown-c" }, rows[3]);
            Assert.AreEqual(1, vm.ModesSeen.Count, "AfterDoSearcher runs once for a single file");
            Assert.AreEqual(3, vm.RowCounts[0], "it sees all the rows to export");
        }

        [TestMethod]
        public void CheckExport_ColumnFilledInAfterDoSearcher_IsExported()
        {
            var (vm, ids) = NewVm(ListVMSearchModeEnum.CheckExport);
            vm.Ids = new List<string> { ids[0].ToString(), ids[2].ToString() };

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(3, rows.Count, "header + 2 checked rows");
            CollectionAssert.AreEqual(new[] { "a", "shown-a" }, rows[1]);
            CollectionAssert.AreEqual(new[] { "c", "shown-c" }, rows[2]);
            Assert.AreEqual(1, vm.ModesSeen.Count);
        }

        [TestMethod]
        public void Export_OptOut_KeepsTheOldBehavior()
        {
            var (vm, _) = NewVm(ListVMSearchModeEnum.Export);
            vm.RunOnExport = false;

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(0, vm.ModesSeen.Count, "AfterDoSearcher is not called");
            Assert.AreEqual(4, rows.Count);
            CollectionAssert.AreEqual(new[] { "a", "" }, rows[1], "the display-only column stays empty, as before");
        }

        [TestMethod]
        public void CheckExport_OptOut_KeepsTheOldBehavior()
        {
            var (vm, ids) = NewVm(ListVMSearchModeEnum.CheckExport);
            vm.RunOnExport = false;
            vm.Ids = new List<string> { ids[1].ToString() };

            var rows = XlsxTestHelper.ReadSheet(vm.GenerateExcel());

            Assert.AreEqual(0, vm.ModesSeen.Count);
            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEqual(new[] { "b", "" }, rows[1]);
        }

        [TestMethod]
        public void AfterDoSearcher_SeesTheExportMode()
        {
            var (export, _) = NewVm(ListVMSearchModeEnum.Export);
            export.GenerateExcel();
            CollectionAssert.AreEqual(new[] { ListVMSearchModeEnum.Export }, export.ModesSeen);

            var (checkedVm, ids) = NewVm(ListVMSearchModeEnum.CheckExport);
            checkedVm.Ids = new List<string> { ids[0].ToString() };
            checkedVm.GenerateExcel();
            CollectionAssert.AreEqual(new[] { ListVMSearchModeEnum.CheckExport }, checkedVm.ModesSeen);
        }

        [TestMethod]
        public void Export_RowsLeftInEntityListAreTheOnesExported()
        {
            var (vm, _) = NewVm(ListVMSearchModeEnum.Export);
            var inner = new ReplacingListVM { Wtm = vm.Wtm, SearcherMode = ListVMSearchModeEnum.Export };

            var rows = XlsxTestHelper.ReadSheet(inner.GenerateExcel());

            Assert.AreEqual(2, rows.Count, "header + the single row AfterDoSearcher left in EntityList");
            CollectionAssert.AreEqual(new[] { "only", "" }, rows[1]);
        }

        /// <summary>AfterDoSearcher replaces the list (as a tree flattening override does).</summary>
        private class ReplacingListVM : BasePagedListVM<GoodsSpecification, BaseSearcher>
        {
            protected override IEnumerable<IGridColumn<GoodsSpecification>> InitGridHeader()
            {
                return new List<GridColumn<GoodsSpecification>>
                {
                    this.MakeGridHeader(x => x.Name),
                    this.MakeGridHeader(x => x.Remark),
                };
            }

            public override IOrderedQueryable<GoodsSpecification> GetSearchQuery()
            {
                return DC.Set<GoodsSpecification>().OrderBy(x => x.OrderNum);
            }

            public override void AfterDoSearcher()
            {
                EntityList = new List<GoodsSpecification> { new GoodsSpecification { Name = "only", Remark = "" } };
            }
        }
    }
}
