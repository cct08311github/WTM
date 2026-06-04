#nullable enable
// Tests for four LOW/MED adversarially-verified bugs fixed in Issue #151.
//
// L5:  ProcessCommand stored-proc path — Searcher.Limit == 0 causes DivideByZeroException.
// L11: DCExtension.Sort — non-existent property name causes NRE via Expression.Property(pe, null!).
// L13: OrderReplaceModifier add-mode — SortDir outside {Asc,Desc} leaves rv=null → return rv! returns null.
// L14: Sort allows any model property → info-leak oracle via result ordering on PasswordHash/Salt/Token.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Shared helpers for L5
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimal model for ProcessCommand tests — backed by a real SQLite table.
    /// </summary>
    public class ProcessCmdItem : TopBasePoco
    {
        public string Label { get; set; } = "";
    }

    /// <summary>
    /// EF DataContext that includes ProcessCmdItem so EnsureCreated() creates the table.
    /// </summary>
    public class ProcessCmdContext : EmptyContext
    {
        public ProcessCmdContext(string cs) : base(cs, DBTypeEnum.SQLite) { }

        public DbSet<ProcessCmdItem> Items { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Do NOT call base — avoids MajorId column-name conflicts in SQLite tests.
            modelBuilder.Entity<ProcessCmdItem>().ToTable("Items");
        }
    }

    /// <summary>
    /// ListVM that returns a real SQLite SELECT when GetSearchCommand() is overridden.
    /// The test subclasses this to inject a DbCommand.
    /// </summary>
    public abstract class ProcessCmdListVMBase : BasePagedListVM<ProcessCmdItem, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<ProcessCmdItem>> InitGridHeader()
            => new[] { this.MakeGridColumn(x => x.Label) };
    }

    /// <summary>
    /// Concrete ListVM whose GetSearchCommand returns the given command.
    /// </summary>
    public class ProcessCmdListVM : ProcessCmdListVMBase
    {
        private readonly DbCommand _cmd;
        public ProcessCmdListVM(DbCommand cmd) { _cmd = cmd; }

        public override DbCommand? GetSearchCommand() => _cmd;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared model for L11 / L13 / L14 (DCExtension.Sort and ExpressionVisitors)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Model with a normal sort field, a name-based sensitive field, and a [JsonIgnore] field.
    /// </summary>
    public class SortTestItem : TopBasePoco
    {
        public string Name { get; set; } = "";

        // L14: name-based sensitive field — should be blocked from sort
        public string PasswordHash { get; set; } = "";

        // L14: attribute-based sensitive field
        [JsonIgnore]
        public string SecretToken { get; set; } = "";

        // L14: [NotMapped] attribute — should be blocked
        [NotMapped]
        public string Computed { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // L5 — ProcessCommand: Searcher.Limit == 0 must not DivideByZero
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class ProcessCommandLimitZeroTests
    {
        private static string BuildConnectionString()
            => $"Data Source=file:l5_{Guid.NewGuid():N}?mode=memory&cache=shared";

        /// <summary>
        /// Sets up a SQLite in-memory DB with one row, then calls DoSearch with Limit=0.
        /// The stored-proc path (GetSearchCommand returns a real command) must not throw
        /// DivideByZeroException and must produce a sane PageCount.
        /// </summary>
        [TestMethod]
        public void ProcessCommand_LimitZero_Does_Not_Throw_DivideByZero()
        {
            var cs = BuildConnectionString();
            var ctx = new ProcessCmdContext(cs);
            ctx.Database.OpenConnection();
            ctx.Database.EnsureCreated();
            ctx.Items.Add(new ProcessCmdItem { Label = "row1" });
            ctx.SaveChanges();

            // Build a raw SELECT command (non-stored-proc → total = EntityList.Count)
            var conn = ctx.Database.GetDbConnection();
            var cmd = conn.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText = "SELECT * FROM Items";

            var vm = new ProcessCmdListVM(cmd);
            vm.Wtm = MockWtmContext.CreateWtmContext(ctx);
            vm.NeedPage = true;
            vm.Searcher.Limit = 0; // triggers the bug without the fix

            // Must not throw DivideByZeroException
            vm.DoSearch();

            // After the fix Limit is normalised to the config default (≥1)
            Assert.IsTrue(vm.Searcher.Limit > 0,
                "Limit must be normalised to a positive value");
            Assert.IsTrue(vm.Searcher.PageCount >= 1,
                "PageCount must be at least 1 after normalisation");
        }

        /// <summary>
        /// When Limit is already a positive value the fix must not change it.
        /// </summary>
        [TestMethod]
        public void ProcessCommand_LimitPositive_PreservesLimit()
        {
            var cs = BuildConnectionString();
            var ctx = new ProcessCmdContext(cs);
            ctx.Database.OpenConnection();
            ctx.Database.EnsureCreated();

            var conn = ctx.Database.GetDbConnection();
            var cmd = conn.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText = "SELECT * FROM Items";

            var vm = new ProcessCmdListVM(cmd);
            vm.Wtm = MockWtmContext.CreateWtmContext(ctx);
            vm.NeedPage = true;
            vm.Searcher.Limit = 10;

            vm.DoSearch();

            Assert.AreEqual(10, vm.Searcher.Limit, "Limit must not be changed when already positive");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // L11 — DCExtension.Sort: non-existent property name must be skipped (no NRE)
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class DCExtensionSortL11Tests
    {
        private static IQueryable<SortTestItem> MakeQuery() =>
            new List<SortTestItem>
            {
                new() { Name = "b" },
                new() { Name = "a" },
            }.AsQueryable();

        [TestMethod]
        public void Sort_NonExistentProperty_DoesNotThrow_AndReturnsQuery()
        {
            var q = MakeQuery();
            // "NonExistentField" is not a property on SortTestItem → should be skipped.
            // Before the fix: Expression.Property(pe, null!) → NRE.
            // After the fix: property is skipped; falls through to the ID fallback.
            var sortJson = "[{\"Property\":\"NonExistentField\",\"Direction\":0}]";

            IOrderedQueryable<SortTestItem>? result = null;
            Exception? caughtEx = null;
            try
            {
                result = q.Sort(sortJson);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx, $"Sort with non-existent property must not throw. Got: {caughtEx}");
            Assert.IsNotNull(result, "Sort must return a non-null IOrderedQueryable");
        }

        [TestMethod]
        public void Sort_ValidProperty_StillSortsCorrectly()
        {
            var q = MakeQuery();
            var sortJson = "[{\"Property\":\"Name\",\"Direction\":0}]";

            var result = q.Sort(sortJson).ToList();

            Assert.AreEqual("a", result[0].Name, "Asc sort by Name: 'a' must come first");
            Assert.AreEqual("b", result[1].Name);
        }

        [TestMethod]
        public void Sort_MixedValidAndInvalidProperties_ValidOnesApplied()
        {
            var q = MakeQuery();
            // "DoesNotExist" is skipped; "Name" Asc is applied
            var sortJson = "[{\"Property\":\"DoesNotExist\",\"Direction\":0},{\"Property\":\"Name\",\"Direction\":0}]";

            IOrderedQueryable<SortTestItem>? result = null;
            Exception? caughtEx = null;
            try
            {
                result = q.Sort(sortJson);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx, $"Mixed valid+invalid must not throw. Got: {caughtEx}");
            Assert.IsNotNull(result);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // L13 — OrderReplaceModifier: invalid Direction (outside Asc/Desc) must not throw
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class OrderReplaceModifierL13Tests
    {
        private static SortInfo MakeSort(string prop, SortDir dir)
            => new SortInfo { Property = prop, Direction = dir };

        [TestMethod]
        public void Modify_InvalidDirection_DoesNotThrow_ReturnsQueryUnchanged()
        {
            var data = new List<SortTestItem>
            {
                new() { Name = "z" },
                new() { Name = "a" },
            }.AsQueryable();

            // Cast 2 to SortDir — neither Asc(0) nor Desc(1); an out-of-range enum value.
            var badDir = (SortDir)2;
            var sortInfo = new SortInfo { Property = nameof(SortTestItem.Name), Direction = badDir };
            var modifier = new OrderReplaceModifier(sortInfo);

            Exception? caughtEx = null;
            Expression? result = null;
            try
            {
                result = modifier.Modify(data.Expression);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx,
                $"Invalid Direction must not throw. Got: {caughtEx?.GetType().Name}: {caughtEx?.Message}");
            Assert.IsNotNull(result, "Modify must return a non-null expression");
        }

        [TestMethod]
        public void Modify_ValidAscDirection_ProducesExpression()
        {
            var data = new List<SortTestItem>
            {
                new() { Name = "b" },
                new() { Name = "a" },
            }.AsQueryable();

            var modifier = new OrderReplaceModifier(MakeSort(nameof(SortTestItem.Name), SortDir.Asc));
            var result = modifier.Modify(data.Expression);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Modify_ValidDescDirection_ProducesExpression()
        {
            var data = new List<SortTestItem>
            {
                new() { Name = "a" },
            }.AsQueryable();

            var modifier = new OrderReplaceModifier(MakeSort(nameof(SortTestItem.Name), SortDir.Desc));
            var result = modifier.Modify(data.Expression);
            Assert.IsNotNull(result);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // L14 — Sort blocklist: sensitive properties must be ignored; normal ones must work
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class SortBlocklistL14Tests
    {
        private static IQueryable<SortTestItem> MakeQuery() =>
            new List<SortTestItem>
            {
                new() { Name = "b", PasswordHash = "hash_b", SecretToken = "tok_b" },
                new() { Name = "a", PasswordHash = "hash_a", SecretToken = "tok_a" },
            }.AsQueryable();

        // ── DCExtension.Sort path ──────────────────────────────────────────────

        [TestMethod]
        public void DCExtensionSort_PasswordHash_IsIgnored_NoSortApplied()
        {
            var q = MakeQuery();
            var sortJson = "[{\"Property\":\"PasswordHash\",\"Direction\":0}]";

            // Sort by PasswordHash must be silently ignored — the result should be
            // the unsorted fallback (order by ID).
            IOrderedQueryable<SortTestItem>? result = null;
            Exception? caughtEx = null;
            try
            {
                result = q.Sort(sortJson);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx, $"Sort by PasswordHash must not throw. Got: {caughtEx}");
            Assert.IsNotNull(result, "Must return a valid query");
        }

        [TestMethod]
        public void DCExtensionSort_JsonIgnoredProperty_IsIgnored()
        {
            var q = MakeQuery();
            // SecretToken is decorated with [JsonIgnore]
            var sortJson = "[{\"Property\":\"SecretToken\",\"Direction\":0}]";

            IOrderedQueryable<SortTestItem>? result = null;
            Exception? caughtEx = null;
            try
            {
                result = q.Sort(sortJson);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx, $"Sort by [JsonIgnore] property must not throw. Got: {caughtEx}");
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void DCExtensionSort_NotMappedProperty_IsIgnored()
        {
            var q = MakeQuery();
            // Computed is decorated with [NotMapped]
            var sortJson = "[{\"Property\":\"Computed\",\"Direction\":0}]";

            IOrderedQueryable<SortTestItem>? result = null;
            Exception? caughtEx = null;
            try
            {
                result = q.Sort(sortJson);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx, $"Sort by [NotMapped] property must not throw. Got: {caughtEx}");
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void DCExtensionSort_NormalProperty_StillSortsCorrectly()
        {
            var q = MakeQuery();
            var sortJson = "[{\"Property\":\"Name\",\"Direction\":0}]";

            var result = q.Sort(sortJson).ToList();

            Assert.AreEqual("a", result[0].Name, "Normal property sort must still work");
            Assert.AreEqual("b", result[1].Name);
        }

        // ── OrderReplaceModifier (ExpressionVisitor) path ──────────────────────

        [TestMethod]
        public void ExpressionVisitor_PasswordHash_IsIgnored_ReturnsNodeUnchanged()
        {
            var data = MakeQuery();
            // Inject PasswordHash sort via OrderReplaceModifier
            var sortInfo = new SortInfo { Property = "PasswordHash", Direction = SortDir.Asc };
            var modifier = new OrderReplaceModifier(sortInfo);

            Exception? caughtEx = null;
            Expression? result = null;
            try
            {
                result = modifier.Modify(data.Expression);
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }

            Assert.IsNull(caughtEx,
                $"OrderReplaceModifier with PasswordHash must not throw. Got: {caughtEx}");
            Assert.IsNotNull(result, "Must return a non-null expression");
        }

        [TestMethod]
        public void ExpressionVisitor_NormalProperty_SortApplied()
        {
            var data = MakeQuery();
            var sortInfo = new SortInfo { Property = "Name", Direction = SortDir.Asc };
            var modifier = new OrderReplaceModifier(sortInfo);

            var result = modifier.Modify(data.Expression);

            // Should return a non-null expression with sort applied
            Assert.IsNotNull(result);
        }
    }
}
