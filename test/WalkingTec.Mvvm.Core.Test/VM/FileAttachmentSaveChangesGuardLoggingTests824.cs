#nullable enable
// Issue #824, adversarial review of PR #978, Finding 3 (MUST FIX): the guard logged nothing at
// either throw site. BaseBatchVM.DoBatchEdit (:624-628 at review time) and DoBatchDelete catch the
// guard's exception and call SetExceptionMessage(e, id: null) -- whose body is `if (id != null)
// {...}`, so with a null id the message is discarded entirely. A live cross-tenant forgery through
// batch edit, or a resolution-query outage rejecting every save in a tenant, left ZERO server-side
// signal. This repo already solved logging from a static, DI-less class for the identical shape:
// DCExtension.ApplyDataPrivilegeForAnalysis's #843 throttled CoreProgram.GetLogger(...).LogWarning.
//
// These tests mirror DPWhereInMemoryTests.cs's ApplyDataPrivilegeForAnalysisTests.CapturingLogger/
// CapturingLoggerFactory/RunWithCapturingLogger pattern (same technique, re-declared here per this
// codebase's own convention of NOT sharing that infrastructure across test files) to swap
// CoreProgram._loggerFactory for the duration of each test and inspect what was actually logged.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Exceptions;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    [TestClass]
    public class FileAttachmentSaveChangesGuardLoggingTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"guardlogging824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();

            // The throttle is a process-wide static, keyed by (entity type, property) — clear it
            // so an earlier test method in this same run cannot suppress this one's log line.
            FileAttachmentSaveChangesGuard.ClearLoggingThrottleForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            FileAttachmentSaveChangesGuard.Enabled = true;
            FileAttachmentSaveChangesGuard.ClearLoggingThrottleForTests();
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        // ── Same CapturingLogger/CapturingLoggerFactory/RunWithCapturingLogger shape as
        // DPWhereInMemoryTests.cs's ApplyDataPrivilegeForAnalysisTests — see that file's own
        // comment for why this is duplicated rather than shared. ──

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

        [TestMethod]
        [Description("#824 adversarial review Finding 3: a rejected forged cross-tenant FK must log a warning naming the entity type, property, and id -- the server-side signal that must exist when the caller-facing message stays generic/tenant-silent")]
        public void Guard_LogsWarning_WhenRejectingForgedCrossTenantFK()
        {
            Guid victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
            }

            var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            attackerDc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = Guid.NewGuid(),
                Name = "Direct",
                PhotoId = victimFileId,
            });

            // RunWithCapturingLogger's own finally block would restore CoreProgram._loggerFactory
            // but then let the guard's exception propagate OUT of RunWithCapturingLogger itself —
            // never returning a logger to assign. Catch inside the action instead, so
            // RunWithCapturingLogger returns normally and we can inspect both the logger and the
            // caught exception afterward.
            UnresolvableFileAttachmentReferenceException? caught = null;
            var logger = RunWithCapturingLogger(() =>
            {
                try { attackerDc.SaveChanges(); }
                catch (UnresolvableFileAttachmentReferenceException ex) { caught = ex; }
            });

            Assert.IsNotNull(caught, "#824 Finding 3: the guard must still have rejected this forged write");
            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#824 Finding 3: a rejection must log exactly one warning.");
            StringAssert.Contains(warnings[0].Message, nameof(ProductWithOptionalPhoto),
                "#824 Finding 3: the log must name the entity type an operator would need to look up.");
            StringAssert.Contains(warnings[0].Message, nameof(ProductWithOptionalPhoto.PhotoId),
                "#824 Finding 3: the log must name the rejected property.");
            StringAssert.Contains(warnings[0].Message, victimFileId.ToString(),
                "#824 Finding 3: the log must carry the posted id — this is a SERVER-SIDE log, " +
                "not the client-facing message, so it may (and must, to be actionable) reveal " +
                "detail the HTTP response deliberately withholds.");
        }

        [TestMethod]
        [Description("#824 adversarial review Finding 3: repeated rejections of the SAME (entity type, property) must log only once per process, not flood the log during a sustained attack or outage")]
        public void Guard_LogsWarningOnlyOnce_ForRepeatedRejectionsOfTheSameField()
        {
            Guid victimFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                victimFileId = SeedFile(seedCtx, "TENANT_VICTIM").ID;
            }

            var logger = RunWithCapturingLogger(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var attackerDc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
                    attackerDc.SetTenantCode("TENANT_ATTACKER");
                    attackerDc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
                    {
                        ID = Guid.NewGuid(),
                        Name = "Direct" + i,
                        PhotoId = victimFileId,
                    });
                    try { attackerDc.SaveChanges(); } catch (UnresolvableFileAttachmentReferenceException) { /* expected */ }
                }
            });

            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#824 Finding 3: three rejections of the SAME (entity type, property) in the same " +
                "process must log exactly one warning, not three — the throttle exists so a " +
                "sustained attack or a resolution-query outage cannot flood the log, matching the " +
                "precedent DCExtension.ApplyDataPrivilegeForAnalysis's #843 throttle already set.");
        }

        [TestMethod]
        [Description("#824 adversarial review Finding 3: a resolution-query FAILURE (not merely an unresolvable id) must also log a warning, distinguishable from an ordinary rejection")]
        public void Guard_LogsWarning_WhenResolutionQueryFails()
        {
            // InvocationCountContext824 (FileAttachmentSaveChangesGuardInvocationCountTests824.cs,
            // same assembly) already declares both a plain and an interceptor-accepting
            // constructor plus a ProductWithOptionalPhoto DbSet — reused here rather than adding
            // an interceptor constructor to BypassGuardContext824 just for this one test.
            var interceptor = new ResolutionFailureInterceptor824();
            var dc = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite, interceptor);
            dc.SetTenantCode("TENANT_A");
            dc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = Guid.NewGuid(),
                Name = "Direct",
                PhotoId = Guid.NewGuid(), // never resolves anyway; the interceptor makes the query itself throw first
            });

            interceptor.Arm();

            UnresolvableFileAttachmentReferenceException? caught = null;
            var logger = RunWithCapturingLogger(() =>
            {
                try { dc.SaveChanges(); }
                catch (UnresolvableFileAttachmentReferenceException ex) { caught = ex; }
            });

            Assert.IsNotNull(caught, "#824 Finding 3: the guard must still have rejected this save");
            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#824 Finding 3: a resolution-query failure must also log exactly one warning.");
            StringAssert.Contains(warnings[0].Message, "resolution query",
                "#824 Finding 3: the log text for a resolution-QUERY failure must be " +
                "distinguishable from an ordinary per-candidate rejection.");
        }

        private static FileAttachment SeedFile(BypassGuardContext824 ctx, string tenantCode)
        {
            var file = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = "file.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = tenantCode,
                UploadTime = DateTime.UtcNow,
                Length = 4
            };
            ctx.Set<FileAttachment>().Add(file);
            ctx.SaveChanges();
            return file;
        }
    }

    /// <summary>
    /// Throws once armed, on every SELECT against the FileAttachment table — same technique as
    /// FileAttachmentResolutionFailureGateTests828.cs's FileAttachmentResolutionFailureInterceptor,
    /// re-declared here (a different context type, BypassGuardContext824) rather than shared.
    /// </summary>
    internal sealed class ResolutionFailureInterceptor824 : DbCommandInterceptor
    {
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeThrow(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeThrow(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void MaybeThrow(string sql)
        {
            if (!_armed) return;
            if (sql.IndexOf("FileAttachment", StringComparison.OrdinalIgnoreCase) < 0) return;
            throw new InvalidOperationException("Simulated FileAttachment resolution query failure (Issue #824 Finding 3 logging test)");
        }
    }
}
