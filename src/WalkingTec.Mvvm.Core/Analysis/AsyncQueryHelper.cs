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
                : Task.FromResult(query.ToList());

        internal static Task<int> SafeCountAsync<T>(IQueryable<T> query, CancellationToken ct)
            => query is IAsyncEnumerable<T>
                ? query.CountAsync(ct)
                : Task.FromResult(query.Count());
    }
}
