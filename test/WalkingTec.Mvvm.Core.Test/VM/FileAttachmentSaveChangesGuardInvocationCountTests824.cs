#nullable enable
// Issue #824, adversarial review of PR #978, Finding 2 (MUST FIX, distinct from the earlier
// cross-vendor review's own "Finding 2" about SavingChanges interceptors -- this one is about the
// guard running more than once per save):
//
// DbContext.SaveChanges() (EF Core's own base class implementation) is, in source, essentially
// `=> SaveChanges(acceptAllChangesOnSuccess: true)` -- an ordinary (non-base-qualified) instance
// method call from WITHIN DbContext's own method body. Because EmptyContext overrides
// SaveChanges(bool) (a virtual method), that call undergoes NORMAL virtual dispatch based on the
// runtime type, landing back on EmptyContext's own override -- not on DbContext's bool-arg
// implementation directly. Concretely, for a caller of the NO-ARG SaveChanges():
//
//   1. EmptyContext.SaveChanges() [no-arg] runs, calls base.SaveChanges() (DbContext's own
//      no-arg impl).
//   2. DbContext.SaveChanges() [no-arg]'s OWN body calls SaveChanges(true) -- ordinary virtual
//      dispatch, re-enters EmptyContext.SaveChanges(bool) (our override) a SECOND time.
//   3. EmptyContext.SaveChanges(bool) calls base.SaveChanges(bool) -- THIS base-qualified call
//      goes straight to DbContext's real bool-arg implementation (no further redispatch), and the
//      actual save happens.
//
// Before the fix, FileAttachmentSaveChangesGuard.Guard(this) was called in BOTH step 1 and step 3
// -- twice per save, including its batched FileAttachment resolution query. The fix removes the
// guard call from the two NO-ARG overrides (SaveChanges()/SaveChangesAsync(CancellationToken)),
// keeping it ONLY on the (bool)/(bool, CancellationToken) overloads that every call path -- no-arg
// or not -- always funnels through exactly once. This test proves that: (a) the guard still fires
// for a no-arg caller (via the redispatch chain, not a direct call), and (b) its resolution query
// runs exactly once, not twice.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Exceptions;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Counts every SELECT whose command text mentions "FileAttachment" -- the same SQL-matching
    /// technique <c>FileAttachmentResolutionFailureInterceptor</c>
    /// (<c>FileAttachmentResolutionFailureGateTests828.cs</c>) already uses in this test assembly.
    /// </summary>
    internal sealed class CountingResolutionQueryInterceptor824 : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeCount(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeCount(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void MaybeCount(string sql)
        {
            if (sql.IndexOf("FileAttachment", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Interlocked.Increment(ref _count);
            }
        }
    }

    internal class InvocationCountContext824 : FrameworkContext
    {
        public DbSet<ProductWithOptionalPhoto> OptionalPhotoOwners { get; set; } = null!;

        private readonly IInterceptor[] _interceptors;

        public InvocationCountContext824(string cs, DBTypeEnum dbType) : base(cs, dbType)
        {
            _interceptors = Array.Empty<IInterceptor>();
        }

        public InvocationCountContext824(string cs, DBTypeEnum dbType, params IInterceptor[] interceptors) : base(cs, dbType)
        {
            _interceptors = interceptors;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            if (_interceptors.Length > 0)
            {
                optionsBuilder.AddInterceptors(_interceptors);
            }
        }
    }

    [TestClass]
    public class FileAttachmentSaveChangesGuardInvocationCountTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"invocationcount824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        [TestMethod]
        [Description("#824 adversarial review Finding 2 (PR #978): calling the NO-ARG SaveChanges() must still invoke the guard (via DbContext's own virtual-dispatch redispatch into SaveChanges(bool)) exactly ONCE, not twice -- counts the batched FileAttachment resolution SELECT via a DbCommandInterceptor")]
        public void SaveChanges_NoArgOverload_QueriesResolutionExactlyOnce()
        {
            Guid legitFileId;
            using (var seedCtx = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_A");
                var file = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "legit.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_A",
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                };
                seedCtx.Set<FileAttachment>().Add(file);
                seedCtx.SaveChanges();
                legitFileId = file.ID;
            }

            var interceptor = new CountingResolutionQueryInterceptor824();
            var dc = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite, interceptor);
            dc.SetTenantCode("TENANT_A");
            dc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = Guid.NewGuid(),
                Name = "Widget",
                PhotoId = legitFileId,
            });

            // The no-arg overload -- the one whose SaveChanges() no longer calls the guard
            // directly, relying on the redispatch chain into SaveChanges(bool) instead.
            dc.SaveChanges();

            Assert.AreEqual(1, interceptor.Count,
                "#824 adversarial review Finding 2: the batched FileAttachment resolution query " +
                "must run exactly once per SaveChanges() call, not twice (DbContext.SaveChanges() " +
                "internally re-enters SaveChanges(bool) via virtual dispatch -- calling the guard " +
                "from BOTH overrides double-runs it and its DB query).");
        }

        [TestMethod]
        [Description("#824 adversarial review Finding 2 async: same as the sync version above, for SaveChangesAsync(CancellationToken)")]
        public async Task SaveChangesAsync_NoArgOverload_QueriesResolutionExactlyOnce()
        {
            Guid legitFileId;
            using (var seedCtx = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_A");
                var file = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "legit.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_A",
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                };
                seedCtx.Set<FileAttachment>().Add(file);
                await seedCtx.SaveChangesAsync();
                legitFileId = file.ID;
            }

            var interceptor = new CountingResolutionQueryInterceptor824();
            var dc = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite, interceptor);
            dc.SetTenantCode("TENANT_A");
            dc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = Guid.NewGuid(),
                Name = "Widget",
                PhotoId = legitFileId,
            });

            await dc.SaveChangesAsync();

            Assert.AreEqual(1, interceptor.Count,
                "#824 adversarial review Finding 2 async: the batched FileAttachment resolution " +
                "query must run exactly once per SaveChangesAsync() call, not twice.");
        }

        [TestMethod]
        [Description("#824 adversarial review Finding 2 non-regression: the no-arg overload must still REJECT a forged cross-tenant FK -- proves the guard still fires via the redispatch chain, not merely that its query count dropped")]
        public void SaveChanges_NoArgOverload_StillRejectsForgedCrossTenantFK()
        {
            Guid victimFileId;
            using (var seedCtx = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                var victim = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "victim.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_VICTIM",
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                };
                seedCtx.Set<FileAttachment>().Add(victim);
                seedCtx.SaveChanges();
                victimFileId = victim.ID;
            }

            var attackerDc = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var newId = Guid.NewGuid();
            attackerDc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = newId,
                Name = "Direct",
                PhotoId = victimFileId,
            });

            Assert.ThrowsException<UnresolvableFileAttachmentReferenceException>(() => attackerDc.SaveChanges());

            using var checkCtx = new InvocationCountContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsFalse(checkCtx.Set<ProductWithOptionalPhoto>().Any(x => x.ID == newId),
                "#824 adversarial review Finding 2 non-regression: the no-arg overload must still " +
                "reject a forged cross-tenant FK via the redispatch chain into SaveChanges(bool)");
        }
    }
}
