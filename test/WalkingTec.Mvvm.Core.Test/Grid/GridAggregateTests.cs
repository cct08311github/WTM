#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Grid
{
    // ─── model ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A simple entity with a numeric column for aggregate testing.
    /// Uses SQLite shared-memory mode as required by known-quirks.md —
    /// EF Core InMemory cannot translate Sum/Avg/Min/Max aggregate queries.
    /// </summary>
    public class SaleRecord : TopBasePoco
    {
        [Display(Name = "Amount")]
        public decimal Amount { get; set; }

        [Display(Name = "Quantity")]
        public int Quantity { get; set; }

        [Display(Name = "Product")]
        public string Product { get; set; } = string.Empty;
    }

    /// <summary>
    /// Minimal context that only registers <see cref="SaleRecord"/>.
    /// Extends <see cref="EmptyContext"/> — NOT FrameworkContext or DataContext —
    /// to avoid the "duplicate column name: MajorId" collision that occurs when
    /// FrameworkContext's full WTM entity graph is migrated in SQLite
    /// shared-memory mode (documented in known-quirks.md).
    /// </summary>
    public class SaleRecordContext : EmptyContext
    {
        public SaleRecordContext(string cs, DBTypeEnum dbtype)
            : base(cs, dbtype) { }

        public DbSet<SaleRecord> SaleRecords { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Register only SaleRecord; no WTM entity scanning.
            modelBuilder.Entity<SaleRecord>(b =>
            {
                b.HasKey(e => e.ID);
                b.Property(e => e.Amount);
                b.Property(e => e.Quantity);
                b.Property(e => e.Product).HasMaxLength(200);
            });
        }
    }

    // ─── ListVM ────────────────────────────────────────────────────────────────

    public class SaleRecordListVM : BasePagedListVM<SaleRecord, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<SaleRecord>> InitGridHeader()
        {
            return new List<IGridColumn<SaleRecord>>
            {
                this.MakeGridHeader(x => x.Product),
                this.MakeGridHeader(x => x.Amount)
                    .SetAggregate(GridAggregateTypeEnum.Sum),
                this.MakeGridHeader(x => x.Quantity)
                    .SetAggregate(GridAggregateTypeEnum.Avg),
            };
        }

        public override IOrderedQueryable<SaleRecord> GetSearchQuery()
        {
            return DC!.Set<SaleRecord>().OrderByDescending(x => x.Amount);
        }
    }

    public class SaleRecordMinMaxListVM : BasePagedListVM<SaleRecord, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<SaleRecord>> InitGridHeader()
        {
            return new List<IGridColumn<SaleRecord>>
            {
                this.MakeGridHeader(x => x.Amount)
                    .SetAggregate(GridAggregateTypeEnum.Min),
                this.MakeGridHeader(x => x.Quantity)
                    .SetAggregate(GridAggregateTypeEnum.Max),
            };
        }

        public override IOrderedQueryable<SaleRecord> GetSearchQuery()
        {
            return DC!.Set<SaleRecord>().OrderByDescending(x => x.Amount);
        }
    }

    public class SaleRecordCountListVM : BasePagedListVM<SaleRecord, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<SaleRecord>> InitGridHeader()
        {
            return new List<IGridColumn<SaleRecord>>
            {
                this.MakeGridHeader(x => x.Product)
                    .SetAggregate(GridAggregateTypeEnum.Count),
            };
        }

        public override IOrderedQueryable<SaleRecord> GetSearchQuery()
        {
            return DC!.Set<SaleRecord>().OrderByDescending(x => x.Amount);
        }
    }

    public class SaleRecordNoAggListVM : BasePagedListVM<SaleRecord, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<SaleRecord>> InitGridHeader()
        {
            return new List<IGridColumn<SaleRecord>>
            {
                this.MakeGridHeader(x => x.Product),
                this.MakeGridHeader(x => x.Amount),
            };
        }

        public override IOrderedQueryable<SaleRecord> GetSearchQuery()
        {
            return DC!.Set<SaleRecord>().OrderByDescending(x => x.Amount);
        }
    }

    public class SaleRecordValueTypeCountListVM : BasePagedListVM<SaleRecord, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<SaleRecord>> InitGridHeader()
        {
            return new List<IGridColumn<SaleRecord>>
            {
                // Quantity is int (non-nullable value type) — this previously threw at
                // Expression.NotEqual(body, Constant(null, typeof(int))) and silently
                // produced a blank footer cell (#431 bug fix).
                this.MakeGridHeader(x => x.Quantity)
                    .SetAggregate(GridAggregateTypeEnum.Count),
            };
        }

        public override IOrderedQueryable<SaleRecord> GetSearchQuery()
        {
            return DC!.Set<SaleRecord>().OrderByDescending(x => x.Amount);
        }
    }

    // ─── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tests for server-side column aggregate computation (#431).
    ///
    /// Uses SQLite shared-memory (not EF Core InMemory) because Sum/Avg/Min/Max
    /// aggregate queries are not translatable by the InMemory provider
    /// (see known-quirks.md — "EF Core InMemory Provider Limitations").
    /// </summary>
    [TestClass]
    public class GridAggregateTests
    {
        private static string MakeSharedMemoryCs(string seed)
            => $"Data Source=file:aggregate_test_{seed}?mode=memory&cache=shared";

        private static SaleRecordContext CreateSeededDb(string seed)
        {
            var cs = MakeSharedMemoryCs(seed);
            var ctx = new SaleRecordContext(cs, DBTypeEnum.SQLite);
            ctx.Database.OpenConnection();
            ctx.Database.EnsureCreated();

            ctx.SaleRecords.AddRange(
                new SaleRecord { ID = Guid.NewGuid(), Product = "A", Amount = 100m, Quantity = 3 },
                new SaleRecord { ID = Guid.NewGuid(), Product = "B", Amount = 200m, Quantity = 7 },
                new SaleRecord { ID = Guid.NewGuid(), Product = "C", Amount = 300m, Quantity = 10 }
            );
            ctx.SaveChanges();
            return ctx;
        }

        // ── Sum / Avg ───────────────────────────────────────────────────────────

        [TestMethod]
        public void ComputeAggregates_Sum_ReturnsFullQueryTotal()
        {
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordListVM>();
            vm.NeedPage = false;

            var result = vm.ComputeAggregates();

            result.Should().ContainKey("Amount");
            // 100 + 200 + 300 = 600
            decimal.Parse(result["Amount"]).Should().Be(600m);
        }

        [TestMethod]
        public void ComputeAggregates_Avg_ReturnsCorrectAverage()
        {
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordListVM>();
            vm.NeedPage = false;

            var result = vm.ComputeAggregates();

            result.Should().ContainKey("Quantity");
            // (3 + 7 + 10) / 3 = 6.666...
            var avg = decimal.Parse(result["Quantity"]);
            avg.Should().BeApproximately(20m / 3m, 0.01m);
        }

        [TestMethod]
        public void ComputeAggregates_Sum_IsOverFullSet_NotJustCurrentPage()
        {
            // Verify that paging does NOT restrict the aggregate —
            // even when only 1 row per page is returned, Sum should cover all 3 rows.
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordListVM>();
            vm.NeedPage = true;
            vm.Searcher.Limit = 1;   // only 1 row per page
            vm.Searcher.Page = 1;

            var result = vm.ComputeAggregates();

            // DoSearch with page=1, limit=1 returns only 1 row, but ComputeAggregates
            // should still return the sum of ALL 3 rows (600).
            result.Should().ContainKey("Amount");
            decimal.Parse(result["Amount"]).Should().Be(600m);
        }

        // ── Min / Max ───────────────────────────────────────────────────────────

        [TestMethod]
        public void ComputeAggregates_Min_ReturnsSmallestValue()
        {
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordMinMaxListVM>();
            vm.NeedPage = false;

            var result = vm.ComputeAggregates();

            result.Should().ContainKey("Amount");
            // Parse instead of exact string match — decimal representation may vary ("100" vs "100.0")
            decimal.Parse(result["Amount"]).Should().Be(100m);
        }

        [TestMethod]
        public void ComputeAggregates_Max_ReturnsLargestValue()
        {
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordMinMaxListVM>();
            vm.NeedPage = false;

            var result = vm.ComputeAggregates();

            result.Should().ContainKey("Quantity");
            // int.Parse for the int quantity column
            int.Parse(result["Quantity"]).Should().Be(10);
        }

        // ── Count ───────────────────────────────────────────────────────────────

        [TestMethod]
        public void ComputeAggregates_Count_ReturnsNonNullRowCount()
        {
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordCountListVM>();
            vm.NeedPage = false;

            var result = vm.ComputeAggregates();

            result.Should().ContainKey("Product");
            result["Product"].Should().Be("3");
        }

        // ── No-aggregate columns ─────────────────────────────────────────────────

        [TestMethod]
        public void ComputeAggregates_NoAggregateColumns_ReturnsEmptyDictionary()
        {
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordNoAggListVM>();
            vm.NeedPage = false;

            var result = vm.ComputeAggregates();

            result.Should().BeEmpty();
        }

        // ── SetAggregate / SetRichColumnType fluent APIs ────────────────────────

        [TestMethod]
        public void SetAggregate_FluentExtension_SetsAggregateType()
        {
            var col = new GridColumn<SaleRecord>(x => x.Amount, null);
            col.SetAggregate(GridAggregateTypeEnum.Sum);

            col.AggregateType.Should().Be(GridAggregateTypeEnum.Sum);
        }

        [TestMethod]
        public void SetRichColumnType_FluentExtension_SetsAllOptions()
        {
            var col = new GridColumn<SaleRecord>(x => x.Amount, null);
            col.SetRichColumnType(GridRichColumnTypeEnum.Currency, currencyFormat: "#,##0.00");

            col.RichColumnType.Should().Be(GridRichColumnTypeEnum.Currency);
            col.CurrencyFormat.Should().Be("#,##0.00");
        }

        [TestMethod]
        public void SetRichColumnType_Tag_SetsColorAndType()
        {
            var col = new GridColumn<SaleRecord>(x => x.Product, null);
            col.SetRichColumnType(GridRichColumnTypeEnum.Tag, tagColor: "green");

            col.RichColumnType.Should().Be(GridRichColumnTypeEnum.Tag);
            col.TagColor.Should().Be("green");
        }

        [TestMethod]
        public void SetRichColumnType_Image_SetsSize()
        {
            var col = new GridColumn<SaleRecord>(x => x.Product, null);
            col.SetRichColumnType(GridRichColumnTypeEnum.Image, imageSize: 48);

            col.RichColumnType.Should().Be(GridRichColumnTypeEnum.Image);
            col.ImageSize.Should().Be(48);
        }

        [TestMethod]
        public void GridColumn_Default_AggregateType_IsNone()
        {
            var col = new GridColumn<SaleRecord>(x => x.Amount, null);

            col.AggregateType.Should().Be(GridAggregateTypeEnum.None);
        }

        [TestMethod]
        public void GridColumn_Default_RichColumnType_IsDefault()
        {
            var col = new GridColumn<SaleRecord>(x => x.Amount, null);

            col.RichColumnType.Should().Be(GridRichColumnTypeEnum.Default);
        }

        [TestMethod]
        public void ComputeAggregates_Count_NonNullableValueType_ReturnsRowCount()
        {
            // Regression: Count over a non-nullable value-type column (int, decimal, bool...)
            // previously threw InvalidOperationException inside Expression.NotEqual(body, null)
            // which was silently swallowed, returning a blank footer cell instead of the count.
            // Fix: detect value types and use query.Count() directly (all value-type rows are
            // non-null by definition).
            var seed = Guid.NewGuid().ToString("N");
            using var ctx = CreateSeededDb(seed);  // seeds 3 rows (Quantity 3, 7, 10)

            var vm = MockWtmContext.CreateWtmContext(ctx).CreateVM<SaleRecordValueTypeCountListVM>();
            vm.NeedPage = false;

            var result = vm.ComputeAggregates();

            result.Should().ContainKey("Quantity",
                "Count aggregate on a non-nullable int column must produce a result");
            result["Quantity"].Should().Be("3",
                "All 3 rows have a non-null int Quantity value, so Count must be 3");
        }
    }
}
