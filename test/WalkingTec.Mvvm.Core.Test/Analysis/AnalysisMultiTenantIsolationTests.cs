#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// 多租戶 Analysis 查詢隔離整合測試（Issue #374）。
    ///
    /// 目的：驗證 EF Core ITenant global query filter 在 Analysis 查詢路徑
    ///        中確實生效，確保跨租戶資料不洩露。
    ///
    /// 測試策略：
    ///   1. 使用 SQLite shared-memory 資料庫，以 WriteContext（無 filter）播種
    ///      TenantA 與 TenantB 的資料列。
    ///   2. 建立 TenantReadContext，在 OnModelCreating 中以與 WTM DataContext
    ///      相同的 Expression Tree 模式（Expression.PropertyOrField(Constant(this), "TenantCode")）
    ///      套用 HasQueryFilter。
    ///   3. 分別以 TenantA / TenantB 的 context 執行 AnalysisQueryEngine.Execute()，
    ///      確認結果只包含對應租戶的資料列。
    /// </summary>
    [TestClass]
    public class AnalysisMultiTenantIsolationTests
    {
        // ─── 測試模型 ──────────────────────────────────────────────────────────

        private class TenantSaleRecord : TopBasePoco, ITenant
        {
            public string? TenantCode { get; set; }

            [Dimension(DisplayName = "地區")]
            public string Region { get; set; } = "";

            [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        /// <summary>
        /// 無 query filter 的寫入 context，用於播種跨租戶測試資料。
        /// </summary>
        private class WriteContext : DbContext
        {
            public WriteContext(DbContextOptions opts) : base(opts) { }
            public DbSet<TenantSaleRecord> TenantSales { get; set; }
        }

        /// <summary>
        /// 套用 ITenant global query filter 的讀取 context，
        /// 鏡像 WTM DataContext.OnModelCreating 的 Expression Tree 模式。
        /// </summary>
        private class TenantReadContext : DbContext
        {
            // EF Core 在執行查詢時讀取此屬性，而非在 OnModelCreating 時快取值。
            public string? TenantCode { get; }

            public TenantReadContext(DbContextOptions opts, string? tenantCode) : base(opts)
            {
                TenantCode = tenantCode;
            }

            public DbSet<TenantSaleRecord> TenantSales { get; set; }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                base.OnModelCreating(modelBuilder);

                // 鏡像 WTM DataContext:181 的 ITenant global query filter 實作：
                // Expression.PropertyOrField(Expression.Constant(this), "TenantCode")
                // 讓 EF Core 在每次查詢時讀取 this.TenantCode 的當前值。
                var pe = Expression.Parameter(typeof(TenantSaleRecord));
                var tenantCodeProp = Expression.Property(pe, nameof(TenantSaleRecord.TenantCode));
                var contextTenantCode = Expression.PropertyOrField(
                    Expression.Constant(this), nameof(TenantCode));
                var filter = Expression.Equal(tenantCodeProp, contextTenantCode);
                modelBuilder.Entity<TenantSaleRecord>()
                    .HasQueryFilter(Expression.Lambda<Func<TenantSaleRecord, bool>>(filter, pe));
            }
        }

        // ─── 基礎設施 ──────────────────────────────────────────────────────────

        private SqliteConnection _keepAlive;
        private string _dbName;
        private IEnumerable<AnalysisFieldMeta> _whitelist;

        [TestInitialize]
        public void Setup()
        {
            _dbName = $"multitenant_{Guid.NewGuid():N}";
            // keep-alive 連線確保 shared-memory 資料庫在整個測試期間存活
            _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
            _keepAlive.Open();

            // 建立 schema 並播種跨租戶資料（WriteContext 無 query filter）
            var writeOpts = new DbContextOptionsBuilder()
                .UseSqlite(_keepAlive)
                .Options;
            using var writeCtx = new WriteContext(writeOpts);
            writeCtx.Database.EnsureCreated();

            // TenantA：2 筆（華東 100, 華南 200）
            writeCtx.TenantSales.AddRange(
                new TenantSaleRecord { ID = Guid.NewGuid(), TenantCode = "A", Region = "華東", Amount = 100m },
                new TenantSaleRecord { ID = Guid.NewGuid(), TenantCode = "A", Region = "華南", Amount = 200m }
            );
            // TenantB：1 筆（華北 999）
            writeCtx.TenantSales.Add(
                new TenantSaleRecord { ID = Guid.NewGuid(), TenantCode = "B", Region = "華北", Amount = 999m }
            );
            writeCtx.SaveChanges();

            _whitelist = AnalysisFieldScanner.ScanModel(typeof(TenantSaleRecord));
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Dispose();
        }

        private TenantReadContext CreateReadCtx(string? tenantCode)
        {
            var opts = new DbContextOptionsBuilder<TenantReadContext>()
                .UseSqlite($"DataSource={_dbName}?mode=memory&cache=shared")
                .Options;
            return new TenantReadContext(opts, tenantCode);
        }

        private static AnalysisQueryEngine Engine() =>
            new AnalysisQueryEngine(GroupByStrategyResolver.Default);

        private static AnalysisQueryRequest Req() => new AnalysisQueryRequest
        {
            Dimensions = new List<string> { "Region" },
            Measures   = new List<MeasureRequest>
                { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
            Filters = new List<FilterCondition>()
        };

        // ─── 租戶隔離測試 ──────────────────────────────────────────────────────

        /// <summary>
        /// TenantA context 執行 Analysis 查詢，結果必須只包含 TenantA 的 2 筆資料，
        /// TenantB 的「華北」記錄不得出現。
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        public void Analysis_query_with_tenantA_context_excludes_tenantB_records()
        {
            using var ctx = CreateReadCtx("A");
            var result = Engine().Execute(ctx.TenantSales.AsQueryable(), Req(), _whitelist);

            Assert.AreEqual(2, result.Rows.Count,
                "TenantA context 應只看到 TenantA 的 2 筆記錄（華東、華南）");
            var regions = result.Rows.Select(r => r["Region"]?.ToString()).ToHashSet();
            CollectionAssert.DoesNotContain(regions.ToList(), "華北",
                "TenantB 的「華北」記錄不應出現在 TenantA 的查詢結果中");
        }

        /// <summary>
        /// TenantB context 執行 Analysis 查詢，結果必須只包含 TenantB 的 1 筆資料，
        /// TenantA 的「華東」、「華南」記錄不得出現。
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        public void Analysis_query_with_tenantB_context_excludes_tenantA_records()
        {
            using var ctx = CreateReadCtx("B");
            var result = Engine().Execute(ctx.TenantSales.AsQueryable(), Req(), _whitelist);

            Assert.AreEqual(1, result.Rows.Count,
                "TenantB context 應只看到 TenantB 的 1 筆記錄（華北）");
            var regions = result.Rows.Select(r => r["Region"]?.ToString()).ToHashSet();
            CollectionAssert.DoesNotContain(regions.ToList(), "華東",
                "TenantA 的「華東」記錄不應出現在 TenantB 的查詢結果中");
            CollectionAssert.DoesNotContain(regions.ToList(), "華南",
                "TenantA 的「華南」記錄不應出現在 TenantB 的查詢結果中");
        }

        /// <summary>
        /// 切換同一 context 實例的 TenantCode 不適用本場景（TenantCode 是 readonly）。
        /// 改用 null tenant：由於所有資料列均有 TenantCode，null 租戶應看到 0 筆資料，
        /// 驗證 filter 不因租戶代碼缺失而開放所有資料（fail-open 防護）。
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        public void Analysis_query_with_null_tenant_context_returns_no_tenant_records()
        {
            using var ctx = CreateReadCtx(null);
            var result = Engine().Execute(ctx.TenantSales.AsQueryable(), Req(), _whitelist);

            Assert.AreEqual(0, result.Rows.Count,
                "null 租戶 context 不應看到任何已分配租戶代碼的資料列（防止 fail-open 洩露）");
        }

        /// <summary>
        /// 驗證原始資料庫確實包含 3 筆資料（前置條件確認），
        /// 確保「0 筆結果」是 filter 生效而非資料未播種。
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        public void Database_contains_all_three_seeded_records_without_filter()
        {
            // WriteContext 無 query filter，應看到全部 3 筆
            var opts = new DbContextOptionsBuilder()
                .UseSqlite(_keepAlive)
                .Options;
            using var ctx = new WriteContext(opts);
            Assert.AreEqual(3, ctx.TenantSales.Count(),
                "資料庫應包含所有 3 筆播種資料（2×TenantA + 1×TenantB），確認 filter 是隔離原因而非資料缺失");
        }
    }
}
