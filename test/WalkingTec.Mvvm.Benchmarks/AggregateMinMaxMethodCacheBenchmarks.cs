#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class AggregateBenchEntity
    {
        public int IntCol { get; set; }
    }

    /// <summary>
    /// Perf(#705): <c>BasePagedListVM&lt;TModel,TSearcher&gt;.ComputeMinMax</c> previously
    /// re-resolved the open <c>Queryable.Select</c>/<c>Min</c> <see cref="MethodInfo"/> via
    /// <c>typeof(Queryable).GetMethods().First(...)</c> (a full reflection scan of every
    /// method on <see cref="System.Linq.Queryable"/>) and called
    /// <c>.MakeGenericMethod(...)</c> for EVERY aggregate column, on EVERY request.
    /// <c>Uncached_ScanAndMakeGeneric</c> reproduces that pre-#705 shape;
    /// <c>Cached_OpenMethodPlusClosedCache</c> reproduces the fix — resolve the open
    /// <c>MethodInfo</c> once (static) and cache the closed-generic <c>MethodInfo</c> per
    /// column property <see cref="Type"/> in a <see cref="ConcurrentDictionary{TKey,TValue}"/>,
    /// mirroring the real cache shape added to <c>BasePagedListVM.Aggregates.cs</c>.
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class AggregateMinMaxMethodCacheBenchmarks
    {
        private static readonly MethodInfo s_openSelectMethod =
            typeof(Queryable).GetMethods().First(m => m.Name == "Select" && m.GetParameters().Length == 2);

        private static readonly MethodInfo s_openMinMethod =
            typeof(Queryable).GetMethods().First(m => m.Name == "Min" && m.GetParameters().Length == 1);

        private static readonly ConcurrentDictionary<Type, MethodInfo> s_selectMethodCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo> s_minMethodCache = new();

        [GlobalSetup]
        public void Setup()
        {
            // Pre-warm the caches used by the Cached_* benchmark.
            s_selectMethodCache.GetOrAdd(typeof(int), t => s_openSelectMethod.MakeGenericMethod(typeof(AggregateBenchEntity), t));
            s_minMethodCache.GetOrAdd(typeof(int), t => s_openMinMethod.MakeGenericMethod(t));
        }

        /// <summary>Pre-#705 shape: full <c>Queryable</c> method scan + MakeGenericMethod, every call.</summary>
        [Benchmark(Baseline = true)]
        public MethodInfo Uncached_ScanAndMakeGeneric()
        {
            var selectMethod = typeof(Queryable)
                .GetMethods()
                .First(m => m.Name == "Select" && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(AggregateBenchEntity), typeof(int));

            var minMethod = typeof(Queryable)
                .GetMethods()
                .First(m => m.Name == "Min" && m.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(int));

            return minMethod ?? selectMethod;
        }

        /// <summary>#705 fix: open MethodInfo resolved once, closed-generic MethodInfo cached per Type.</summary>
        [Benchmark]
        public MethodInfo Cached_OpenMethodPlusClosedCache()
        {
            var selectMethod = s_selectMethodCache.GetOrAdd(
                typeof(int),
                t => s_openSelectMethod.MakeGenericMethod(typeof(AggregateBenchEntity), t));

            var minMethod = s_minMethodCache.GetOrAdd(typeof(int), t => s_openMinMethod.MakeGenericMethod(t));

            return minMethod ?? selectMethod;
        }
    }
}
