#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    #region Test models

    public class DPTestMajor : TopBasePoco
    {
        public string? MajorName { get; set; }
    }

    public class DPTestStudent : TopBasePoco
    {
        public string? Name { get; set; }
        public Guid? MajorId { get; set; }
        public DPTestMajor? Major { get; set; }
    }

    public class DPTestEnrollment : TopBasePoco
    {
        public Guid StudentId { get; set; }
        public DPTestStudent? Student { get; set; }
        public List<DPTestTag>? Tags { get; set; }
    }

    public class DPTestTag : TopBasePoco
    {
        public string? TagName { get; set; }
        public Guid EnrollmentId { get; set; }
    }

    /// <summary>
    /// Minimal IDataPrivilege for wiring DataPrivilegeSettings without needing a DB.
    /// </summary>
    public class DPTestPrivilegeInfo : IDataPrivilege
    {
        public string ModelName { get; set; } = "";
        public string PrivillegeName { get; set; } = "";
        public Type ModelType { get; set; } = typeof(TopBasePoco);

        public List<ComboSelectListItem> GetItemList(WTMContext wtmcontext, string? filter = null, List<string>? ids = null)
            => new();

        public List<string> GetTreeParentIds(WTMContext wtmcontext, List<DataPrivilege> dps)
            => new();

        public List<string> GetTreeSubIds(WTMContext wtmcontext, List<string> pids)
            => new();
    }

    #endregion

    [TestClass]
    public class DPWhereInMemoryTests
    {
        private static WTMContext CreateWtmWithDP(
            string tableName,
            List<string?> relateIds)
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo
                {
                    ModelName = tableName,
                    PrivillegeName = tableName,
                    ModelType = typeof(TopBasePoco)
                }
            };

            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                DataPrivileges = relateIds.Select(id => new SimpleDataPri
                {
                    ID = Guid.NewGuid(),
                    TableName = tableName,
                    RelateId = id,
                    UserCode = "testuser"
                }).ToList()
            };

            return wtm;
        }

        #region AppendSelfDPWhere tests (via reflection since it's private)

        private static IQueryable<T> InvokeAppendSelfDPWhere<T>(
            IQueryable<T> query, WTMContext? wtm, List<SimpleDataPri>? dps) where T : TopBasePoco
        {
            var method = typeof(DCExtension)
                .GetMethod("AppendSelfDPWhere", BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(T));

            return (IQueryable<T>)method.Invoke(null, new object?[] { query, wtm, dps })!;
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_Filters_By_ID_List()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();

            var data = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" },
                new() { ID = id3, MajorName = "Physics" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { id1.ToString(), id3.ToString() });
            var dps = wtm.LoginUserInfo!.DataPrivileges;

            var result = InvokeAppendSelfDPWhere(data, wtm, dps).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.ID == id1));
            Assert.IsTrue(result.Any(x => x.ID == id3));
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_NullDps_Returns_Empty()
        {
            var data = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());
            // Explicitly pass null dps
            var result = InvokeAppendSelfDPWhere(data, wtm, null).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_EmptyIds_Returns_Empty()
        {
            var data = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());
            // dps list exists but has no entries for this table
            var dps = new List<SimpleDataPri>();

            var result = InvokeAppendSelfDPWhere(data, wtm, dps).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void AppendSelfDPWhere_InMemory_NullContainingIds_Returns_All()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();

            var data = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" }
            }.AsQueryable();

            // null in the ids list means "all access"
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { null });
            var dps = wtm.LoginUserInfo!.DataPrivileges;

            var result = InvokeAppendSelfDPWhere(data, wtm, dps).ToList();

            Assert.AreEqual(2, result.Count);
        }

        #endregion

        #region DPWhere tests (public extension method)

        [TestMethod]
        public void DPWhere_InMemory_Filters_By_ForeignKey()
        {
            var majorId1 = Guid.NewGuid();
            var majorId2 = Guid.NewGuid();
            var majorId3 = Guid.NewGuid();

            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = majorId1 },
                new() { ID = Guid.NewGuid(), Name = "Bob", MajorId = majorId2 },
                new() { ID = Guid.NewGuid(), Name = "Carol", MajorId = majorId3 }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { majorId1.ToString(), majorId3.ToString() });

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.Name == "Alice"));
            Assert.IsTrue(result.Any(x => x.Name == "Carol"));
        }

        [TestMethod]
        public void DPWhere_InMemory_NullDps_Returns_Empty()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // Create WTM with no login user (null dps)
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestMajor) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            // LoginUserInfo is null => dps is null

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_EmptyIds_Returns_Empty()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // User has data privileges configured for this table but no specific IDs
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_NullContainingIds_Returns_All()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() },
                new() { ID = Guid.NewGuid(), Name = "Bob", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // null in ids list means "all access" — no filter applied
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { null });

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_NoMatchingDPSetting_Returns_All()
        {
            var data = new List<DPTestStudent>
            {
                new() { ID = Guid.NewGuid(), Name = "Alice", MajorId = Guid.NewGuid() },
                new() { ID = Guid.NewGuid(), Name = "Bob", MajorId = Guid.NewGuid() }
            }.AsQueryable();

            // DP settings don't include DPTestMajor, so the filter should be skipped
            var wtm = CreateWtmWithDP("SomeOtherTable", new List<string?> { Guid.NewGuid().ToString() });

            var result = data.DPWhere(wtm, x => x.MajorId!).ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void DPWhere_InMemory_SelfId_Filters_By_Own_ID()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();

            var data = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" },
                new() { ID = id3, MajorName = "Physics" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { id1.ToString(), id2.ToString() });

            // IdField = x => x.ID filters on the entity's own ID
            var result = data.DPWhere(wtm, x => x.ID).ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.ID == id1));
            Assert.IsTrue(result.Any(x => x.ID == id2));
        }

        #endregion
    }

    // ─── ApplyDataPrivilegeForAnalysis tests (#554) ───────────────────────────

    [TestClass]
    public class ApplyDataPrivilegeForAnalysisTests
    {
        private static WTMContext CreateWtmWithDP(string tableName, List<string?> relateIds)
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo
                {
                    ModelName = tableName,
                    PrivillegeName = tableName,
                    ModelType = typeof(TopBasePoco)
                }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                DataPrivileges = relateIds.Select(id => new SimpleDataPri
                {
                    ID = Guid.NewGuid(),
                    TableName = tableName,
                    RelateId = id,
                    UserCode = "testuser"
                }).ToList()
            };
            return wtm;
        }

        [TestMethod]
        public void ApplyDataPrivilege_FiltersRowsByAllowedIds()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" },
                new() { ID = id3, MajorName = "Physics" }
            }.AsQueryable();

            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { id1.ToString(), id3.ToString() });

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Any(x => x.ID == id1));
            Assert.IsTrue(result.Any(x => x.ID == id3));
        }

        [TestMethod]
        public void ApplyDataPrivilege_DeniesAll_WhenNoPrivilegesConfigured()
        {
            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" },
                new() { ID = Guid.NewGuid(), MajorName = "English" }
            }.AsQueryable();

            // DP setting for DPTestMajor exists, but user has no relateIds → deny all
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_AllowsAll_WhenNullRelateIdPresent()
        {
            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" },
                new() { ID = Guid.NewGuid(), MajorName = "English" }
            }.AsQueryable();

            // null in relateIds list = unrestricted access
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?> { null });

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenModelNotInDPSettings()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" }
            }.AsQueryable();

            // DP settings only cover "SomeOtherTable" — DPTestMajor is not restricted
            var wtm = CreateWtmWithDP("SomeOtherTable", new List<string?> { Guid.NewGuid().ToString() });

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenElementTypeNotTopBasePoco()
        {
            // Non-TopBasePoco type should pass through unchanged
            IQueryable baseQuery = new List<string> { "a", "b", "c" }.AsQueryable();
            var wtm = CreateWtmWithDP("String", new List<string?>());

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<string>().ToList();

            Assert.AreEqual(3, result.Count);
        }

        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenWtmIsNull()
        {
            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, null)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(1, result.Count);
        }

        /// <summary>
        /// #843: prior to this fix, LoginUserInfo == null short-circuited straight to an
        /// unfiltered return — "no identity" was silently treated as "no filter", which is how a
        /// background job (DashboardSnapshotJob / DashboardAlertHostedService, both resolve
        /// WTMContext from a bare no-HttpContext scope) ended up seeing every row of a
        /// DataPrivilege-gated model regardless of who created the widget config pointing at it.
        /// The default now fails CLOSED for any model that has a DataPrivilege rule configured —
        /// same "1 != 1" outcome AppendSelfDPWhere already produces for an authenticated caller
        /// with zero assigned RelateIds (see ApplyDataPrivilege_DeniesAll_WhenNoPrivilegesConfigured
        /// above), just reached via a wider trigger (no identity at all, not merely no RelateIds).
        /// This test used to assert the OLD (buggy) fail-open behaviour under the name
        /// ApplyDataPrivilege_NoOp_WhenLoginUserInfoIsNull — renamed and re-asserted per #843.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_DeniesAll_WhenLoginUserInfoIsNull_AndModelHasDataPrivilegeConfigured()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestMajor) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            // LoginUserInfo intentionally left null — the background-job shape.

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(0, result.Count,
                "#843: no identity + a DataPrivilege rule configured for this model must deny all rows by default.");
        }

        /// <summary>
        /// #843: the fail-closed default must not over-reach — a model that was never
        /// DataPrivilege-gated in the first place stays unfiltered regardless of identity, same
        /// as ApplyDataPrivilege_NoOp_WhenModelNotInDPSettings above but with LoginUserInfo null.
        /// This is what keeps the #843 fix narrowly scoped: background jobs against models with
        /// no configured DataPrivilege rule are unaffected by this change.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_NoOp_WhenLoginUserInfoIsNull_AndModelNotInDPSettings()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = "SomeOtherTable" }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            // LoginUserInfo intentionally left null.

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" },
                new() { ID = Guid.NewGuid(), MajorName = "English" }
            }.AsQueryable();

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count);
        }

        /// <summary>
        /// #843 escape hatch: a caller that explicitly declares <c>declaredSystemQuery: true</c>
        /// gets the old (pre-#843) unfiltered behaviour back — proving the escape hatch works.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_AllowsAll_WhenLoginUserInfoIsNull_AndDeclaredSystemQuery()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestMajor) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            // LoginUserInfo intentionally left null.

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" },
                new() { ID = Guid.NewGuid(), MajorName = "English" }
            }.AsQueryable();

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm, declaredSystemQuery: true)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(2, result.Count,
                "#843: declaredSystemQuery: true is the one sanctioned, explicit escape hatch back to unfiltered access.");
        }

        /// <summary>
        /// #843 escape hatch must be opt-in, not the default: calling the same method WITHOUT
        /// passing declaredSystemQuery (i.e. using the parameter's default value) must NOT get
        /// the unfiltered behaviour — proves the default really is false / fail-closed, not a
        /// convenience overload that happens to always allow.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_DeniesAll_WhenLoginUserInfoIsNull_AndDeclaredSystemQueryOmitted()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestMajor) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = Guid.NewGuid(), MajorName = "Math" }
            }.AsQueryable();

            // No third argument — exercises the default value directly.
            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(0, result.Count,
                "#843: declaredSystemQuery must default to false — omitting it must NOT be equivalent to declaring a system query.");
        }

        /// <summary>
        /// #843: declaredSystemQuery is only a no-identity escape hatch. An authenticated caller
        /// (LoginUserInfo != null) cannot use it to bypass their own row-level DataPrivilege —
        /// the flag has no effect once there IS an identity to filter by.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_StillFiltersByOwnPrivileges_WhenLoginUserInfoPresent_AndDeclaredSystemQueryTrue()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();

            IQueryable baseQuery = new List<DPTestMajor>
            {
                new() { ID = id1, MajorName = "Math" },
                new() { ID = id2, MajorName = "English" }
            }.AsQueryable();

            // Authenticated user with zero assigned RelateIds for DPTestMajor.
            var wtm = CreateWtmWithDP(nameof(DPTestMajor), new List<string?>());

            var result = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm, declaredSystemQuery: true)
                .Cast<DPTestMajor>().ToList();

            Assert.AreEqual(0, result.Count,
                "#843: declaredSystemQuery must not let an authenticated caller bypass their own DataPrivilege filtering.");
        }

        // ─── #843 observability: fail-closed denial must not be silent ───────────
        //
        // Each test below uses its OWN dedicated element type, never shared with any other
        // test in this file or elsewhere in the assembly. ApplyDataPrivilegeForAnalysis's
        // "log once per element type per process" throttle is a process-lifetime static
        // dictionary — reusing a type (e.g. DPTestMajor, which other tests above already push
        // through the exact same denial path) would make whether THIS test observes a log
        // entirely dependent on test execution order. A fresh type per test sidesteps that.

        private sealed class CapturingLogger : ILogger
        {
            public readonly List<(LogLevel Level, string Message)> Records = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Records.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class CapturingLoggerFactory : ILoggerFactory
        {
            public readonly CapturingLogger Logger = new();
            public ILogger CreateLogger(string categoryName) => Logger;
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }
        }

        /// <summary>
        /// Swaps <see cref="CoreProgram._loggerFactory"/> — the process-wide static
        /// <c>DCExtension</c> (a static helper with no DI access) logs through, same as
        /// <c>WtmFileProvider</c> — for the duration of <paramref name="action"/>, and restores
        /// the original value afterward regardless of outcome. Never leave a test double
        /// installed process-wide after this method returns.
        /// </summary>
        private static CapturingLogger RunWithCapturingLogger(Action action)
        {
            var original = CoreProgram._loggerFactory;
            var factory = new CapturingLoggerFactory();
            CoreProgram._loggerFactory = factory;
            try
            {
                action();
            }
            finally
            {
                CoreProgram._loggerFactory = original;
            }
            return factory.Logger;
        }

        private sealed class DPTestLogWarnOnceModel : TopBasePoco
        {
            public string? Name { get; set; }
        }

        /// <summary>
        /// #843: the fail-closed default is silent unless observed — an operator debugging an
        /// unexpectedly empty background widget/snapshot needs a signal that names the model and
        /// the remedy. Asserts the warning fires, and that its text is actually useful: names the
        /// element type, points at the escape hatch, and is findable by searching for the issue.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_LogsWarning_WhenLoginUserInfoIsNull_AndModelHasDataPrivilegeConfigured()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestLogWarnOnceModel) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);

            IQueryable baseQuery = new List<DPTestLogWarnOnceModel>
            {
                new() { ID = Guid.NewGuid(), Name = "x" }
            }.AsQueryable();

            var logger = RunWithCapturingLogger(() =>
            {
                DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm).Cast<DPTestLogWarnOnceModel>().ToList();
            });

            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#843: a fail-closed denial must log exactly one warning.");
            StringAssert.Contains(warnings[0].Message, nameof(DPTestLogWarnOnceModel),
                "#843: the warning must name the element type an operator would need to look up.");
            StringAssert.Contains(warnings[0].Message, "declaredSystemQuery",
                "#843: the warning must name the remedy (declaredSystemQuery: true).");
        }

        private sealed class DPTestLogOnceModel : TopBasePoco
        {
            public string? Name { get; set; }
        }

        /// <summary>
        /// #843: this sits in a query path a background job can re-enter every few minutes —
        /// logging on every call would spam. Proves the throttle: three denials for the same
        /// element type in the same process produce exactly one warning, not three.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_LogsWarningOnlyOnce_ForRepeatedDenialsOfTheSameElementType()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestLogOnceModel) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);

            IQueryable baseQuery = new List<DPTestLogOnceModel>
            {
                new() { ID = Guid.NewGuid(), Name = "x" }
            }.AsQueryable();

            var logger = RunWithCapturingLogger(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm).Cast<DPTestLogOnceModel>().ToList();
                }
            });

            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#843: repeated denials for the SAME element type in the same process must log once, not once per call.");
        }

        private sealed class DPTestLogNoRuleModel : TopBasePoco
        {
            public string? Name { get; set; }
        }

        /// <summary>
        /// #843: a model with no DataPrivilege rule configured is a no-op regardless of identity
        /// (see ApplyDataPrivilege_NoOp_WhenLoginUserInfoIsNull_AndModelNotInDPSettings) — nothing
        /// was actually denied, so nothing should be logged either.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_DoesNotLogWarning_WhenModelHasNoDataPrivilegeConfigured()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = "SomeOtherTable" }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);

            IQueryable baseQuery = new List<DPTestLogNoRuleModel>
            {
                new() { ID = Guid.NewGuid(), Name = "x" }
            }.AsQueryable();

            var logger = RunWithCapturingLogger(() =>
            {
                DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm).Cast<DPTestLogNoRuleModel>().ToList();
            });

            Assert.AreEqual(0, logger.Records.Count(r => r.Level == LogLevel.Warning),
                "#843: a model with no configured DataPrivilege rule is a no-op — nothing was denied, so nothing should log.");
        }

        private sealed class DPTestLogDeclaredModel : TopBasePoco
        {
            public string? Name { get; set; }
        }

        /// <summary>
        /// #843: declaredSystemQuery: true takes the early return before the log block —
        /// nothing was denied, so nothing should log.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_DoesNotLogWarning_WhenDeclaredSystemQuery()
        {
            var dpSettings = new List<IDataPrivilege>
            {
                new DPTestPrivilegeInfo { ModelName = nameof(DPTestLogDeclaredModel) }
            };
            var wtm = new WTMContext(null, new GlobalData(), null, null, dpSettings);
            wtm.MSD = new BasicMSD();
            wtm.DC = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);

            IQueryable baseQuery = new List<DPTestLogDeclaredModel>
            {
                new() { ID = Guid.NewGuid(), Name = "x" }
            }.AsQueryable();

            var logger = RunWithCapturingLogger(() =>
            {
                DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm, declaredSystemQuery: true)
                    .Cast<DPTestLogDeclaredModel>().ToList();
            });

            Assert.AreEqual(0, logger.Records.Count(r => r.Level == LogLevel.Warning),
                "#843: an explicitly declared system query is not a denial — nothing should log.");
        }

        private sealed class DPTestLogAuthenticatedModel : TopBasePoco
        {
            public string? Name { get; set; }
        }

        /// <summary>
        /// #843: the log is specifically about the no-identity fail-closed path. An authenticated
        /// caller who legitimately has zero assigned RelateIds hits the SAME "1 != 1" denial in
        /// AppendSelfDPWhere, but that is pre-existing, expected, per-user behaviour — not the new
        /// #843 regression risk — and must not log.
        /// </summary>
        [TestMethod]
        public void ApplyDataPrivilege_DoesNotLogWarning_WhenLoginUserInfoPresent()
        {
            var wtm = CreateWtmWithDP(nameof(DPTestLogAuthenticatedModel), new List<string?>());

            IQueryable baseQuery = new List<DPTestLogAuthenticatedModel>
            {
                new() { ID = Guid.NewGuid(), Name = "x" }
            }.AsQueryable();

            var logger = RunWithCapturingLogger(() =>
            {
                DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, wtm).Cast<DPTestLogAuthenticatedModel>().ToList();
            });

            Assert.AreEqual(0, logger.Records.Count(r => r.Level == LogLevel.Warning),
                "#843: an authenticated caller's own zero-privilege denial is pre-existing behaviour, not the #843 no-identity case — must not log.");
        }
    }
}
