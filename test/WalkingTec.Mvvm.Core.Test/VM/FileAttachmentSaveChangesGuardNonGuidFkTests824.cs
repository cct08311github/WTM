#nullable enable
// Issue #824, adversarial review of PR #978, Finding 7 (disclosed via logging, not enforced --
// see FileAttachmentSaveChangesGuard.LogNonGuidAttachmentFk's own doc comment for why full
// non-Guid key support is out of scope for this boundary guard):
//
// FileAttachmentSaveChangesGuard.BuildMap recognises a candidate FK purely by relationship SHAPE
// -- the PRINCIPAL entity type is FileAttachment or derived from it -- regardless of the FK
// property's own CLR type. Every FileAttachment-derived type shipped in THIS repository inherits
// TopBasePoco.ID (Guid, non-virtual), so a convention-discovered FK following the principal KEY is
// always Guid-typed in practice. The one legal EF Core shape that defeats this: a relationship
// configured via HasForeignKey(...).HasPrincipalKey(x => x.SomeAlternateKey) against a non-Guid
// ALTERNATE key on a FileAttachment-derived type. Before this fix, BuildMap added such an FK to
// its per-property list unconditionally, then ExtractGuid (Guid-only) silently returned null for
// every value on it -- CollectCandidates never produced a candidate, the guard never queried,
// never rejected, and nothing signalled that this field even existed. A caller could point such a
// field at ANY tenant's file (or an id that resolves to nothing at all) with zero enforcement and
// zero server-side trace -- exactly the "looks handled but isn't" shape this whole campaign has
// been closing everywhere else.
//
// The fix (FileAttachmentSaveChangesGuard.cs, BuildMap): when a recognised FK's own CLR type is
// not Guid/Guid?, it is now EXCLUDED from the map (same observable behaviour as before -- this
// field remains unenforced, full non-Guid support is genuinely out of scope) but a throttled
// warning is logged once per (entity type, property) per process via LogNonGuidAttachmentFk, using
// the same CoreProgram.GetLogger + ConcurrentDictionary throttle pattern Finding 3's rejection/
// resolution-failure logging already established in this class.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // A relationship whose principal is FileAttachment itself, keyed for THIS one relationship by
    // a non-Guid ALTERNATE key (FileName -- [Required] string, see FileAttachment.cs) rather than
    // its own (still Guid) primary key -- the one EF Core shape that produces a "recognised by
    // shape, not enforceable by value" FK. (A DERIVED FileAttachment subtype cannot carry its own
    // alternate key under EF Core's default TPH mapping -- "A key cannot be configured on
    // '<derived>' because it is a derived type. The key must be configured on the root type
    // 'FileAttachment'." -- so this fixture configures the alternate key on the root type
    // directly; FileAttachment is itself a valid FileAttachment-or-derived principal for
    // DCExtension.IsFileAttachmentPrincipal, so this exercises the exact same BuildMap branch.)
    public class DocumentByHash824 : TopBasePoco
    {
        public string? FileHash { get; set; }
        public FileAttachment? File { get; set; }
    }

    internal class NonGuidFkContext824 : FrameworkContext
    {
        public DbSet<DocumentByHash824> Documents { get; set; } = null!;

        public NonGuidFkContext824(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<FileAttachment>().HasAlternateKey(x => x.FileName);
            modelBuilder.Entity<DocumentByHash824>()
                .HasOne(x => x.File)
                .WithMany()
                .HasForeignKey(x => x.FileHash)
                .HasPrincipalKey(x => x.FileName);
        }
    }

    [TestClass]
    public class FileAttachmentSaveChangesGuardNonGuidFkTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"nonguidfk824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();

            FileAttachmentSaveChangesGuard.ClearCacheForTests();
            FileAttachmentSaveChangesGuard.ClearLoggingThrottleForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            FileAttachmentSaveChangesGuard.ClearCacheForTests();
            FileAttachmentSaveChangesGuard.ClearLoggingThrottleForTests();
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        // ── Same CapturingLogger/CapturingLoggerFactory/RunWithCapturingLogger shape as
        // FileAttachmentSaveChangesGuardLoggingTests824.cs -- duplicated per this codebase's own
        // convention of not sharing test infrastructure across files. ──

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

        private static FileAttachment SeedFile(NonGuidFkContext824 ctx, string fileName, string tenantCode)
        {
            var file = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = fileName,
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = tenantCode,
                UploadTime = DateTime.UtcNow,
                Length = 4,
            };
            ctx.Set<FileAttachment>().Add(file);
            ctx.SaveChanges();
            return file;
        }

        [TestMethod]
        [Description("#824 Finding 7: a FileAttachment-principal FK whose own CLR type is not Guid (here: a string FK against a non-Guid alternate key) must log exactly one warning naming the entity type, property, principal type, and actual CLR type -- the field is unenforceable and this is the only signal that it exists")]
        public void SaveChanges_NonGuidFileAttachmentForeignKey_LogsWarningOnce()
        {
            // BuildMap walks the WHOLE model (every entity type) the FIRST time ANY SaveChanges
            // call reaches this DbContext type's (process-wide-cached) IModel -- including a
            // seed-only SaveChanges that never touches DocumentByHash824 at all. The seed step
            // must therefore run INSIDE the captured-logger scope too, or it silently consumes
            // the once-per-process throttle before the assertion below ever gets to observe it.
            var logger = RunWithCapturingLogger(() =>
            {
                using (var seedCtx = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite))
                {
                    seedCtx.SetTenantCode("TENANT_A");
                    SeedFile(seedCtx, "doc1.txt", "TENANT_A");
                }

                var dc = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite);
                dc.SetTenantCode("TENANT_A");
                dc.Set<DocumentByHash824>().Add(new DocumentByHash824
                {
                    ID = Guid.NewGuid(),
                    FileHash = "doc1.txt",
                });
                dc.SaveChanges();
            });

            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#824 Finding 7: BuildMap must log exactly one warning for this non-Guid " +
                "attachment FK the first time the model is used.");
            StringAssert.Contains(warnings[0].Message, nameof(DocumentByHash824),
                "#824 Finding 7: the log must name the entity type.");
            StringAssert.Contains(warnings[0].Message, nameof(DocumentByHash824.FileHash),
                "#824 Finding 7: the log must name the unenforceable property.");
            StringAssert.Contains(warnings[0].Message, nameof(FileAttachment),
                "#824 Finding 7: the log must name the FileAttachment (or derived) principal type.");
        }

        [TestMethod]
        [Description("#824 Finding 7: the throttle must suppress a second warning for the SAME (entity type, property) across a second, independent SaveChanges call in the same process -- matching Finding 3's established once-per-process throttle")]
        public void SaveChanges_NonGuidFileAttachmentForeignKey_LogsOnlyOnceAcrossMultipleSaves()
        {
            // Same reasoning as LogsWarningOnce above: the seed step is the first SaveChanges
            // call to reach this model, so it must be inside the captured-logger scope.
            var logger = RunWithCapturingLogger(() =>
            {
                using (var seedCtx = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite))
                {
                    seedCtx.SetTenantCode("TENANT_A");
                    SeedFile(seedCtx, "doc1.txt", "TENANT_A");
                    SeedFile(seedCtx, "doc2.txt", "TENANT_A");
                }

                var fileNames = new[] { "doc1.txt", "doc2.txt" };
                foreach (var fileName in fileNames)
                {
                    var dc = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite);
                    dc.SetTenantCode("TENANT_A");
                    dc.Set<DocumentByHash824>().Add(new DocumentByHash824
                    {
                        ID = Guid.NewGuid(),
                        FileHash = fileName,
                    });
                    dc.SaveChanges();
                }
            });

            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#824 Finding 7: two SaveChanges calls touching the same non-Guid attachment FK " +
                "must log exactly one warning total, not one per call.");
        }

        [TestMethod]
        [Description("#824 Finding 7 disclosed narrowing, made explicit rather than left as an implicit assumption: a non-Guid FileAttachment-principal FK is NOT tenant-checked by this guard -- a caller can post ANOTHER TENANT's real FileName and it persists, because this field never becomes a candidate at all. The raw database FK constraint still requires the value to reference an EXISTING row (referential integrity is not lost), but the #824 THREAT MODEL -- blocking a reference to a real row outside the caller's own tenant -- is not enforced for this field. This documents the boundary of the fix rather than asserting a false 'automatic coverage' claim.")]
        public void SaveChanges_NonGuidFileAttachmentForeignKey_CrossTenantRealReference_NotRejected()
        {
            using (var seedCtx = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                SeedFile(seedCtx, "victim-only.txt", "TENANT_VICTIM");
            }

            var attackerDc = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var docId = Guid.NewGuid();
            attackerDc.Set<DocumentByHash824>().Add(new DocumentByHash824
            {
                ID = docId,
                FileHash = "victim-only.txt", // a REAL row, but owned by a DIFFERENT tenant
            });

            // Must NOT throw UnresolvableFileAttachmentReferenceException -- the guard's own
            // tenant-scoped resolution never ran against this field (Finding 7's disclosed gap).
            attackerDc.SaveChanges();

            using var checkCtx = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<DocumentByHash824>().First(x => x.ID == docId);
            Assert.AreEqual("victim-only.txt", reloaded.FileHash,
                "#824 Finding 7: the cross-tenant reference persists unrejected -- this guard " +
                "never rewrites a posted value (see the class's own 'Never rewrite a posted " +
                "value' doc comment), it either resolves-and-allows, rejects, or (here, disclosed " +
                "rather than fixed) never sees this field as a candidate at all.");
        }
    }
}
