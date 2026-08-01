#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core.Extensions
{
    /// <summary>
    /// Issue #824: the ONE batched, tenant-scoped <see cref="FileAttachment"/> id resolution
    /// query, shared so <see cref="BaseCRUDVM{TModel}"/>'s own
    /// <c>ResolveFileAttachmentIdsForCaller</c>/<c>Async</c> (Issue #815/#828) and
    /// <see cref="WalkingTec.Mvvm.Core.FileAttachmentSaveChangesGuard"/>'s SaveChanges-level
    /// backstop (Issue #824) never re-derive their own version of the same query. Extracted here
    /// specifically because two independently-written implementations of "does this id resolve
    /// under the caller's own tenant scope" would inevitably drift — see
    /// <see cref="DCExtension.IsFileAttachmentForeignKeyProperty(IDataContext?, Type?, string?)"/>'s
    /// doc comment for the same rationale applied to the FK-shape predicate.
    /// </summary>
    public static partial class DCExtension
    {
        /// <summary>
        /// Issue #828: SQL Server's hard per-query parameter cap is 2100. EF Core 10 reverted the
        /// default parameterized-<c>Contains()</c> translation back to one scalar parameter PER
        /// candidate id (see <see href="https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/breaking-changes#parameterized-collections-now-use-multiple-parameters-by-default"/>),
        /// so a resolution query built from more candidates than this can throw purely from its
        /// own size. Batching removes that as a routine failure mode; it does not by itself make
        /// an unrelated resolution failure (timeout, connection drop, ...) safe — that safety
        /// comes from every caller of <see cref="ResolveFileAttachmentIds"/>/<see cref="ResolveFileAttachmentIdsAsync"/>
        /// treating <see cref="FileAttachmentResolutionOutcome.Succeeded"/> == <see langword="false"/>
        /// as "unknown", never "unresolvable", and rejecting the whole write rather than narrowing.
        /// </summary>
        public const int FileAttachmentResolutionBatchSize = 500;

        /// <summary>
        /// The result of a batched <see cref="FileAttachment"/> id resolution attempt.
        /// <see cref="Succeeded"/> is <see langword="false"/> only when the resolution QUERY
        /// itself threw — never when it ran fine and simply found fewer rows than candidates.
        /// Callers must treat a failed resolution as "unknown, reject the whole write", never as
        /// "these candidates are unresolvable, narrow to the rest" (Issue #828).
        /// </summary>
        public readonly record struct FileAttachmentResolutionOutcome(bool Succeeded, HashSet<Guid> ResolvedIds, Exception? Failure);

        /// <summary>
        /// Resolves every id in <paramref name="candidateIds"/> to the set of ids that exist as a
        /// <see cref="FileAttachment"/> row (including any TPH/TPT-derived subclass row — the
        /// query is against the base <see cref="FileAttachment"/> DbSet, which both mapping
        /// strategies expose every row through) under the CALLER's own tenant scope — the
        /// <c>ITenant</c> global query filter kept ON unconditionally, no
        /// <c>IgnoreQueryFilters()</c> — batched into <see cref="FileAttachmentResolutionBatchSize"/>-sized
        /// queries. On the FIRST batch that throws, resolution stops immediately and reports
        /// failure: a partially-resolved set from batches that succeeded before the failure is
        /// deliberately discarded, never returned, since every caller ignores
        /// <see cref="FileAttachmentResolutionOutcome.ResolvedIds"/> when
        /// <see cref="FileAttachmentResolutionOutcome.Succeeded"/> is <see langword="false"/> and
        /// keeping it around risks a future caller mistaking "resolved so far" for "resolved,
        /// full stop".
        /// </summary>
        public static FileAttachmentResolutionOutcome ResolveFileAttachmentIds(this IDataContext self, ICollection<Guid> candidateIds)
        {
            if (candidateIds.Count == 0)
            {
                return new FileAttachmentResolutionOutcome(true, [], null);
            }
            var resolved = new HashSet<Guid>();
            try
            {
                foreach (var batch in candidateIds.Chunk(FileAttachmentResolutionBatchSize))
                {
                    foreach (var id in self.Set<FileAttachment>().Where(x => batch.Contains(x.ID)).Select(x => x.ID))
                    {
                        resolved.Add(id);
                    }
                }
                return new FileAttachmentResolutionOutcome(true, resolved, null);
            }
            catch (OperationCanceledException)
            {
                // Issue #824 adversarial review Finding 5: a caller-requested (or timeout-driven)
                // cancellation is not a resolution FAILURE — converting it into
                // FileAttachmentResolutionOutcome.Succeeded == false would launder an
                // OperationCanceledException into an UnresolvableFileAttachmentReferenceException,
                // making an ordinary cancellation look, to every caller and to the client, like a
                // rejected forged FK (a security-shaped failure it is not). Let it propagate as
                // itself; the sync overload has no CancellationToken of its own, but the underlying
                // ADO.NET provider still honours a command/connection timeout via
                // OperationCanceledException, so this catch is reachable here too.
                throw;
            }
            catch (Exception ex)
            {
                return new FileAttachmentResolutionOutcome(false, [], ex);
            }
        }

        /// <summary>
        /// Async counterpart of <see cref="ResolveFileAttachmentIds"/> — same batched queries,
        /// awaited instead of run synchronously so an async SaveChanges/DoAdd/DoEdit path never
        /// blocks a ThreadPool thread on it.
        /// </summary>
        public static async Task<FileAttachmentResolutionOutcome> ResolveFileAttachmentIdsAsync(this IDataContext self, ICollection<Guid> candidateIds, CancellationToken cancellationToken = default)
        {
            if (candidateIds.Count == 0)
            {
                return new FileAttachmentResolutionOutcome(true, [], null);
            }
            var resolved = new HashSet<Guid>();
            try
            {
                foreach (var batch in candidateIds.Chunk(FileAttachmentResolutionBatchSize))
                {
                    foreach (var id in await self.Set<FileAttachment>().Where(x => batch.Contains(x.ID)).Select(x => x.ID).ToListAsync(cancellationToken).ConfigureAwait(false))
                    {
                        resolved.Add(id);
                    }
                }
                return new FileAttachmentResolutionOutcome(true, resolved, null);
            }
            catch (OperationCanceledException)
            {
                // Issue #824 adversarial review Finding 5: see the sync overload's matching catch
                // above for the full rationale. This is the path that actually fires in practice —
                // ToListAsync(cancellationToken) throws OperationCanceledException directly when
                // the token is signalled, and the bare `catch (Exception ex)` below would otherwise
                // convert a plain cancellation into a security-shaped rejection.
                throw;
            }
            catch (Exception ex)
            {
                return new FileAttachmentResolutionOutcome(false, [], ex);
            }
        }
    }
}
