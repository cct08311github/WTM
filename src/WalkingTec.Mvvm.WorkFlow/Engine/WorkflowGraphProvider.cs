#nullable enable
// #666: default IWorkflowGraphProvider — ConcurrentDictionary-backed deserialize cache.

using System.Collections.Concurrent;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Default <see cref="IWorkflowGraphProvider"/>. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// — the same pattern <c>WhitelistRoutingEvaluator</c> uses for its compiled-predicate cache.
///
/// <para>Registered as a singleton by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/> so the
/// cache is shared across every scoped <c>WorkflowEngine</c>/<c>WorkflowTimerExecutor</c> instance
/// for the lifetime of the process.</para>
/// </summary>
internal sealed class WorkflowGraphProvider : IWorkflowGraphProvider
{
    /// <summary>
    /// Trivial bounded-size guard (per #666 design note: "versions are few"). Published
    /// <see cref="ProcessDefinitionVersion"/> rows are immutable and created only at publish
    /// time — this cap is a defensive backstop against unbounded process-lifetime growth, not
    /// an expected steady state. On overflow the cache simply stops accepting NEW entries;
    /// callers still get a correctly deserialized graph, just without the cache benefit for
    /// versions beyond the cap. Existing cached entries are never evicted (no LRU — versions
    /// already in cache stay cheap, which is the common case: a handful of hot definitions).
    /// </summary>
    private const int MaxCachedGraphs = 10_000;

    private readonly ConcurrentDictionary<System.Guid, WorkflowGraph> _cache = new();

    /// <inheritdoc/>
    public WorkflowGraph GetGraph(ProcessDefinitionVersion version)
    {
        if (_cache.TryGetValue(version.ID, out var cached))
            return cached;

        // Cache miss: deserialize. GraphJson is immutable per version (spec invariant), so the
        // result is safe to cache and hand out to every future caller of this DefinitionVersionId.
        var graph = WorkflowGraphSerializer.Deserialize(version.GraphJson);

        if (_cache.Count >= MaxCachedGraphs)
            return graph; // bounded-size guard — skip caching, still return a correct result

        // GetOrAdd: if a concurrent caller raced us and already inserted, we discard our extra
        // deserialize and return the winner's instance — both are content-identical, so which
        // one wins is immaterial; this just avoids a duplicate cache entry.
        return _cache.GetOrAdd(version.ID, graph);
    }
}
