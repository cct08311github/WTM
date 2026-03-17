#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// An <see cref="IEtlSource"/> that unions data from multiple inner sources.
/// Each inner source is paired with its own connection string and query template,
/// so heterogeneous databases can be merged in a single ETL job.
///
/// Usage:
/// <code>
/// var composite = new CompositeEtlSource(new[]
/// {
///     ("Server=db1;...", "SELECT * FROM Orders",  new MssqlSource()),
///     ("Server=db2;...", "SELECT * FROM Archive", new MssqlSource()),
/// });
/// </code>
///
/// Note: all inner sources must produce compatible schemas (same column names and types)
/// for the downstream <see cref="IBulkLoader"/> to merge them correctly.
/// </summary>
public sealed class CompositeEtlSource : IEtlSource
{
    private readonly IReadOnlyList<(string ConnectionString, string QueryTemplate, IEtlSource Source)> _sources;

    public CompositeEtlSource(
        IEnumerable<(string ConnectionString, string QueryTemplate, IEtlSource Source)> sources)
    {
        _sources = new List<(string, string, IEtlSource)>(sources);
    }

    /// <summary>
    /// Yields all batches from each inner source in declaration order.
    /// The top-level <paramref name="connectionString"/> and <paramref name="queryTemplate"/>
    /// parameters are ignored — each inner source uses its own values.
    /// </summary>
    public async IAsyncEnumerable<DataTable> ExtractBatchesAsync(
        string connectionString,
        string queryTemplate,
        object? watermarkValue,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (cs, query, source) in _sources)
        {
            await foreach (var batch in source.ExtractBatchesAsync(
                cs, query, watermarkValue, batchSize, cancellationToken))
            {
                yield return batch;
            }
        }
    }

    public void Dispose()
    {
        foreach (var (_, _, source) in _sources)
            source.Dispose();
    }
}
