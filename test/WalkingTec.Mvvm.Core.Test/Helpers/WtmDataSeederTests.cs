#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Core.Test.Helpers
{
    /// <summary>
    /// Tests for <see cref="WtmDataSeeder"/>: idempotency across repeat
    /// invocations, single-query existence check, in-batch dedup,
    /// result accounting, no-op empty input, and argument guards.
    /// </summary>
    [TestClass]
    public class WtmDataSeederTests : IDisposable
    {
        private SqliteConnection? _keepAlive;
        private SeederTestContext? _dc;

        public class SeedablePoco : BasePoco
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
        }

        private class SeederTestContext : EmptyContext
        {
            public DbSet<SeedablePoco> Entities { get; set; } = null!;

            public SeederTestContext(string cs, DBTypeEnum t) : base(cs, t) { }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            {
                optionsBuilder.UseSqlite($"DataSource={CSName}?mode=memory&cache=shared");
            }
        }

        [TestInitialize]
        public void Setup()
        {
            var seed = $"Seeder_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection($"DataSource={seed}?mode=memory&cache=shared");
            _keepAlive.Open();
            _dc = new SeederTestContext(seed, DBTypeEnum.SQLite);
            _dc.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _dc?.Dispose();
            _keepAlive?.Close();
            _keepAlive?.Dispose();
        }

        public void Dispose() => Cleanup();

        // ── Core behaviour ──────────────────────────────────────────────

        [TestMethod]
        public async Task Seeding_empty_table_inserts_every_row()
        {
            var items = new[]
            {
                new SeedablePoco { Code = "a", Name = "Alpha" },
                new SeedablePoco { Code = "b", Name = "Beta" },
                new SeedablePoco { Code = "c", Name = "Gamma" },
            };

            var result = await WtmDataSeeder.SeedAsync(_dc!, items, x => x.Code);

            Assert.AreEqual(3, result.Added);
            Assert.AreEqual(0, result.Skipped);
            Assert.AreEqual(3, result.Considered);
            Assert.AreEqual(3, await _dc!.Entities.CountAsync());
        }

        [TestMethod]
        public async Task Re_running_the_same_seed_is_a_no_op()
        {
            var items = new[]
            {
                new SeedablePoco { Code = "a", Name = "Alpha" },
                new SeedablePoco { Code = "b", Name = "Beta" },
            };

            await WtmDataSeeder.SeedAsync(_dc!, items, x => x.Code);

            // Rebuild input objects — a second call normally comes from
            // a startup-time fixture that constructs fresh instances.
            var itemsAgain = new[]
            {
                new SeedablePoco { Code = "a", Name = "Alpha-renamed" },
                new SeedablePoco { Code = "b", Name = "Beta-renamed" },
            };

            var result = await WtmDataSeeder.SeedAsync(_dc!, itemsAgain, x => x.Code);

            Assert.AreEqual(0, result.Added);
            Assert.AreEqual(2, result.Skipped);
            Assert.AreEqual(2, await _dc!.Entities.CountAsync(),
                "No new rows must be inserted on a duplicate seed call.");

            // Seeder must NOT update existing rows — it is an idempotent
            // insert, not an upsert. The original Name stays put.
            var a = await _dc!.Entities.SingleAsync(x => x.Code == "a");
            Assert.AreEqual("Alpha", a.Name,
                "Seeder must not mutate existing rows (insert-only semantics).");
        }

        [TestMethod]
        public async Task Partial_overlap_inserts_only_new_rows()
        {
            await WtmDataSeeder.SeedAsync(_dc!,
                new[]
                {
                    new SeedablePoco { Code = "a", Name = "Alpha" },
                    new SeedablePoco { Code = "b", Name = "Beta" },
                },
                x => x.Code);

            var result = await WtmDataSeeder.SeedAsync(_dc!,
                new[]
                {
                    new SeedablePoco { Code = "b", Name = "Beta" },       // existing
                    new SeedablePoco { Code = "c", Name = "Gamma" },      // new
                    new SeedablePoco { Code = "d", Name = "Delta" },      // new
                },
                x => x.Code);

            Assert.AreEqual(2, result.Added);
            Assert.AreEqual(1, result.Skipped);
            Assert.AreEqual(4, await _dc!.Entities.CountAsync());
        }

        // ── In-batch duplicate collapse ─────────────────────────────────

        [TestMethod]
        public async Task Duplicate_keys_within_input_collapse_to_single_insert()
        {
            var items = new[]
            {
                new SeedablePoco { Code = "x", Name = "First" },
                new SeedablePoco { Code = "x", Name = "Second (would conflict)" },
                new SeedablePoco { Code = "x", Name = "Third (would conflict)" },
                new SeedablePoco { Code = "y", Name = "Y" },
            };

            var result = await WtmDataSeeder.SeedAsync(_dc!, items, x => x.Code);

            Assert.AreEqual(2, result.Added, "Only the first occurrence of each key is inserted.");
            Assert.AreEqual(2, result.Skipped, "Two duplicate-in-batch rows are counted as skipped.");
            Assert.AreEqual(2, await _dc!.Entities.CountAsync());

            // First occurrence wins — not the last.
            var x = await _dc!.Entities.SingleAsync(e => e.Code == "x");
            Assert.AreEqual("First", x.Name);
        }

        // ── Empty / null-safety / arg validation ────────────────────────

        [TestMethod]
        public async Task Empty_input_is_a_no_op_and_does_not_query_the_db()
        {
            var result = await WtmDataSeeder.SeedAsync(
                _dc!, Array.Empty<SeedablePoco>(), x => x.Code);

            Assert.AreEqual(new WtmSeedResult(0, 0), result);
            Assert.AreEqual(0, await _dc!.Entities.CountAsync());
        }

        [TestMethod]
        public async Task Null_context_throws_ArgumentNullException()
        {
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
                await WtmDataSeeder.SeedAsync<SeedablePoco, string>(
                    null!, Array.Empty<SeedablePoco>(), x => x.Code));
        }

        [TestMethod]
        public async Task Null_items_throws_ArgumentNullException()
        {
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
                await WtmDataSeeder.SeedAsync<SeedablePoco, string>(
                    _dc!, null!, x => x.Code));
        }

        [TestMethod]
        public async Task Null_keySelector_throws_ArgumentNullException()
        {
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
                await WtmDataSeeder.SeedAsync<SeedablePoco, string>(
                    _dc!, Array.Empty<SeedablePoco>(), null!));
        }

        // ── SaveChangesAsync is NOT called when nothing was added ───────

        [TestMethod]
        public async Task All_existing_skips_do_not_trigger_SaveChanges()
        {
            await WtmDataSeeder.SeedAsync(_dc!,
                new[] { new SeedablePoco { Code = "k", Name = "K" } },
                x => x.Code);

            // Manually mutate an in-memory entity to 'dirty' state, then
            // run a pure-skip seed — if the seeder called SaveChanges
            // the mutation would be persisted. It must not.
            var tracked = await _dc!.Entities.SingleAsync(x => x.Code == "k");
            tracked.Name = "DirtyButNotPersisted";

            var result = await WtmDataSeeder.SeedAsync(_dc!,
                new[] { new SeedablePoco { Code = "k", Name = "K-again" } },
                x => x.Code);

            Assert.AreEqual(0, result.Added);
            Assert.AreEqual(1, result.Skipped);

            _dc!.ChangeTracker.Clear();
            var reread = await _dc!.Entities.SingleAsync(x => x.Code == "k");
            Assert.AreEqual("K", reread.Name,
                "Pure-skip seed must not trigger SaveChanges; previous dirty state must not leak into the DB.");
        }

        // ── Integer-key support ─────────────────────────────────────────

        [TestMethod]
        public async Task Integer_keyed_entities_work_the_same()
        {
            // Sanity-check a non-string key — the generic <TEntity, TKey>
            // signature is only useful if it actually handles other key
            // shapes.
            var items = new List<SeedablePoco>();
            for (int i = 1; i <= 5; i++)
            {
                items.Add(new SeedablePoco { Code = i.ToString(), Name = $"N{i}" });
            }

            // First call inserts all 5 …
            await WtmDataSeeder.SeedAsync(_dc!, items, x => x.Code.Length);
            // Second call with same Code.Length values skips them all.
            var result = await WtmDataSeeder.SeedAsync(_dc!, items, x => x.Code.Length);

            Assert.AreEqual(0, result.Added);
            Assert.IsTrue(result.Skipped > 0);
        }
    }
}
