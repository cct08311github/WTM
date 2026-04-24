#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core.Helper
{
    /// <summary>
    /// Result of a <see cref="WtmDataSeeder"/> operation. Exposes how
    /// many rows were newly inserted vs. skipped because a row with the
    /// same business key already existed — useful for log lines /
    /// seeding reports.
    /// </summary>
    public sealed record WtmSeedResult(int Added, int Skipped)
    {
        /// <summary>Total rows considered (<c>Added + Skipped</c>).</summary>
        public int Considered => Added + Skipped;
    }

    /// <summary>
    /// Idempotent bulk seeding for EF Core entities keyed by a caller-
    /// supplied business key (email, product SKU, tenant code, …).
    /// Re-running the same seed call yields zero inserts — making it
    /// safe to invoke on every dev/demo startup, in a test fixture, or
    /// from a migration job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contract:
    /// </para>
    /// <list type="bullet">
    /// <item>The first call inserts every row whose key is not already
    /// present; subsequent calls with overlapping keys skip the
    /// overlap and only insert genuinely-new rows.</item>
    /// <item>Existence is resolved with a <b>single</b> DB query (one
    /// <c>SELECT … WHERE Key IN (…)</c> round trip) rather than
    /// per-item, so seeding a fixture of hundreds of rows is cheap.</item>
    /// <item><see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// is called at most once — only when at least one new row was
    /// added, keeping audit interceptors / SaveChanges filters
    /// predictable.</item>
    /// <item>Duplicate keys inside <paramref name="items"/> are
    /// collapsed: only the <b>first</b> occurrence is inserted; later
    /// occurrences of the same key are counted as skipped.</item>
    /// </list>
    /// </remarks>
    public static class WtmDataSeeder
    {
        /// <summary>
        /// Seeds <paramref name="items"/> into <paramref name="context"/>,
        /// skipping any entity whose business key (per
        /// <paramref name="keySelector"/>) is already present in the
        /// table or was already inserted earlier in this call.
        /// </summary>
        /// <typeparam name="TEntity">Entity type.</typeparam>
        /// <typeparam name="TKey">Business-key type. Must be one that
        /// EF Core can project and compare in the database —
        /// <see cref="string"/>, <see cref="int"/>, <see cref="Guid"/>,
        /// and similar value types all work; a complex / composite key
        /// requires a helper property or a deterministic encoding.</typeparam>
        /// <exception cref="ArgumentNullException">When any required
        /// argument is null.</exception>
        public static async Task<WtmSeedResult> SeedAsync<TEntity, TKey>(
            DbContext context,
            IEnumerable<TEntity> items,
            Expression<Func<TEntity, TKey>> keySelector,
            CancellationToken cancellationToken = default)
            where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(keySelector);

            // Materialize once — callers may pass lazy sources (LINQ
            // pipelines, Select(...)) we don't want to enumerate twice.
            var list = items as IList<TEntity> ?? items.ToList();
            if (list.Count == 0)
            {
                return new WtmSeedResult(0, 0);
            }

            var compiledKey = keySelector.Compile();

            // Collapse duplicate keys inside the input — first occurrence
            // wins. A dev who wrote `new[] { userA, userA }` should get
            // one row, not a primary-key conflict.
            var seenInBatch = new HashSet<TKey>();
            var firstOccurrence = new List<TEntity>(list.Count);
            int dupInBatch = 0;
            foreach (var item in list)
            {
                var key = compiledKey(item);
                if (seenInBatch.Add(key))
                {
                    firstOccurrence.Add(item);
                }
                else
                {
                    dupInBatch++;
                }
            }

            // Single round-trip to resolve "which of these keys are
            // already in the table?" — cheaper than N EXISTS queries.
            var desiredKeys = firstOccurrence.Select(compiledKey).ToList();
            var set = context.Set<TEntity>();
            var existingKeys = (await set
                    .Select(keySelector)
                    .Where(k => desiredKeys.Contains(k))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .ToHashSet();

            int added = 0;
            int skipped = dupInBatch;
            foreach (var item in firstOccurrence)
            {
                var key = compiledKey(item);
                if (existingKeys.Contains(key))
                {
                    skipped++;
                    continue;
                }
                await set.AddAsync(item, cancellationToken).ConfigureAwait(false);
                added++;
            }

            if (added > 0)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return new WtmSeedResult(added, skipped);
        }
    }
}
