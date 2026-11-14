using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    public class DpParent : BasePoco
    {
        public string Name { get; set; }
        public List<DpChild> Children { get; set; }
    }

    public class DpChild : PersistPoco, ITenant
    {
        public string Name { get; set; }
        public Guid? ParentId { get; set; }
        public DpParent Parent { get; set; }
        public string TenantCode { get; set; }
    }

    /// <summary>A projection type derived from the entity, not mapped to a table.</summary>
    public class DpChildView : DpChild
    {
        [NotMapped]
        public string ParentName_view { get; set; }
    }

    /// <summary>A projection type derived from the parent entity, not mapped to a table.</summary>
    public class DpParentView : DpParent
    {
    }

    public class DpContext : FrameworkContext
    {
        public DpContext(string cs, DBTypeEnum type) : base(cs, type) { }
        public DbSet<DpParent> DpParents { get; set; }
        public DbSet<DpChild> DpChildren { get; set; }
    }

    /// <summary>
    /// The data privilege conditions are kept when a list query is narrowed to selected records (batch, checked export, ReplaceWhere),
    /// while the search conditions are still replaced. Runs on SQLite; also on PostgreSQL when the environment variable
    /// WTM_TEST_PGSQL holds a connection string without a database name.
    /// Likewise on SQL Server when WTM_TEST_MSSQL holds a connection string without a database name (opt-in, off by default).
    /// </summary>
    [TestClass]
    public class DataPrivilegeBatchQueryTest
    {
        private enum User { LimitedToParentA, AllAccess, NoLogin, LimitedToChild1, AllAccessEverywhere }

        private static readonly string[] Everyone = { "m1", "m2", "m3", "m4invalid", "m5tenantX" };

        // ---- environment -----------------------------------------------------------------------------------------------------------------

        private sealed class Env : IDisposable
        {
            public readonly DBTypeEnum Type;
            public readonly string Cs;
            public readonly string ProviderName;
            private readonly SqliteConnection _keepAlive;
            public readonly Guid ParentA = Guid.NewGuid();
            public readonly Guid ParentB = Guid.NewGuid();
            public readonly Dictionary<string, Guid> Child = new Dictionary<string, Guid>();

            public Env(string provider)
            {
                ProviderName = provider;
                var name = "dp" + Guid.NewGuid().ToString("N");
                if (provider == "sqlite")
                {
                    Type = DBTypeEnum.SQLite;
                    Cs = "Data Source=" + name + ";Mode=Memory;Cache=Shared";
                    _keepAlive = new SqliteConnection(Cs);
                    _keepAlive.Open();
                }
                else if (provider == "mssql")
                {
                    Type = DBTypeEnum.SqlServer;
                    Cs = Environment.GetEnvironmentVariable("WTM_TEST_MSSQL").TrimEnd(';') + ";Initial Catalog=" + name + ";TrustServerCertificate=True";
                }
                else
                {
                    Type = DBTypeEnum.PgSql;
                    Cs = Environment.GetEnvironmentVariable("WTM_TEST_PGSQL").TrimEnd(';') + ";Database=" + name;
                }
                using (var dc = new DpContext(Cs, Type))
                {
                    CreateOwnTables(dc);
                    dc.Set<DpParent>().AddRange(new DpParent { ID = ParentA, Name = "A" }, new DpParent { ID = ParentB, Name = "B" });
                    var rows = new[]
                    {
                        ("m1", ParentA, true, (string)null), ("m2", ParentA, true, null), ("m3", ParentB, true, null),
                        ("m4invalid", ParentB, false, null), ("m5tenantX", ParentA, true, "X"),
                    };
                    foreach (var (n, p, valid, tenant) in rows)
                    {
                        var c = new DpChild { ID = Guid.NewGuid(), Name = n, ParentId = p, IsValid = valid, TenantCode = tenant };
                        Child[n] = c.ID;
                        dc.Set<DpChild>().Add(c);
                    }
                    dc.SaveChanges();
                }
            }

            // the model holds every entity of the test assembly (some cannot be created together on SQLite); only the two tables of this test are created
            private static void CreateOwnTables(DpContext dc)
            {
                if (dc.Database.ProviderName.Contains("Npgsql") || dc.Database.ProviderName.Contains("SqlServer"))
                {
                    Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(dc).Create();
                }
                var script = dc.Database.GenerateCreateScript();
                foreach (var statement in System.Text.RegularExpressions.Regex.Split(script, @";\r?\n|(?m)^GO\r?$"))
                {
                    if (statement.Contains("\"DpParents\"") || statement.Contains("\"DpChildren\"") || statement.Contains("[DpParents]") || statement.Contains("[DpChildren]"))
                    {
                        dc.Database.ExecuteSqlRaw(statement.Trim());
                    }
                }
            }

            public List<string> AllChildIds() => Everyone.Select(x => Child[x].ToString()).ToList();
            public List<string> AllParentIds() => new List<string> { ParentA.ToString(), ParentB.ToString() };

            public WTMContext Ctx(User user, bool? configKeep = null, DataPrivilegeOptions options = null, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory = null, IDataContext dc = null, List<IDataPrivilege> settingsOverride = null, bool nullSettings = false)
            {
                var settings = settingsOverride ?? new List<IDataPrivilege>
                {
                    new DataPrivilegeInfo<DpParent>("parent", x => x.Name),
                    new DataPrivilegeInfo<DpChild>("child", x => x.Name),
                };
                if (nullSettings)
                {
                    settings = null;
                }
                Microsoft.Extensions.Options.IOptionsMonitor<Configs> monitor = null;
                if (configKeep != null || options != null)
                {
                    var m = new Mock<IOptionsMonitor<Configs>>();
                    var o = options ?? new DataPrivilegeOptions { KeepInBatchQuery = configKeep.Value };
                    m.Setup(x => x.CurrentValue).Returns(new Configs { DataPrivilegeOptions = o });
                    monitor = m.Object;
                }
                var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
                var ctx = new WTMContext(monitor, null, null, null, settings, dc ?? new DpContext(Cs, Type), loggerFactory: loggerFactory, cache: cache);
                switch (user)
                {
                    case User.LimitedToParentA:
                        ctx.LoginUserInfo = new LoginUserInfo { ITCode = "u", DataPrivileges = new List<SimpleDataPri> { new SimpleDataPri { TableName = "DpParent", RelateId = ParentA.ToString() } } };
                        break;
                    case User.AllAccess:
                        ctx.LoginUserInfo = new LoginUserInfo { ITCode = "u", DataPrivileges = new List<SimpleDataPri> { new SimpleDataPri { TableName = "DpParent", RelateId = null } } };
                        break;
                    case User.AllAccessEverywhere:
                        ctx.LoginUserInfo = new LoginUserInfo { ITCode = "u", DataPrivileges = new List<SimpleDataPri> { new SimpleDataPri { TableName = "DpParent", RelateId = null }, new SimpleDataPri { TableName = "DpChild", RelateId = null } } };
                        break;
                    case User.LimitedToChild1:
                        ctx.LoginUserInfo = new LoginUserInfo { ITCode = "u", DataPrivileges = new List<SimpleDataPri> { new SimpleDataPri { TableName = "DpChild", RelateId = Child["m1"].ToString() } } };
                        break;
                }
                return ctx;
            }

            public void Dispose()
            {
                if (_keepAlive != null)
                {
                    _keepAlive.Dispose();
                }
                else
                {
                    using var dc = new DpContext(Cs, Type);
                    dc.Database.EnsureDeleted();
                }
            }
        }

        private static IEnumerable<string> Providers()
        {
            yield return "sqlite";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WTM_TEST_PGSQL")))
            {
                yield return "pgsql";
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WTM_TEST_MSSQL")))
            {
                yield return "mssql";
            }
        }

        private static void Each(Action<Env> body)
        {
            foreach (var p in Providers())
            {
                using var env = new Env(p);
                try { body(env); }
                catch (Exception e) when (!(e is AssertFailedException)) { throw new Exception("provider " + p + ": " + e.Message, e); }
            }
        }

        private static async Task EachAsync(Func<Env, Task> body)
        {
            foreach (var p in Providers())
            {
                using var env = new Env(p);
                await body(env);
            }
        }

        // ---- view models -----------------------------------------------------------------------------------------------------------------

        private class DpListVM<T> : BasePagedListVM<T, BaseSearcher> where T : TopBasePoco, new()
        {
            public Func<DpListVM<T>, IQueryable<T>> Q;
            public bool? KeepOverride;

            protected override bool KeepDataPrivilegeInBatchQuery => KeepOverride ?? base.KeepDataPrivilegeInBatchQuery;

            public override IOrderedQueryable<T> GetSearchQuery() => (IOrderedQueryable<T>)Q(this);
        }

        private class DpChildExportVM : DpListVM<DpChild>
        {
            protected override IEnumerable<IGridColumn<DpChild>> InitGridHeader()
            {
                return new List<GridColumn<DpChild>> { this.MakeGridHeader(x => x.Name) };
            }
        }

        private static DpListVM<T> Vm<T>(WTMContext ctx, ListVMSearchModeEnum mode, Func<DpListVM<T>, IQueryable<T>> q, List<string> ids, Action<DpListVM<T>> cfg = null) where T : TopBasePoco, new()
        {
            var vm = new DpListVM<T> { Wtm = ctx, Q = q, Ids = ids, SearcherMode = mode, NeedPage = false, Searcher = new BaseSearcher() };
            cfg?.Invoke(vm);
            return vm;
        }

        // search condition "name is m1", then the data privilege condition on the parent
        private static IQueryable<DpChild> SearchThenPrivilege(DpListVM<DpChild> vm)
            => vm.DC.Set<DpChild>().Where(x => x.Name == "m1").DPWhere(vm.Wtm, x => x.ParentId).OrderBy(x => x.Name);

        private static string Names(IEnumerable<DpChild> rows) => string.Join(",", rows.Select(x => x.Name).OrderBy(x => x));
        private static string Names(IEnumerable<DpParent> rows) => string.Join(",", rows.Select(x => x.Name).OrderBy(x => x));

        private static string Run(Env env, User user, ListVMSearchModeEnum mode, bool? config = null, Action<DpListVM<DpChild>> cfg = null, List<string> ids = null)
        {
            var vm = Vm<DpChild>(env.Ctx(user, config), mode, SearchThenPrivilege, ids ?? env.AllChildIds(), cfg);
            vm.DoSearch();
            return Names(vm.EntityList);
        }

        // ---- the fix: a record outside the privilege cannot be reached by id ------------------------------------------------------------------

        [TestMethod]
        public void Batch_UserLimitedToOneParent_DoesNotReturnAnotherParentsRow()
        {
            Each(env =>
            {
                Assert.AreEqual("m1", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Search), "search mode: the search condition applies");
                Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch), "m3 belongs to the other parent");
                var only = new List<string> { env.Child["m3"].ToString() };
                Assert.AreEqual("", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, ids: only));
            });
        }

        [TestMethod]
        public void CheckExport_UserLimitedToOneParent_DoesNotReturnAnotherParentsRow()
        {
            Each(env =>
            {
                Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.CheckExport));
                var only = new List<string> { env.Child["m3"].ToString() };
                Assert.AreEqual("", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.CheckExport, ids: only));
            });
        }

        [TestMethod]
        public async Task BatchAndCheckExport_Async_KeepThePrivilege()
        {
            await EachAsync(async env =>
            {
                foreach (var mode in new[] { ListVMSearchModeEnum.Batch, ListVMSearchModeEnum.CheckExport })
                {
                    var vm = Vm<DpChild>(env.Ctx(User.LimitedToParentA), mode, SearchThenPrivilege, env.AllChildIds());
                    await vm.DoSearchAsync();
                    Assert.AreEqual("m1,m2", Names(vm.EntityList), mode.ToString());
                }
            });
        }

        [TestMethod]
        public void SearchConditionsAreStillReplaced_TickedRowShowsAfterTheSearchChanged()
        {
            Each(env =>
            {
                // the search form now says "m1", the ticked row is m2
                var ticked = new List<string> { env.Child["m2"].ToString() };
                Assert.AreEqual("m1", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Search));
                Assert.AreEqual("m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, ids: ticked));
                Assert.AreEqual("m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.CheckExport, ids: ticked));
            });
        }

        [TestMethod]
        public void AllAccessUser_GetsEveryTickedRow_NoLoginUser_GetsNone()
        {
            Each(env =>
            {
                foreach (var mode in new[] { ListVMSearchModeEnum.Batch, ListVMSearchModeEnum.CheckExport })
                {
                    Assert.AreEqual("m1,m2,m3", Run(env, User.AllAccess, mode), "all access " + mode);
                    Assert.AreEqual("", Run(env, User.NoLogin, mode), "no login " + mode);
                }
            });
        }

        [TestMethod]
        public void GlobalFilters_AreKept_WithTheSwitchOnAndOff()
        {
            Each(env =>
            {
                foreach (var keep in new[] { true, false })
                {
                    foreach (var mode in new[] { ListVMSearchModeEnum.Batch, ListVMSearchModeEnum.CheckExport })
                    {
                        var rows = Run(env, User.AllAccess, mode, keep);
                        Assert.AreEqual("m1,m2,m3", rows, "IsValid=false and the other tenant never appear; keep=" + keep + " " + mode);
                    }
                }
            });
        }

        [TestMethod]
        public void GlobalFilter_OfTheCurrentTenant_IsKept()
        {
            Each(env =>
            {
                var ctx = env.Ctx(User.AllAccess);
                ((FrameworkContext)ctx.DC).SetTenantCode("X");
                var vm = Vm<DpChild>(ctx, ListVMSearchModeEnum.Batch, SearchThenPrivilege, env.AllChildIds());
                vm.DoSearch();
                Assert.AreEqual("m5tenantX", Names(vm.EntityList));
            });
        }

        // ---- the switch -----------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Switch_Off_RestoresTheEarlierBatchBehavior()
        {
            Each(env =>
            {
                Assert.AreEqual("m1,m2,m3", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, false));
                Assert.AreEqual("m1,m2,m3", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.CheckExport, false));
                Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, true));
            });
        }

        [TestMethod]
        public void Switch_NotConfigured_DefaultsToOn()
        {
            Each(env => Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, null)));
        }

        [TestMethod]
        public void PerViewModelOverride_WinsOverTheConfiguration()
        {
            Each(env =>
            {
                Assert.AreEqual("m1,m2,m3", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, true, v => v.KeepOverride = false));
                Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, false, v => v.KeepOverride = true));
            });
        }

        [TestMethod]
        public void Options_Binding_AndDefaults()
        {
            Assert.IsTrue(new Configs().DataPrivilegeOptions.KeepInBatchQuery);
            var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build().Get<Configs>();
            Assert.IsTrue(empty == null || empty.DataPrivilegeOptions.KeepInBatchQuery);
            var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataPrivilegeOptions:KeepInBatchQuery"] = "false" }).Build().Get<Configs>();
            Assert.IsFalse(off.DataPrivilegeOptions.KeepInBatchQuery);
            var on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataPrivilegeOptions:KeepInBatchQuery"] = "true" }).Build().Get<Configs>();
            Assert.IsTrue(on.DataPrivilegeOptions.KeepInBatchQuery);
            var other = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["CookiePre"] = "x" }).Build().Get<Configs>();
            Assert.IsTrue(other.DataPrivilegeOptions.KeepInBatchQuery, "an unrelated configuration leaves the default");
        }

        [TestMethod]
        public void Options_Binding_RejectsInvalidValues()
        {
            foreach (var value in new[] { "0", "1", "", "abc" })
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataPrivilegeOptions:KeepInBatchQuery"] = value }).Build();
                Assert.ThrowsException<InvalidOperationException>(() => config.Get<Configs>(), $"value '{value}' must be rejected");
            }
        }

        // ---- other ways to narrow the query ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void SelectorValueField_NotId_KeepsThePrivilege()
        {
            Each(env =>
            {
                var names = new List<string> { "m1", "m2", "m3" };
                Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, true, v => v.SelectorValueField = "Name", names));
                Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.CheckExport, true, v => v.SelectorValueField = "Name", names));
                Assert.AreEqual("m1,m2,m3", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Batch, false, v => v.SelectorValueField = "Name", names));
            });
        }

        [TestMethod]
        public void ReplaceWhere_KeepsThePrivilege_AndReplacesTheSearch()
        {
            Each(env =>
            {
                Expression<Func<DpChild, bool>> replace = y => y.Name != "none";
                Assert.AreEqual("m1,m2", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Search, true, v => v.ReplaceWhere = replace));
                Assert.AreEqual("m1,m2,m3", Run(env, User.LimitedToParentA, ListVMSearchModeEnum.Search, false, v => v.ReplaceWhere = replace));
                Assert.AreEqual("m1,m2,m3", Run(env, User.AllAccess, ListVMSearchModeEnum.Search, true, v => v.ReplaceWhere = replace));
            });
        }

        [TestMethod]
        public void ListNavigationPrivilege_IsKept()
        {
            Each(env =>
            {
                Func<DpListVM<DpParent>, IQueryable<DpParent>> q = vm => vm.DC.Set<DpParent>().Where(x => x.Name != "zzz").DPWhere(vm.Wtm, x => x.Children[0].ID).OrderBy(x => x.Name);
                foreach (var (keep, expected) in new[] { (true, "A"), (false, "A,B") })
                {
                    var vm = Vm<DpParent>(env.Ctx(User.LimitedToChild1, keep), ListVMSearchModeEnum.Batch, q, env.AllParentIds());
                    vm.DoSearch();
                    Assert.AreEqual(expected, Names(vm.EntityList), "keep=" + keep);
                }
            });
        }

        // ---- a hand-written Where after a Select to a view type -------------------------------------------------------------------------

        private static IQueryable<DpChildView> HandWrittenAfterView(DpListVM<DpChildView> vm)
        {
            var allowed = vm.DC.Set<DpChild>().DPWhere(vm.Wtm, x => x.ParentId).Select(x => x.ID).ToList();
            return vm.DC.Set<DpChild>().Where(x => x.Name == "m1")
                .Select(x => new DpChildView { ID = x.ID, Name = x.Name, ParentId = x.ParentId })
                .Where(x => allowed.Contains(x.ID))
                .OrderBy(x => x.Name);
        }

        [TestMethod]
        public async Task HandWrittenWhereAfterSelectToView_IsUnchanged_InBatchAndCheckExport_OnAndOff()
        {
            await EachAsync(async env =>
            {
                foreach (var keep in new[] { true, false })
                {
                    foreach (var mode in new[] { ListVMSearchModeEnum.Batch, ListVMSearchModeEnum.CheckExport })
                    {
                        var sync = Vm<DpChildView>(env.Ctx(User.LimitedToParentA, keep), mode, HandWrittenAfterView, env.AllChildIds());
                        sync.DoSearch();
                        Assert.AreEqual("m1,m2", Names(sync.EntityList), $"sync keep={keep} {mode}");
                        var async = Vm<DpChildView>(env.Ctx(User.LimitedToParentA, keep), mode, HandWrittenAfterView, env.AllChildIds());
                        await async.DoSearchAsync();
                        Assert.AreEqual("m1,m2", Names(async.EntityList), $"async keep={keep} {mode}");
                    }
                }
                // the generated SQL is the same with the switch on and off (the stopping behavior of the modifier is not changed)
                var on = Vm<DpChildView>(env.Ctx(User.LimitedToParentA, true), ListVMSearchModeEnum.Batch, HandWrittenAfterView, env.AllChildIds()).GetBatchQuery().ToQueryString();
                var off = Vm<DpChildView>(env.Ctx(User.LimitedToParentA, false), ListVMSearchModeEnum.Batch, HandWrittenAfterView, env.AllChildIds()).GetBatchQuery().ToQueryString();
                Assert.AreEqual(Normalize(off), Normalize(on));
            });
        }

        [TestMethod]
        public void WhereProtectedAfterSelectToView_StaysInPlace_InBatch()
        {
            Each(env =>
            {
                Func<DpListVM<DpChildView>, IQueryable<DpChildView>> q = vm => vm.DC.Set<DpChild>().Where(x => x.Name == "m1")
                    .Select(x => new DpChildView { ID = x.ID, Name = x.Name, ParentId = x.ParentId })
                    .WhereProtected(x => x.Name != "m2").OrderBy(x => x.Name);
                foreach (var keep in new[] { true, false })
                {
                    var vm = Vm<DpChildView>(env.Ctx(User.AllAccess, keep), ListVMSearchModeEnum.Batch, q, env.AllChildIds());
                    vm.DoSearch();
                    Assert.AreEqual("m1,m3", Names(vm.EntityList), "keep=" + keep);
                }
            });
        }

        // ---- shapes of the query ------------------------------------------------------------------------------------------------------------

        private static string Normalize(string sql) => System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ");

        private static string WhereOnly(IQueryable q) { var s = Normalize(q.ToQueryString()); var i = s.IndexOf(" FROM ", StringComparison.Ordinal); return i < 0 ? s : s.Substring(i); }

        private static IQueryable<T> Narrow<T>(IQueryable<T> q, List<string> ids, bool? keep) where T : TopBasePoco
        {
            var mod = keep == null ? new WhereReplaceModifier<T>(ids.GetContainIdExpression<T>()) : new WhereReplaceModifier<T>(ids.GetContainIdExpression<T>(), keep.Value);
            return q.Provider.CreateQuery<T>(mod.Modify(q.Expression));
        }

        private class DropNames : ExpressionVisitor
        {
            protected override Expression VisitLambda<D>(Expression<D> node) => Expression.Lambda<D>(Visit(node.Body), node.Parameters);
        }

        // S = search condition, D = DPWhere. The privilege is parent A; S hides m1 and S2 hides m2. All five children are ticked.
        private static readonly (string Name, Func<WTMContext, bool, IQueryable<DpChild>> Make, string On, string Off)[] Shapes =
        {
            ("S.D.Select.Order", (c, p) => D(c.DC.Set<DpChild>().Where(S), c).Select(x => new DpChild { ID = x.ID, Name = x.Name }).OrderBy(x => x.Name), "m1,m2", "m1,m2,m3"),
            ("D.S.S2.Order", (c, p) => D(c.DC.Set<DpChild>(), c).Where(S).Where(S2).OrderBy(x => x.Name), "m1,m2", "m1,m2,m3"),
            ("S.D.D.Order", (c, p) => D(D(c.DC.Set<DpChild>().Where(S), c), c).OrderBy(x => x.Name), "m1,m2", "m1,m2,m3"),
            ("D.Include.S.Order", (c, p) => D(c.DC.Set<DpChild>(), c).Include(x => x.Parent).Where(S).OrderBy(x => x.Name), "m1,m2", "m1,m2"),
            ("S.Include.D.Order", (c, p) => D(c.DC.Set<DpChild>().Where(S).Include(x => x.Parent), c).OrderBy(x => x.Name), "m2", "m2,m3"),
            ("S.AsNoTracking.D.Order", (c, p) => D(c.DC.Set<DpChild>().Where(S).AsNoTracking(), c).OrderBy(x => x.Name), "m2", "m2,m3"),
            ("S.Include.S2.Include.D.Order", (c, p) => D(c.DC.Set<DpChild>().Where(S).Include(x => x.Parent).Where(S2).Include(x => x.Parent), c).OrderBy(x => x.Name), "", "m3"),
            ("S.Select(same T).D.Order", (c, p) => D(c.DC.Set<DpChild>().Where(S).Select(x => new DpChild { ID = x.ID, Name = x.Name, ParentId = x.ParentId }), c).OrderBy(x => x.Name), "m2", "m2,m3"),
            ("D.Order.S", (c, p) => D(c.DC.Set<DpChild>(), c).OrderBy(x => x.Name).Where(S), "m2", "m2,m3"),
            ("D.S.D.Order (marked conditions interleaved with a search condition)", (c, p) => D(D(c.DC.Set<DpChild>(), c).Where(S), c).OrderBy(x => x.Name), "m1,m2", "m1,m2,m3"),
            ("S.D", (c, p) => D(c.DC.Set<DpChild>().Where(S), c), "m1,m2", "m1,m2"),
            ("D.S", (c, p) => D(c.DC.Set<DpChild>(), c).Where(S), "m2", "m2,m3"),
        };

        private static readonly Expression<Func<DpChild, bool>> S = x => x.Name != "m1";
        private static readonly Expression<Func<DpChild, bool>> S2 = x => x.Name != "m2";
        private static IQueryable<DpChild> D(IQueryable<DpChild> q, WTMContext c) => q.DPWhere(c, x => x.ParentId);

        [TestMethod]
        public void Shapes_KeepOn_KeepsTheMarkedConditions_KeepOff_IsTheEarlierBehavior()
        {
            Each(env =>
            {
                var ids = env.AllChildIds();
                foreach (var shape in Shapes)
                {
                    var c = env.Ctx(User.LimitedToParentA);
                    var q = shape.Make(c, true);
                    Assert.AreEqual(shape.On, Names(Narrow(q, ids, true).ToList()), shape.Name + " keep on");
                    Assert.AreEqual(shape.Off, Names(Narrow(q, ids, false).ToList()), shape.Name + " keep off");
                    Assert.AreEqual(shape.Off, Names(Narrow(q, ids, null).ToList()), shape.Name + " existing constructor");
                }
            });
        }

        [TestMethod]
        public void Shapes_KeepOff_GeneratesTheSameSqlAsTheExistingConstructor_AndAsUnmarkedConditions()
        {
            Each(env =>
            {
                var ids = env.AllChildIds();
                foreach (var shape in Shapes)
                {
                    var c = env.Ctx(User.LimitedToParentA);
                    var q = shape.Make(c, true);
                    var unmarked = q.Provider.CreateQuery<DpChild>(new DropNames().Visit(q.Expression));
                    var old = Normalize(Narrow(q, ids, null).ToQueryString());
                    Assert.AreEqual(old, Normalize(Narrow(q, ids, false).ToQueryString()), shape.Name + ": keep=false vs existing constructor");
                    Assert.AreEqual(old, Normalize(Narrow(unmarked, ids, null).ToQueryString()), shape.Name + ": same as a query built without marks");
                    Assert.AreEqual(Normalize(unmarked.ToQueryString()), Normalize(q.ToQueryString()), shape.Name + ": the mark does not change the SQL of the query itself");
                }
            });
        }

        [TestMethod]
        public void Mark_IsOnTheLambdaName_AndNoConditionIsAddedWithoutAPrivilegeSetting()
        {
            Each(env =>
            {
                var c = env.Ctx(User.LimitedToParentA);
                var q = c.DC.Set<DpChild>().DPWhere(c, x => x.ParentId);
                var lambda = (LambdaExpression)((UnaryExpression)((MethodCallExpression)q.Expression).Arguments[1]).Operand;
                Assert.AreEqual(ProtectedCondition.LambdaName, lambda.Name);
                var plain = c.DC.Set<DpChild>();
                Assert.AreSame(plain, plain.DPWhere(c, x => x.Name), "an unrelated field adds nothing");
            });
        }

        [TestMethod]
        public void WhereProtected_KeepsTheCondition_AndTheCapturedValueStaysAParameter()
        {
            Each(env =>
            {
                var secret = "m2";
                var c = env.Ctx(User.AllAccess);
                var q = c.DC.Set<DpChild>().WhereProtected(x => x.Name != secret).Where(S).OrderBy(x => x.Name);
                var ids = env.AllChildIds();
                Assert.AreEqual("m1,m3", Names(Narrow(q, ids, true).ToList()));
                Assert.AreEqual("m1,m2,m3", Names(Narrow(q, ids, false).ToList()));
                var where = WhereOnly(Narrow(q, ids, true));
                Assert.IsFalse(where.Contains("'m2'"), "the value is not written into the SQL: " + where);
                Assert.IsTrue(where.Contains("@secret"), where);
                var plain = c.DC.Set<DpChild>().Where(x => x.Name != secret).OrderBy(x => x.Name);
                Assert.AreEqual(Normalize(plain.ToQueryString()), Normalize(c.DC.Set<DpChild>().WhereProtected(x => x.Name != secret).OrderBy(x => x.Name).ToQueryString()));
            });
        }

        [TestMethod]
        public void WhereProtected_RejectsNull()
        {
            Each(env =>
            {
                Assert.ThrowsExactly<ArgumentNullException>(() => ((IQueryable<DpChild>)null).WhereProtected(x => true));
                Assert.ThrowsExactly<ArgumentNullException>(() => env.Ctx(User.AllAccess).DC.Set<DpChild>().WhereProtected(null));
            });
        }

        // ---- limits of the mark -------------------------------------------------------------------------------------------------------------

        private class OverriddenBatchVM : DpListVM<DpChild>
        {
            public bool PrivilegeInOverride;

            public override IOrderedQueryable<DpChild> GetBatchQuery()
            {
                IQueryable<DpChild> q = DC.Set<DpChild>().Where(Ids.GetContainIdExpression<DpChild>());
                if (PrivilegeInOverride)
                {
                    q = q.DPWhere(Wtm, x => x.ParentId);
                }
                return q.OrderBy(x => x.Name);
            }
        }

        [TestMethod]
        public void OverriddenGetBatchQuery_IsUsedAsItIs_InBatchAndCheckExport()
        {
            Each(env =>
            {
                foreach (var keep in new[] { true, false })
                {
                    foreach (var mode in new[] { ListVMSearchModeEnum.Batch, ListVMSearchModeEnum.CheckExport })
                    {
                        // the override owns the query: the framework neither adds nor removes a privilege condition
                        var bare = new OverriddenBatchVM { Wtm = env.Ctx(User.LimitedToParentA, keep), Ids = env.AllChildIds(), SearcherMode = mode, NeedPage = false, Searcher = new BaseSearcher() };
                        bare.DoSearch();
                        Assert.AreEqual("m1,m2,m3", Names(bare.EntityList), $"override without a privilege condition, keep={keep} {mode}");

                        // an override that adds the privilege itself keeps it, with the switch on or off
                        var guarded = new OverriddenBatchVM { PrivilegeInOverride = true, Wtm = env.Ctx(User.LimitedToParentA, keep), Ids = env.AllChildIds(), SearcherMode = mode, NeedPage = false, Searcher = new BaseSearcher() };
                        guarded.DoSearch();
                        Assert.AreEqual("m1,m2", Names(guarded.EntityList), $"override with its own privilege condition, keep={keep} {mode}");
                    }
                }
            });
        }

        [TestMethod]
        public void UnmarkedHandWrittenCondition_IsStillReplaced_TheLimitOfTheMark()
        {
            Each(env =>
            {
                var parentA = env.ParentA;
                // a hand-written authorization condition has no mark, so it is replaced like a search condition (documented in FORK.md)
                Func<DpListVM<DpChild>, IQueryable<DpChild>> handWritten = vm => vm.DC.Set<DpChild>().Where(x => x.ParentId == parentA).OrderBy(x => x.Name);
                foreach (var mode in new[] { ListVMSearchModeEnum.Batch, ListVMSearchModeEnum.CheckExport })
                {
                    var vm = Vm<DpChild>(env.Ctx(User.LimitedToParentA, true), mode, handWritten, env.AllChildIds());
                    vm.DoSearch();
                    Assert.AreEqual("m1,m2,m3", Names(vm.EntityList), "unmarked condition is replaced, keep=true " + mode);
                }

                // the same condition added with WhereProtected is kept
                Func<DpListVM<DpChild>, IQueryable<DpChild>> marked = vm => vm.DC.Set<DpChild>().WhereProtected(x => x.ParentId == parentA).OrderBy(x => x.Name);
                var kept = Vm<DpChild>(env.Ctx(User.LimitedToParentA, true), ListVMSearchModeEnum.Batch, marked, env.AllChildIds());
                kept.DoSearch();
                Assert.AreEqual("m1,m2", Names(kept.EntityList));

                // a privilege condition whose mark was lost by rebuilding the expression is replaced as well
                Func<DpListVM<DpChild>, IQueryable<DpChild>> rebuilt = vm =>
                {
                    var q = vm.DC.Set<DpChild>().DPWhere(vm.Wtm, x => x.ParentId).OrderBy(x => x.Name);
                    return (IOrderedQueryable<DpChild>)q.Provider.CreateQuery<DpChild>(new DropNames().Visit(q.Expression));
                };
                var lost = Vm<DpChild>(env.Ctx(User.LimitedToParentA, true), ListVMSearchModeEnum.Batch, rebuilt, env.AllChildIds());
                lost.DoSearch();
                Assert.AreEqual("m1,m2,m3", Names(lost.EntityList), "mark lost by an expression visitor");
            });
        }

        // ---- DPWhere on a projected view type -------------------------------------------------------------------------------------------

        /// <summary>A view type that is not derived from an entity: nothing ties it to a table.</summary>
        public class DpFlatView
        {
            public Guid ID { get; set; }
            public string Name { get; set; }
            public Guid? ParentId { get; set; }
        }

        /// <summary>A view type of its own for each scenario: the unresolved-type warning is logged once per process for each (type, field, mode), so scenarios use distinct closed types.</summary>
        public class DpFlatView<TTag> : DpFlatView { }
        public sealed class TagLegacy { }
        public sealed class TagAudit { }
        public sealed class TagAuditDefault { }
        public sealed class TagEnforce { }
        public sealed class TagPartial { }
        public sealed class TagRepeat { }
        public sealed class TagList { }
        public sealed class TagMulti { }

        // the warning is logged once per process for each (type, field, mode): only the first provider of a scenario sees it
        private static int Once(Env env, int n) => env.ProviderName == "sqlite" ? n : 0;

        private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
        {
            public readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Category, string Message)> Entries = new List<(Microsoft.Extensions.Logging.LogLevel, string, string)>();
            public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new L(this, categoryName);
            public void Dispose() { }
            private sealed class L : Microsoft.Extensions.Logging.ILogger
            {
                private readonly CapturingLoggerFactory _f; private readonly string _c;
                public L(CapturingLoggerFactory f, string c) { _f = f; _c = c; }
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                    => _f.Entries.Add((logLevel, _c, formatter(state, exception)));
            }
        }

        private static DataPrivilegeOptions Opts(bool resolve = true, DataPrivilegeUnresolvedTypeMode mode = DataPrivilegeUnresolvedTypeMode.Audit)
            => new DataPrivilegeOptions { ResolveProjectedTypes = resolve, UnresolvedTypeMode = mode };

        private static IQueryable<DpChildView> ToView(IQueryable<DpChild> q)
            => q.Select(x => new DpChildView { ID = x.ID, Name = x.Name, ParentId = x.ParentId });

        private static IQueryable<DpFlatView> ToFlat(IQueryable<DpChild> q)
            => q.Select(x => new DpFlatView { ID = x.ID, Name = x.Name, ParentId = x.ParentId });

        private static IQueryable<DpFlatView<TTag>> ToFlat<TTag>(IQueryable<DpChild> q)
            => q.Select(x => new DpFlatView<TTag> { ID = x.ID, Name = x.Name, ParentId = x.ParentId });

        private static string Rows(IEnumerable<DpChildView> rows) => string.Join(",", rows.Select(x => x.Name).OrderBy(x => x));
        private static string Rows(IEnumerable<DpFlatView> rows) => string.Join(",", rows.Select(x => x.Name).OrderBy(x => x));

        [TestMethod]
        public void ProjectedView_IdField_LimitedUserGetsOnlyAllowedRows()
        {
            Each(env =>
            {
                var c = env.Ctx(User.LimitedToChild1, options: Opts());
                Assert.AreEqual("m1", Names(c.DC.Set<DpChild>().DPWhere(c, x => x.ID).ToList()), "on the entity");
                Assert.AreEqual("m1", Rows(ToView(c.DC.Set<DpChild>()).DPWhere(c, x => x.ID).ToList()), "on the projected view");
                var all = env.Ctx(User.AllAccessEverywhere, options: Opts());
                Assert.AreEqual("m1,m2,m3", Rows(ToView(all.DC.Set<DpChild>()).DPWhere(all, x => x.ID).ToList()));
                var none = env.Ctx(User.NoLogin, options: Opts());
                Assert.AreEqual("", Rows(ToView(none.DC.Set<DpChild>()).DPWhere(none, x => x.ID).ToList()), "no login: no rows, as on the entity");
                Assert.AreEqual("", Names(none.DC.Set<DpChild>().DPWhere(none, x => x.ID).ToList()));
            });
        }

        [TestMethod]
        public void ProjectedView_FkField_LimitedAllAccessAndNoLogin()
        {
            Each(env =>
            {
                foreach (var (user, expected) in new[] { (User.LimitedToParentA, "m1,m2"), (User.AllAccess, "m1,m2,m3"), (User.NoLogin, "") })
                {
                    var c = env.Ctx(user, options: Opts());
                    Assert.AreEqual(expected, Names(c.DC.Set<DpChild>().DPWhere(c, x => x.ParentId).ToList()), "entity " + user);
                    Assert.AreEqual(expected, Rows(ToView(c.DC.Set<DpChild>()).DPWhere(c, x => x.ParentId).ToList()), "view " + user);
                    // the default configuration (no options at all) behaves the same
                    var d = env.Ctx(user);
                    Assert.AreEqual(expected, Rows(ToView(d.DC.Set<DpChild>()).DPWhere(d, x => x.ParentId).ToList()), "view, defaults " + user);
                }
            });
        }

        [TestMethod]
        public void ProjectedView_BeforeAndAfterProjection_SameRowsAndSql()
        {
            Each(env =>
            {
                foreach (var user in new[] { User.LimitedToParentA, User.AllAccess, User.NoLogin })
                {
                    var c = env.Ctx(user, options: Opts());
                    var before = ToView(c.DC.Set<DpChild>().Where(x => x.Name != "m3").DPWhere(c, x => x.ParentId)).OrderBy(x => x.Name);
                    var after = ToView(c.DC.Set<DpChild>().Where(x => x.Name != "m3")).DPWhere(c, x => x.ParentId).OrderBy(x => x.Name);
                    Assert.AreEqual(Rows(before.ToList()), Rows(after.ToList()), "rows " + user);
                    Assert.AreEqual(Normalize(before.ToQueryString()), Normalize(after.ToQueryString()), "sql " + user);
                }
            });
        }

        [TestMethod]
        public void ProjectedView_MarkSurvives_InBatchAndCheckExport_WhenKeepIsOn()
        {
            Each(env =>
            {
                Func<DpListVM<DpChildView>, IQueryable<DpChildView>> q = vm => ToView(vm.DC.Set<DpChild>()).DPWhere(vm.Wtm, x => x.ParentId).OrderBy(x => x.Name);
                var lambda = (LambdaExpression)((UnaryExpression)((MethodCallExpression)((MethodCallExpression)q(Vm<DpChildView>(env.Ctx(User.LimitedToParentA), ListVMSearchModeEnum.Search, q, null)).Expression).Arguments[0]).Arguments[1]).Operand;
                Assert.AreEqual(ProtectedCondition.LambdaName, lambda.Name, "the condition added on the view carries the mark");
                foreach (var mode in new[] { ListVMSearchModeEnum.Batch, ListVMSearchModeEnum.CheckExport })
                {
                    // the condition is added after the projection, where the narrowing of the query stops: it stays in place with the switch on and off
                    foreach (var keep in new[] { true, false })
                    {
                        var vm = Vm<DpChildView>(env.Ctx(User.LimitedToParentA, keep), mode, q, env.AllChildIds());
                        vm.DoSearch();
                        Assert.AreEqual("m1,m2", Rows(vm.EntityList), $"keep={keep} {mode}");
                    }
                    // a ticked row outside the privilege is not reachable by its id
                    var other = Vm<DpChildView>(env.Ctx(User.LimitedToParentA, true), mode, q, new List<string> { env.Child["m3"].ToString() });
                    other.DoSearch();
                    Assert.AreEqual("", Rows(other.EntityList), "m3 belongs to the other parent " + mode);
                }
            });
        }

        [TestMethod]
        public void ProjectedView_ResolveProjectedTypesOff_KeepsThePreviousUnfilteredBehavior()
        {
            Each(env =>
            {
                var log = new CapturingLoggerFactory();
                foreach (var user in new[] { User.LimitedToParentA, User.NoLogin })
                {
                    var c = env.Ctx(user, options: Opts(resolve: false, mode: DataPrivilegeUnresolvedTypeMode.Legacy), loggerFactory: log);
                    Assert.AreEqual("m1,m2,m3", Rows(ToView(c.DC.Set<DpChild>()).DPWhere(c, x => x.ParentId).ToList()), "fk " + user);
                    // the ID field with no login: the type name of the view is a table name the old code took as is, so no rows (as at the base commit)
                    Assert.AreEqual(user == User.NoLogin ? "" : "m1,m2,m3", Rows(ToView(c.DC.Set<DpChild>()).DPWhere(c, x => x.ID).ToList()), "id " + user);
                    // the same type of the entity is still filtered
                    Assert.AreEqual(user == User.NoLogin ? "" : "m1,m2", Names(c.DC.Set<DpChild>().DPWhere(c, x => x.ParentId).ToList()));
                }
                Assert.AreEqual(0, log.Entries.Count, "Legacy is silent");
                // with the default mode (Audit) the rows are the same and the skipped field is logged
                var a = env.Ctx(User.LimitedToParentA, options: Opts(resolve: false), loggerFactory: log);
                Assert.AreEqual("m1,m2,m3", Rows(ToView(a.DC.Set<DpChild>()).DPWhere(a, x => x.ParentId).ToList()));
                Assert.AreEqual(Once(env, 1), log.Entries.Count);
            });
        }

        [TestMethod]
        public void NonDerivedView_Legacy_Audit_Enforce()
        {
            Each(env =>
            {
                // Legacy: silent, all rows
                var legacyLog = new CapturingLoggerFactory();
                var l = env.Ctx(User.LimitedToParentA, options: Opts(mode: DataPrivilegeUnresolvedTypeMode.Legacy), loggerFactory: legacyLog);
                Assert.AreEqual("m1,m2,m3", Rows(ToFlat<TagLegacy>(l.DC.Set<DpChild>()).DPWhere(l, x => x.ParentId).ToList()));
                Assert.AreEqual(0, legacyLog.Entries.Count);

                // Audit (also the default without any option): a warning naming the type and the field, all rows
                // (a distinct view type for each, so each one logs)
                {
                    var log = new CapturingLoggerFactory();
                    var a = env.Ctx(User.LimitedToParentA, options: Opts(mode: DataPrivilegeUnresolvedTypeMode.Audit), loggerFactory: log);
                    var q = ToFlat<TagAudit>(a.DC.Set<DpChild>());
                    Assert.AreSame(q, q.DPWhere(a, x => x.ParentId), "the query is returned unchanged");
                    Assert.AreEqual("m1,m2,m3", Rows(q.DPWhere(a, x => x.ParentId).ToList()));
                    // logged once for the type, field and mode, although the field was handled twice
                    Assert.AreEqual(Once(env, 1), log.Entries.Count);
                    if (log.Entries.Count > 0)
                    {
                        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, log.Entries[0].Level);
                        StringAssert.Contains(log.Entries[0].Message, nameof(DpFlatView));
                        StringAssert.Contains(log.Entries[0].Message, "ParentId");
                        StringAssert.Contains(log.Entries[0].Message, "Audit");
                    }
                }
                {
                    // the options absent altogether: the default is Audit
                    var log = new CapturingLoggerFactory();
                    var a = env.Ctx(User.LimitedToParentA, options: null, loggerFactory: log);
                    var q = ToFlat<TagAuditDefault>(a.DC.Set<DpChild>());
                    Assert.AreSame(q, q.DPWhere(a, x => x.ParentId));
                    Assert.AreEqual("m1,m2,m3", Rows(q.DPWhere(a, x => x.ParentId).ToList()));
                    Assert.AreEqual(Once(env, 1), log.Entries.Count);
                }

                // Enforce: no rows, and a warning
                var elog = new CapturingLoggerFactory();
                var e = env.Ctx(User.LimitedToParentA, options: Opts(mode: DataPrivilegeUnresolvedTypeMode.Enforce), loggerFactory: elog);
                Assert.AreEqual("", Rows(ToFlat<TagEnforce>(e.DC.Set<DpChild>()).DPWhere(e, x => x.ParentId).ToList()));
                Assert.AreEqual("", Rows(ToFlat<TagEnforce>(e.DC.Set<DpChild>()).DPWhere(e, x => x.ID).ToList()));
                Assert.AreEqual(Once(env, 2), elog.Entries.Count);
                if (elog.Entries.Count > 0)
                {
                    StringAssert.Contains(elog.Entries[0].Message, "Enforce");
                }
                // the filtering does not depend on the log: the same call again logs nothing and still returns no rows
                var before = elog.Entries.Count;
                Assert.AreEqual("", Rows(ToFlat<TagEnforce>(e.DC.Set<DpChild>()).DPWhere(e, x => x.ParentId).ToList()));
                Assert.AreEqual(before, elog.Entries.Count, "once per type, field and mode");
                // no login: no rows in Enforce, unchanged and logged in Audit
                var en = env.Ctx(User.NoLogin, options: Opts(mode: DataPrivilegeUnresolvedTypeMode.Enforce), loggerFactory: elog);
                Assert.AreEqual("", Rows(ToFlat<TagEnforce>(en.DC.Set<DpChild>()).DPWhere(en, x => x.ParentId).ToList()));
                var an = env.Ctx(User.NoLogin, options: Opts(), loggerFactory: elog);
                Assert.AreEqual("m1,m2,m3", Rows(ToFlat<TagAudit>(an.DC.Set<DpChild>()).DPWhere(an, x => x.ParentId).ToList()));
            });
        }

        [TestMethod]
        public void NonDerivedView_UnrestrictedUser_IsNotAffected_AndNothingIsLogged()
        {
            Each(env =>
            {
                foreach (var mode in new[] { DataPrivilegeUnresolvedTypeMode.Audit, DataPrivilegeUnresolvedTypeMode.Enforce })
                {
                    var log = new CapturingLoggerFactory();
                    var c = env.Ctx(User.AllAccessEverywhere, options: Opts(mode: mode), loggerFactory: log);
                    Assert.AreEqual("m1,m2,m3", Rows(ToFlat<TagPartial>(c.DC.Set<DpChild>()).DPWhere(c, x => x.ParentId).ToList()), mode.ToString());
                    Assert.AreEqual(0, log.Entries.Count, mode + " logs nothing for a user who can read everything");
                    // a user who is unrestricted on one table only is restricted on the other, so the unknown table is still checked
                    var partial = env.Ctx(User.AllAccess, options: Opts(mode: mode), loggerFactory: log);
                    var rows = Rows(ToFlat<TagPartial>(partial.DC.Set<DpChild>()).DPWhere(partial, x => x.ParentId).ToList());
                    Assert.AreEqual(mode == DataPrivilegeUnresolvedTypeMode.Enforce ? "" : "m1,m2,m3", rows, "partial " + mode);
                    Assert.AreEqual(Once(env, 1), log.Entries.Count, "partial " + mode);
                }
            });
        }

        [TestMethod]
        public void MappedEntity_UnrelatedField_IsNotTreatedAsUnresolved()
        {
            Each(env =>
            {
                var log = new CapturingLoggerFactory();
                var c = env.Ctx(User.LimitedToParentA, options: Opts(mode: DataPrivilegeUnresolvedTypeMode.Enforce), loggerFactory: log);
                var plain = c.DC.Set<DpChild>();
                Assert.AreSame(plain, plain.DPWhere(c, x => x.Name), "a mapped entity with a non-key field adds nothing, whatever the mode");
                Assert.AreEqual(0, log.Entries.Count);
                // a derived view with an unrelated field resolves to its entity and behaves the same
                var view = ToView(c.DC.Set<DpChild>());
                Assert.AreSame(view, view.DPWhere(c, x => x.Name));
                Assert.AreEqual(0, log.Entries.Count);
            });
        }

        [TestMethod]
        public void ProjectedView_WithoutThePrivilegeMember_Behavior_IsPinned()
        {
            Each(env =>
            {
                var c = env.Ctx(User.LimitedToParentA, options: Opts());
                // the projection does not assign ParentId: EF cannot translate a condition on a member the projection does not carry
                var noParent = c.DC.Set<DpChild>().Select(x => new DpChildView { ID = x.ID, Name = x.Name });
                var filtered = noParent.DPWhere(c, x => x.ParentId);
                string outcome;
                try { outcome = "rows:" + Rows(filtered.ToList()); }
                catch (Exception ex) { outcome = "throws:" + ex.GetType().Name; }
                Assert.AreEqual("throws:InvalidOperationException", outcome, "the query fails at translation; it does not return every row");
                // the same projection without DPWhere works, so the failure is the condition on the missing member
                Assert.AreEqual("m1,m2,m3", Rows(noParent.ToList()));
            });
        }

        // ---- name-first resolution, multiple fields, list segments, missing settings -------------------------------------------------

        private static List<SimpleDataPri> Pri(params (string Table, string Id)[] items)
            => items.Select(x => new SimpleDataPri { TableName = x.Table, RelateId = x.Id }).ToList();

        private static WTMContext With(WTMContext c, List<SimpleDataPri> dps)
        {
            c.LoginUserInfo = new LoginUserInfo { ITCode = "u", DataPrivileges = dps };
            return c;
        }

        [TestMethod]
        public void NameFirst_UnmappedDerivedType_SettingUnderItsOwnName_IsUsed_AsAtTheBaseCommit()
        {
            Each(env =>
            {
                // the type is not mapped in WTMContext.DC, but it has a setting under its own name (the entity of another context): that setting wins over the mapped base type
                var settings = new List<IDataPrivilege> { new DataPrivilegeInfo<DpChildView>("view", x => x.Name) };
                foreach (var resolve in new[] { true, false })
                {
                    var c = With(env.Ctx(User.NoLogin, options: Opts(resolve), settingsOverride: settings), Pri(("DpChildView", env.Child["m1"].ToString())));
                    Assert.AreEqual("m1", Rows(ToView(c.DC.Set<DpChild>()).DPWhere(c, x => x.ID).ToList()), "own name, resolve=" + resolve);
                    var all = With(env.Ctx(User.NoLogin, options: Opts(resolve), settingsOverride: settings), Pri(("DpChildView", null)));
                    Assert.AreEqual("m1,m2,m3", Rows(ToView(all.DC.Set<DpChild>()).DPWhere(all, x => x.ID).ToList()), "own name, all access, resolve=" + resolve);
                    // a user with no entry for the setting gets no rows
                    var none = With(env.Ctx(User.NoLogin, options: Opts(resolve), settingsOverride: settings), Pri(("DpChild", env.Child["m1"].ToString())));
                    Assert.AreEqual("", Rows(ToView(none.DC.Set<DpChild>()).DPWhere(none, x => x.ID).ToList()), "own name, no entry, resolve=" + resolve);
                }
            });
        }

        [TestMethod]
        public void NameFirst_UnmappedDerivedType_SettingOnlyForTheBaseType_ResolvesViaTheBaseType()
        {
            Each(env =>
            {
                var settings = new List<IDataPrivilege> { new DataPrivilegeInfo<DpChild>("child", x => x.Name) };
                var c = With(env.Ctx(User.NoLogin, options: Opts(), settingsOverride: settings), Pri(("DpChild", env.Child["m1"].ToString())));
                Assert.AreEqual("m1", Rows(ToView(c.DC.Set<DpChild>()).DPWhere(c, x => x.ID).ToList()), "resolved via the base type");
                // with the walk off the type stays unresolved (silently skipped in Audit, as before)
                var off = With(env.Ctx(User.NoLogin, options: Opts(resolve: false, mode: DataPrivilegeUnresolvedTypeMode.Legacy), settingsOverride: settings), Pri(("DpChild", env.Child["m1"].ToString())));
                Assert.AreEqual("m1,m2,m3", Rows(ToView(off.DC.Set<DpChild>()).DPWhere(off, x => x.ID).ToList()), "resolve=false");
            });
        }

        // the user may read the rows of parent B and the child m1 (m3 belongs to B)
        private static List<SimpleDataPri> ParentBAndChild1(Env env) => Pri(("DpParent", env.ParentB.ToString()), ("DpChild", env.Child["m1"].ToString()));

        [TestMethod]
        public void MultiField_OrCombined_AfterProjection_EqualsBeforeProjection()
        {
            Each(env =>
            {
                // several fields are OR-combined (rows allowed by the first OR by the second); a derived view now contributes every field, as the entity does
                var c = With(env.Ctx(User.NoLogin, options: Opts()), ParentBAndChild1(env));
                var entity = c.DC.Set<DpChild>().Where(x => x.Name != "zzz");
                Assert.AreEqual("m3", Names(entity.DPWhere(c, x => x.ParentId).ToList()), "first field alone");
                Assert.AreEqual("m1", Names(entity.DPWhere(c, x => x.ID).ToList()), "second field alone");
                var before = ToView(entity.DPWhere(c, x => x.ParentId, x => x.ID)).OrderBy(x => x.Name);
                var after = ToView(entity).DPWhere(c, x => x.ParentId, x => x.ID).OrderBy(x => x.Name);
                Assert.AreEqual("m1,m3", Rows(before.ToList()), "before the projection");
                Assert.AreEqual(Rows(before.ToList()), Rows(after.ToList()), "after the projection: the same rows");
                Assert.AreEqual(Normalize(before.ToQueryString()), Normalize(after.ToQueryString()), "after the projection: the same sql");
                // the order of the fields does not matter
                var swapped = ToView(entity).DPWhere(c, x => x.ID, x => x.ParentId).OrderBy(x => x.Name);
                Assert.AreEqual("m1,m3", Rows(swapped.ToList()));
            });
        }

        [TestMethod]
        public void MultiField_OnAView_PreviousVersusNewResult_IsDocumented()
        {
            Each(env =>
            {
                // the view type has a setting under its own name for ID (resolved before and now); ParentId used to be skipped on the view and is now resolved via the entity
                var settings = new List<IDataPrivilege>
                {
                    new DataPrivilegeInfo<DpParent>("parent", x => x.Name),
                    new DataPrivilegeInfo<DpChildView>("view", x => x.Name),
                };
                var dps = Pri(("DpParent", env.ParentB.ToString()), ("DpChildView", env.Child["m1"].ToString()));
                var previous = With(env.Ctx(User.NoLogin, options: Opts(resolve: false, mode: DataPrivilegeUnresolvedTypeMode.Legacy), settingsOverride: settings), dps);
                var now = With(env.Ctx(User.NoLogin, options: Opts(), settingsOverride: settings), dps);
                Assert.AreEqual("m1", Rows(ToView(previous.DC.Set<DpChild>()).DPWhere(previous, x => x.ParentId, x => x.ID).ToList()), "ResolveProjectedTypes=false: only the field that resolved contributes");
                Assert.AreEqual("m1,m3", Rows(ToView(now.DC.Set<DpChild>()).DPWhere(now, x => x.ParentId, x => x.ID).ToList()), "default: both fields contribute (rows of either)");
            });
        }

        [TestMethod]
        public void ListSegment_OnADerivedView_IsPinned()
        {
            Each(env =>
            {
                // the last member of Children[0].ID belongs to the child entity, so the condition resolves as it does on the entity; the view carries the list
                var c = With(env.Ctx(User.LimitedToChild1, options: Opts(mode: DataPrivilegeUnresolvedTypeMode.Enforce)), Pri(("DpChild", env.Child["m1"].ToString())));
                Assert.AreEqual("A", Names(c.DC.Set<DpParent>().DPWhere(c, x => x.Children[0].ID).ToList()), "entity");
                var view = c.DC.Set<DpParent>().Select(x => new DpParentView { ID = x.ID, Name = x.Name, Children = x.Children });
                string outcome;
                try { outcome = "rows:" + string.Join(",", view.DPWhere(c, x => x.Children[0].ID).ToList().Select(x => x.Name).OrderBy(x => x)); }
                catch (Exception ex) { outcome = "throws:" + ex.GetType().Name; }
                Assert.AreEqual("rows:A", outcome, "the view gives the same rows as the entity");
                // a view that does not carry the list fails at translation, it does not return every parent
                var noList = c.DC.Set<DpParent>().Select(x => new DpParentView { ID = x.ID, Name = x.Name });
                try { outcome = "rows:" + string.Join(",", noList.DPWhere(c, x => x.Children[0].ID).ToList().Select(x => x.Name).OrderBy(x => x)); }
                catch (Exception ex) { outcome = "throws:" + ex.GetType().Name; }
                Assert.AreNotEqual("rows:A,B", outcome, "never every row");
            });
        }

        [TestMethod]
        public void NullDataPrivilegeSettings_NoException_MappedTypesAsAtTheBase_ViewsUnaffected()
        {
            Each(env =>
            {
                foreach (var mode in new[] { DataPrivilegeUnresolvedTypeMode.Legacy, DataPrivilegeUnresolvedTypeMode.Audit, DataPrivilegeUnresolvedTypeMode.Enforce })
                {
                    var log = new CapturingLoggerFactory();
                    // the context turns a null list into an empty one: nothing is configured, so nothing is protected
                    var c = With(env.Ctx(User.NoLogin, options: Opts(mode: mode), loggerFactory: log, nullSettings: true), Pri(("DpChild", env.Child["m1"].ToString())));
                    Assert.AreEqual("m1,m2,m3", Names(c.DC.Set<DpChild>().DPWhere(c, x => x.ID).ToList()), "mapped, " + mode);
                    Assert.AreEqual("m1,m2,m3", Names(c.DC.Set<DpChild>().DPWhere(c, x => x.ParentId).ToList()), "mapped fk, " + mode);
                    Assert.AreEqual("m1,m2,m3", Rows(ToView(c.DC.Set<DpChild>()).DPWhere(c, x => x.ID).ToList()), "derived view, " + mode);
                    Assert.AreEqual("m1,m2,m3", Rows(ToFlat<TagList>(c.DC.Set<DpChild>()).DPWhere(c, x => x.ParentId).ToList()), "non-derived view, " + mode);
                    Assert.AreEqual(0, log.Entries.Count, "nothing to protect, nothing logged, " + mode);
                    // no login as well
                    var nl = env.Ctx(User.NoLogin, options: Opts(mode: mode), loggerFactory: log, nullSettings: true);
                    // as on the entity (and at the base commit), no login information gives no rows even when no setting is configured
                    Assert.AreEqual("", Names(nl.DC.Set<DpChild>().DPWhere(nl, x => x.ParentId).ToList()), "mapped, no login, " + mode);
                    Assert.AreEqual("", Rows(ToView(nl.DC.Set<DpChild>()).DPWhere(nl, x => x.ParentId).ToList()), "derived view, no login, " + mode);
                }
            });
        }

        // ---- a query from another DbContext than WTMContext.DC ------------------------------------------------------------------------

        [TestMethod]
        public void EntityMappedOnlyInAnotherContext_IdField_IsStillFiltered_InEveryMode_AndNothingIsLogged()
        {
            Each(env =>
            {
                // WTMContext.DC (an EmptyContext) does not map DpChild; the query runs on a context that does.
                // At the base commit the table name came from the member's owner type name, so the condition was applied in every case below.
                using var queryDc = new DpContext(env.Cs, env.Type);
                foreach (var mode in new[] { DataPrivilegeUnresolvedTypeMode.Legacy, DataPrivilegeUnresolvedTypeMode.Audit, DataPrivilegeUnresolvedTypeMode.Enforce })
                {
                    foreach (var resolve in new[] { true, false })
                    {
                        var log = new CapturingLoggerFactory();
                        var gap = new EmptyContext(env.Cs, env.Type);
                        Assert.IsNull(gap.Model.FindEntityType(typeof(DpChild)), "the harness: DC does not map the entity");
                        foreach (var (user, expected) in new[] { (User.LimitedToChild1, "m1"), (User.AllAccessEverywhere, "m1,m2,m3"), (User.NoLogin, "") })
                        {
                            var c = env.Ctx(user, options: Opts(resolve, mode), loggerFactory: log, dc: new EmptyContext(env.Cs, env.Type));
                            Assert.AreEqual(expected, Names(queryDc.Set<DpChild>().DPWhere(c, x => x.ID).ToList()), $"{user} {mode} resolve={resolve}");
                        }
                        Assert.AreEqual(0, log.Entries.Count, $"{mode} resolve={resolve} logs nothing: the condition was resolved");
                    }
                }
                // the options absent altogether behave the same
                var d = env.Ctx(User.LimitedToChild1, dc: new EmptyContext(env.Cs, env.Type));
                Assert.AreEqual("m1", Names(queryDc.Set<DpChild>().DPWhere(d, x => x.ID).ToList()), "defaults");
            });
        }

        [TestMethod]
        public void EntityMappedOnlyInAnotherContext_FkField_StaysUnresolved_AsAtTheBaseCommit()
        {
            Each(env =>
            {
                // the foreign key cannot be resolved without the model of the query's own context (as at the base commit: the field was skipped); with Enforce it fails closed
                using var queryDc = new DpContext(env.Cs, env.Type);
                var log = new CapturingLoggerFactory();
                var a = env.Ctx(User.LimitedToParentA, options: Opts(), loggerFactory: log, dc: new EmptyContext(env.Cs, env.Type));
                Assert.AreEqual("m1,m2,m3", Names(queryDc.Set<DpChild>().DPWhere(a, x => x.ParentId).ToList()));
                Assert.AreEqual(Once(env, 1), log.Entries.Count, "Audit logs the skipped field");
                var e = env.Ctx(User.LimitedToParentA, options: Opts(mode: DataPrivilegeUnresolvedTypeMode.Enforce), loggerFactory: log, dc: new EmptyContext(env.Cs, env.Type));
                Assert.AreEqual("", Names(queryDc.Set<DpChild>().DPWhere(e, x => x.ParentId).ToList()));
            });
        }

        [TestMethod]
        public void Options_Binding_ProjectedTypeSettings_DefaultsAndInvalidValues()
        {
            var d = new Configs().DataPrivilegeOptions;
            Assert.IsTrue(d.ResolveProjectedTypes);
            Assert.AreEqual(DataPrivilegeUnresolvedTypeMode.Audit, d.UnresolvedTypeMode);
            var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["CookiePre"] = "x" }).Build().Get<Configs>();
            Assert.IsTrue(empty.DataPrivilegeOptions.ResolveProjectedTypes);
            Assert.AreEqual(DataPrivilegeUnresolvedTypeMode.Audit, empty.DataPrivilegeOptions.UnresolvedTypeMode);

            var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataPrivilegeOptions:ResolveProjectedTypes"] = "false" }).Build().Get<Configs>();
            Assert.IsFalse(off.DataPrivilegeOptions.ResolveProjectedTypes);
            foreach (var (text, expected) in new[] { ("Legacy", DataPrivilegeUnresolvedTypeMode.Legacy), ("audit", DataPrivilegeUnresolvedTypeMode.Audit), ("ENFORCE", DataPrivilegeUnresolvedTypeMode.Enforce) })
            {
                var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataPrivilegeOptions:UnresolvedTypeMode"] = text }).Build().Get<Configs>();
                Assert.AreEqual(expected, cfg.DataPrivilegeOptions.UnresolvedTypeMode, text);
            }

            foreach (var value in new[] { "0", "1", "", "abc" })
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataPrivilegeOptions:ResolveProjectedTypes"] = value }).Build();
                Assert.ThrowsException<InvalidOperationException>(() => config.Get<Configs>(), $"ResolveProjectedTypes '{value}' must be rejected");
            }
            foreach (var value in new[] { "abc", "Enforced", "off", "-1" })
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataPrivilegeOptions:UnresolvedTypeMode"] = value }).Build();
                // text that is not a mode: InvalidOperationException from the binder; a number that is no mode: the setter's exception (also at startup)
                var rejected = false;
                try { config.Get<Configs>(); } catch (Exception) { rejected = true; }
                Assert.IsTrue(rejected, $"UnresolvedTypeMode '{value}' must be rejected");
            }
        }

        // ---- Excel -------------------------------------------------------------------------------------------------------------------------

        private static List<string> ExportNames(byte[] bytes) => Excel.XlsxTestHelper.ReadSheet(bytes).Skip(1).Select(r => r[0]).OrderBy(x => x).ToList();

        [TestMethod]
        public void CheckExport_GenerateExcel_WritesOnlyTheRowsTheUserMayRead()
        {
            Each(env =>
            {
                byte[] Export(bool keep, List<string> ids)
                {
                    var vm = new DpChildExportVM { Wtm = env.Ctx(User.LimitedToParentA, keep), Q = SearchThenPrivilege, Ids = ids, SearcherMode = ListVMSearchModeEnum.CheckExport, Searcher = new BaseSearcher() };
                    return vm.GenerateExcel();
                }
                var all = env.AllChildIds();
                CollectionAssert.AreEqual(new[] { "m1", "m2" }, ExportNames(Export(true, all)));
                CollectionAssert.AreEqual(new[] { "m1", "m2", "m3" }, ExportNames(Export(false, all)));
                Assert.AreEqual(0, Excel.XlsxTestHelper.SchemaErrors(Export(true, all)).Count);
                // every ticked row is outside the privilege: the file is still written, with the header only
                var onlyOther = new List<string> { env.Child["m3"].ToString() };
                var bytes = Export(true, onlyOther);
                var sheet = Excel.XlsxTestHelper.ReadSheet(bytes);
                Assert.AreEqual(1, sheet.Count, "header row only");
                Assert.AreEqual(0, Excel.XlsxTestHelper.SchemaErrors(bytes).Count);
            });
        }
    }
}
