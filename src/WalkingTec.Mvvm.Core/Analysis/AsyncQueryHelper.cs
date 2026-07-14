#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core.Analysis
{
    internal static class AsyncQueryHelper
    {
        internal static Task<List<T>> SafeToListAsync<T>(IQueryable<T> query, CancellationToken ct)
            => query is IAsyncEnumerable<T>
                ? query.ToListAsync(ct)
                : Task.FromResult(ToListRespectingCancellation(query, ct));

        internal static Task<int> SafeCountAsync<T>(IQueryable<T> query, CancellationToken ct)
        {
            if (query is IAsyncEnumerable<T>)
                return query.CountAsync(ct);

            ct.ThrowIfCancellationRequested();
            return Task.FromResult(query.Count());
        }

        // Non-EF (e.g. in-memory List-backed) IQueryable sources have no async execution
        // path, so query.ToList() would otherwise run to completion regardless of an
        // already-cancelled token — silently handing back a full result to a caller that
        // asked to be cancelled (Issue #662: Dashboard widget async migration surfaced this
        // via CFO_CancellationToken_CancelledQuery_ThrowsNotReturnsPartialData). Mirrors the
        // per-item ThrowIfCancellationRequested() check already used by the sync Execute
        // strategies (InProcessGroupByStrategy/ServerSideGroupByStrategy) so sync and async
        // cancellation behaviour stay identical.
        private static List<T> ToListRespectingCancellation<T>(IQueryable<T> query, CancellationToken ct)
        {
            var result = new List<T>();
            foreach (var item in query)
            {
                ct.ThrowIfCancellationRequested();
                result.Add(item);
            }
            return result;
        }
    }
}
