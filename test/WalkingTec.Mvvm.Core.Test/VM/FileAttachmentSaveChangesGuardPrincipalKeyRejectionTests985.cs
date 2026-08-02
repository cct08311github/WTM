#nullable enable
// Issue #985, cross-vendor review of #824 (PR #978)/#983: FileAttachmentSaveChangesGuard.BuildMap
// used to keep only the dependent FK property NAME and the principal's CLR TYPE, discarding
// fk.PrincipalKey entirely. Every Guid candidate collected downstream is resolved with
// DCExtension.ResolveFileAttachmentIds(Async)'s HARDCODED `x.ID` query
// (DCExtension.FileAttachmentResolution.cs:72,:114) -- so "this FK's relationship SHAPE matches"
// (principal is FileAttachment-or-derived) was never the same fact as "the resolution query
// answers the right question for THIS FK".
//
// A relationship configured via HasForeignKey(...).HasPrincipalKey(x => x.SomeGuidAlternateKey)
// against a Guid-typed alternate key on FileAttachment produced BOTH:
//   - a FALSE ALLOW: victim ID=V, AlternateGuid=A; attacker ID=A, AlternateGuid=B. The dependent
//     FK carries A (against principal key AlternateGuid, so the DB FK constraint really does point
//     at the victim's row) -- but the guard's resolution query filters on `x.ID`, matches the
//     ATTACKER's own row (whose ID happens to equal A), and permits it.
//   - a FALSE REJECT: a legitimate FK value carrying a real AlternateGuid is never found by ID and
//     throws UnresolvableFileAttachmentReferenceException even though the DB FK is perfectly valid.
//
// The design gate settled this as a MODEL CONFIGURATION error to reject LOUDLY at BuildMap time
// (a NotSupportedException, thrown once per distinct IModel, deterministic and loud at first use --
// see FileAttachmentSaveChangesGuard._fkMapCache's own doc comment: GetOrAdd does not cache a
// throwing factory, so every SaveChanges on that model rethrows), NOT a resolution query to
// generalize. See FileAttachmentSaveChangesGuard's class doc comment for the full narrative.

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
    // ── Fixture 1 (single Guid alternate key): the alternate key is a SHADOW property added to
    // FileAttachment (the root type -- see FileAttachmentSaveChangesGuardNonGuidFkTests824.cs's own
    // comment on why a derived FileAttachment subtype cannot carry its own key under TPH) purely
    // via Fluent API, exactly the shape a downstream context can configure WITHOUT touching
    // FileAttachment.cs at all. ──

    public class DocumentByGuidAltKey985 : TopBasePoco
    {
        public Guid? AttachmentRef { get; set; }
        public FileAttachment? Attachment { get; set; }
    }

    internal class GuidAlternateKeyContext985 : FrameworkContext
    {
        public DbSet<DocumentByGuidAltKey985> Documents { get; set; } = null!;

        public GuidAlternateKeyContext985(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<FileAttachment>().Property<Guid>("AlternateGuid985");
            modelBuilder.Entity<FileAttachment>().HasAlternateKey("AlternateGuid985");
            modelBuilder.Entity<DocumentByGuidAltKey985>()
                .HasOne(x => x.Attachment)
                .WithMany()
                .HasForeignKey(x => x.AttachmentRef)
                .HasPrincipalKey("AlternateGuid985");
        }
    }

    // ── Seed-only context sharing GuidAlternateKeyContext985's connection string and its
    // AlternateGuid985 shadow-property mapping, but WITHOUT declaring DocumentByGuidAltKey985 --
    // its own model therefore has ZERO FileAttachment-principal FKs, so its own BuildMap map is
    // empty and its SaveChanges never trips the #985 throw. Needed because BuildMap walks the
    // WHOLE model the FIRST time ANY SaveChanges call reaches GuidAlternateKeyContext985's own
    // model (see FileAttachmentSaveChangesGuardNonGuidFkTests824.cs's own doc comment) -- so even
    // a seed-only save through THAT context type would throw before any test data could be
    // planted, on the very fix this test exists to prove. ──

    internal class GuidAlternateKeySeedContext985 : FrameworkContext
    {
        public GuidAlternateKeySeedContext985(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<FileAttachment>().Property<Guid>("AlternateGuid985");
        }
    }

    // ── Fixture 2 (composite key with a Guid component): a two-property alternate key made of one
    // existing non-nullable CLR property (FileName) and one shadow Guid property. FileAttachment's
    // FK-property-count-equals-principal-key-property-count invariant (EF Core's ForeignKey.
    // AreCompatible -> ForeignKeyCountMismatch) means a composite dependent FK can only ever be
    // configured against a composite principal key -- never against the single-column ID PK -- so
    // this exercises the "composite comes free" claim in FileAttachmentSaveChangesGuard's own doc
    // comment: the SAME non-canonical-principal-key check that rejects the single-Guid-AK shape
    // also rejects this one, with no separate composite-specific branch in BuildMap. ──

    public class DocumentByCompositeGuidKey985 : TopBasePoco
    {
        public string? AttachmentFileNameRef { get; set; }
        public Guid? AttachmentGuidRef { get; set; }
    }

    internal class CompositeGuidKeyContext985 : FrameworkContext
    {
        public DbSet<DocumentByCompositeGuidKey985> Documents { get; set; } = null!;

        public CompositeGuidKeyContext985(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<FileAttachment>().Property<Guid>("CompositeGuidPart985");
            modelBuilder.Entity<FileAttachment>().HasAlternateKey(nameof(FileAttachment.FileName), "CompositeGuidPart985");
            modelBuilder.Entity<DocumentByCompositeGuidKey985>()
                .HasOne<FileAttachment>()
                .WithMany()
                .HasForeignKey(x => new { x.AttachmentFileNameRef, x.AttachmentGuidRef })
                .HasPrincipalKey(nameof(FileAttachment.FileName), "CompositeGuidPart985");
        }
    }

    [TestClass]
    public class FileAttachmentSaveChangesGuardPrincipalKeyRejectionTests985
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"pkreject985_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

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

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Test 1: single Guid alternate key -- replaces BOTH the false-allow and false-reject
        // reproductions from the issue.
        //
        // Why one throwing test replaces two behavioural reproductions: the false-allow repro
        // (attacker ID == victim's AlternateGuid, waved through) and the false-reject repro (a
        // legitimate AlternateGuid value never found by ID, rejected) are two DIFFERENT SYMPTOMS
        // of the exact SAME root cause -- BuildMap trusting ANY Guid-shaped candidate FK to be
        // resolvable by `x.ID` regardless of what fk.PrincipalKey actually is. The fix does not
        // patch either symptom individually (e.g. teaching the resolution query to also try
        // AlternateGuid); it removes the root cause by refusing to build a map entry for this
        // shape AT ALL, at BuildMap time, before any SaveChanges call -- so neither the false
        // allow nor the false reject can ever be reached: SaveChanges throws before any
        // resolution query runs, for BOTH an attacker's forged reference and a legitimate one.
        // Reproducing both symptoms separately post-fix would only prove the same single
        // NotSupportedException fires from two different call sites -- redundant with proving it
        // fires at all.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#985/#1000: SaveChanges on a model with a Guid-alternate-key FileAttachment FK throws NotSupportedException naming the entity, the FK property, and the principal key's own property -- confirmed via the guard's real entry point (SaveChanges), the first (and only) place BuildMap is ever invoked (FileAttachmentSaveChangesGuard.GetOrBuildMap, called from Guard/GuardAsync) -- AND rethrows on a second SaveChanges against the same dc instance, proving docs/production-readiness.md's #985 row's 'rethrows on every subsequent SaveChanges' claim rather than merely asserting it (#1000 Part 1)")]
        public void SaveChanges_GuidAlternateKeyPrincipal_ThrowsAtFirstUse()
        {
            using (var setupCtx = new GuidAlternateKeyContext985(ConnectionString, DBTypeEnum.SQLite))
            {
                setupCtx.Database.EnsureCreated();
            }

            var dc = new GuidAlternateKeyContext985(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");
            dc.Set<DocumentByGuidAltKey985>().Add(new DocumentByGuidAltKey985
            {
                ID = Guid.NewGuid(),
                AttachmentRef = Guid.NewGuid(),
            });

            var ex = Assert.ThrowsException<NotSupportedException>(() => dc.SaveChanges());

            StringAssert.Contains(ex.Message, nameof(DocumentByGuidAltKey985),
                "#985: the message must name the entity carrying the misconfigured FK.");
            StringAssert.Contains(ex.Message, nameof(DocumentByGuidAltKey985.AttachmentRef),
                "#985: the message must name the FK property.");
            StringAssert.Contains(ex.Message, "AlternateGuid985",
                "#985: the message must name the principal key's own property.");
            StringAssert.Contains(ex.Message, "FileAttachmentSaveChangesGuard.Enabled",
                "#985: the message must document the Enabled = false opt-out remediation.");

            // Not the request-time exception type: a model-configuration error must never be
            // confused with "a posted id failed tenant-scoped resolution" (see this class's own
            // doc comment on UnresolvableFileAttachmentReferenceException for why reusing it would
            // make a configuration mistake look like a detected attack).
            Assert.IsFalse(ex is WalkingTec.Mvvm.Core.Exceptions.UnresolvableFileAttachmentReferenceException,
                "#985: must NOT reuse UnresolvableFileAttachmentReferenceException.");

            // Issue #1000 Part 1: docs/production-readiness.md's #985 row claims "每一次 SaveChanges
            // 碰到這個模型都會重新拋出，不是「第一次拋、之後靜默通過」" (every SaveChanges against this
            // model rethrows -- not "throws once, then silently succeeds"), reasoning from
            // ConcurrentDictionary.GetOrAdd's documented contract (a throwing factory is never
            // cached). That reasoning was never actually exercised by a second call before #1000 --
            // this proves it, not merely asserts it: the SAME dc instance (the Added entity above is
            // still tracked; nothing was rolled back by the guard's own throw) must also throw
            // NotSupportedException on an immediately-following second SaveChanges call.
            var ex2 = Assert.ThrowsException<NotSupportedException>(() => dc.SaveChanges());
            StringAssert.Contains(ex2.Message, nameof(DocumentByGuidAltKey985),
                "#1000: the SECOND SaveChanges on the same dc instance must also throw and name the same entity -- proving BuildMap reruns rather than the rejection being cached or silently bypassed after the first call.");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Mutation-gate reproduction (test/mutants/entries/fileattachmentguard985-principal-key-
        // check-neutralize.json): the CONCRETE false-allow shape from the issue, planted as data
        // so that if the #985 check were ever deleted, THIS exact scenario is what would slip
        // through -- not merely "some Guid AK FK", but the specific attacker-ID-equals-victim-
        // AlternateGuid collision. On the fixed code this throws NotSupportedException exactly
        // like Test 1 above (the throw fires from the model SHAPE alone, before any data is even
        // looked at) -- the mutation gate's value is in what happens WITHOUT the check: victim
        // FileAttachment (ID=V, AlternateGuid985=A, TENANT_VICTIM) and attacker's OWN FileAttachment
        // (ID=A, AlternateGuid985=B, TENANT_ATTACKER) both exist; the attacker's dependent FK
        // carries A (the TRUE relationship, via AlternateGuid985, points at the VICTIM's row). With
        // the check deleted, BuildMap treats AttachmentRef as an ordinary Guid candidate, and
        // DCExtension.ResolveFileAttachmentIds's `x.ID == A` query, scoped to TENANT_ATTACKER,
        // finds the ATTACKER'S OWN row (whose ID the attacker's data happens to equal) and
        // incorrectly reports it resolved -- SaveChanges succeeds SILENTLY instead of throwing.
        // Seeded via GuidAlternateKeySeedContext985 (a separate model with no FileAttachment FK at
        // all) specifically so seeding itself can never trip GuidAlternateKeyContext985's own
        // BuildMap throw before this test's real assertion runs.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#985 mutation-gate reproduction: attacker's own FileAttachment.ID deliberately equals the victim's AlternateGuid985 value -- the exact false-allow collision from the issue. Must throw NotSupportedException before any resolution query can be reached; without the #985 check, this exact data would resolve against the attacker's own row and be silently permitted")]
        public void SaveChanges_GuidAlternateKeyPrincipal_AttackerIdEqualsVictimAlternateKey_ThrowsAtFirstUse()
        {
            using (var setupCtx = new GuidAlternateKeyContext985(ConnectionString, DBTypeEnum.SQLite))
            {
                setupCtx.Database.EnsureCreated();
            }

            var victimId = Guid.NewGuid();
            var victimAlternateGuid = Guid.NewGuid();
            var attackerAlternateGuid = Guid.NewGuid();
            // The attacker's own FileAttachment.ID is deliberately set EQUAL to the victim's
            // AlternateGuid985 value -- the collision the false-allow reproduction depends on.
            var attackerId = victimAlternateGuid;

            using (var seedCtx = new GuidAlternateKeySeedContext985(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                var victim = new FileAttachment
                {
                    ID = victimId,
                    FileName = "victim985.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_VICTIM",
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                };
                seedCtx.Set<FileAttachment>().Add(victim);
                seedCtx.Entry(victim).Property("AlternateGuid985").CurrentValue = victimAlternateGuid;
                seedCtx.SaveChanges();

                seedCtx.SetTenantCode("TENANT_ATTACKER");
                var attackerFile = new FileAttachment
                {
                    ID = attackerId,
                    FileName = "attacker985.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_ATTACKER",
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                };
                seedCtx.Set<FileAttachment>().Add(attackerFile);
                seedCtx.Entry(attackerFile).Property("AlternateGuid985").CurrentValue = attackerAlternateGuid;
                seedCtx.SaveChanges();
            }

            var dc = new GuidAlternateKeyContext985(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_ATTACKER");
            dc.Set<DocumentByGuidAltKey985>().Add(new DocumentByGuidAltKey985
            {
                ID = Guid.NewGuid(),
                AttachmentRef = victimAlternateGuid, // == attackerId; the TRUE relationship (via AlternateGuid985) points at the VICTIM's row
            });

            Assert.ThrowsException<NotSupportedException>(() => dc.SaveChanges(),
                "#985: BuildMap must reject this model shape before any resolution query runs -- " +
                "this is what makes the false-allow collision above unreachable.");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Test 2: composite key with a Guid component -- proves "composite comes free": the SAME
        // non-canonical-principal-key check rejects this shape too, with no separate branch.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#985: a FileAttachment-principal FK whose relationship targets a COMPOSITE principal key containing a Guid component must also throw NotSupportedException at first use -- proving BuildMap needs no separate composite-specific branch, only the single non-canonical-principal-key check")]
        public void SaveChanges_CompositeKeyWithGuidComponent_ThrowsAtFirstUse()
        {
            using (var setupCtx = new CompositeGuidKeyContext985(ConnectionString, DBTypeEnum.SQLite))
            {
                setupCtx.Database.EnsureCreated();
            }

            var dc = new CompositeGuidKeyContext985(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");
            dc.Set<DocumentByCompositeGuidKey985>().Add(new DocumentByCompositeGuidKey985
            {
                ID = Guid.NewGuid(),
                AttachmentFileNameRef = "whatever.txt",
                AttachmentGuidRef = Guid.NewGuid(),
            });

            var ex = Assert.ThrowsException<NotSupportedException>(() => dc.SaveChanges());

            StringAssert.Contains(ex.Message, nameof(DocumentByCompositeGuidKey985),
                "#985: the message must name the entity carrying the misconfigured composite FK.");
            StringAssert.Contains(ex.Message, nameof(DocumentByCompositeGuidKey985.AttachmentGuidRef),
                "#985: the message must name at least the Guid-typed FK property.");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Test 3: non-Guid alternate key -- Finding 7 regression pin. This must NOT change: a
        // principal key that is neither the canonical single Guid ID nor has any Guid-typed FK
        // property falls straight through to the pre-existing, byte-for-byte-unchanged
        // warn-and-exclude loop. Reuses the EXISTING Finding 7 fixture
        // (NonGuidFkContext824/DocumentByHash824 from FileAttachmentSaveChangesGuardNonGuidFkTests824.cs,
        // same assembly/namespace) rather than declaring a parallel one, specifically so this test
        // exercises the identical code path Finding 7's own test suite already pins.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#985 / #824 Finding 7 regression pin: a non-Guid alternate-key FileAttachment FK must still warn-and-skip, NOT throw -- the new #985 check must never touch this pre-existing, deliberately-unenforced shape")]
        public void SaveChanges_NonGuidAlternateKeyPrincipal_StillWarnsAndSkips()
        {
            using (var seedCtx = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.SetTenantCode("TENANT_A");
                seedCtx.Set<FileAttachment>().Add(new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "doc985.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_A",
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                });
                seedCtx.SaveChanges();
            }

            var dc = new NonGuidFkContext824(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");
            dc.Set<DocumentByHash824>().Add(new DocumentByHash824
            {
                ID = Guid.NewGuid(),
                FileHash = "doc985.txt",
            });

            dc.SaveChanges(); // must NOT throw -- neither NotSupportedException nor the request-time exception
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Test 4: positive control -- an in-tree model's map is unchanged and normal saves still
        // work. Reuses the EXISTING canonical fixture (BypassGuardContext824/ProductWithOptionalPhoto,
        // convention-discovered PhotoId -> FileAttachment.ID) rather than declaring a parallel one,
        // so this exercises the exact model shape #824's own bypass-path suite already depends on.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#985 positive control: a canonical, in-tree, convention-discovered FileAttachment FK (principal key IS FileAttachment.ID) must NOT throw the new NotSupportedException, and a legitimate same-tenant save must still persist normally -- the #985 check changes nothing for the one shape every in-tree model actually uses")]
        public void SaveChanges_InTreeCanonicalGuidIdPrincipal_MapUnchanged_NormalSavesStillWork()
        {
            using (var ctx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                ctx.Database.EnsureCreated();
            }

            Guid legitFileId;
            using (var seedCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_A");
                var file = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "legit985.txt",
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

            var dc = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");
            var newId = Guid.NewGuid();
            dc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = newId,
                Name = "Positive985",
                PhotoId = legitFileId,
            });

            dc.SaveChanges(); // must NOT throw -- #985 changes nothing for this canonical shape

            using var checkCtx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloaded = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == newId);
            Assert.AreEqual(legitFileId, reloaded.PhotoId,
                "#985 positive control: a legitimate same-tenant FK against the canonical FileAttachment.ID principal key must still persist exactly as before this fix.");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Issue #1000 Part 1: BuildPrincipalKeyRejectionException's throw site previously logged
        // nothing at all -- the same Finding 3 (#824) gap this class's OTHER three rejection
        // decisions already closed, just never applied to this fourth one when #985 added it (see
        // FileAttachmentSaveChangesGuard.cs's own rationale comment above _loggedRejections, and
        // LogPrincipalKeyRejection's doc comment). Same CapturingLogger/CapturingLoggerFactory/
        // RunWithCapturingLogger technique as FileAttachmentSaveChangesGuardLoggingTests824.cs,
        // re-declared here per this codebase's own convention of NOT sharing that infrastructure
        // across test files.
        // ─────────────────────────────────────────────────────────────────────────────────────

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
        [Description("#1000 Part 1: the #985 principal-key rejection must log a warning at the throw site itself -- naming the entity, the FK propert(y/ies), and the principal key's own propert(y/ies) -- the server-side signal this decision point never had before #1000, matching the shape/level this class already established for its other three rejection decisions")]
        public void SaveChanges_GuidAlternateKeyPrincipal_LogsWarningAtThrow()
        {
            using (var setupCtx = new GuidAlternateKeyContext985(ConnectionString, DBTypeEnum.SQLite))
            {
                setupCtx.Database.EnsureCreated();
            }

            var dc = new GuidAlternateKeyContext985(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");
            dc.Set<DocumentByGuidAltKey985>().Add(new DocumentByGuidAltKey985
            {
                ID = Guid.NewGuid(),
                AttachmentRef = Guid.NewGuid(),
            });

            NotSupportedException? caught = null;
            var logger = RunWithCapturingLogger(() =>
            {
                try { dc.SaveChanges(); }
                catch (NotSupportedException ex) { caught = ex; }
            });

            Assert.IsNotNull(caught, "#1000: the guard must still have thrown NotSupportedException.");
            var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(1, warnings.Count,
                "#1000: the principal-key rejection must log exactly one warning at the throw site.");
            StringAssert.Contains(warnings[0].Message, nameof(DocumentByGuidAltKey985),
                "#1000: the log must name the entity carrying the misconfigured FK.");
            StringAssert.Contains(warnings[0].Message, nameof(DocumentByGuidAltKey985.AttachmentRef),
                "#1000: the log must name the FK property.");
            StringAssert.Contains(warnings[0].Message, "AlternateGuid985",
                "#1000: the log must name the principal key's own property.");
        }
    }
}
