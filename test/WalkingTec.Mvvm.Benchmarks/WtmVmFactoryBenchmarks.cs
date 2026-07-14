#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class SampleVm : BaseVM
    {
    }

    /// <summary>
    /// Perf(#663): WtmVmFactory.CreateVM used to call
    /// <c>vmType.GetConstructor(Type.EmptyTypes)?.Invoke(null)</c> - uncached reflection
    /// invoke - on every VM creation (every WTM request, plus once per nested sub-VM).
    /// Uncached_* below inlines that original shape; Cached_* mirrors the fixed
    /// approach (an Expression.New delegate compiled once and cached in a
    /// ConcurrentDictionary&lt;Type, Func&lt;object&gt;&gt; keyed by Type, the same
    /// technique WtmVmFactory now uses internally).
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class WtmVmFactoryBenchmarks
    {
        private static readonly ConcurrentDictionary<Type, Func<object>?> _cache = new();

        private static Func<object>? GetOrAddCtorFactory(Type type)
        {
            return _cache.GetOrAdd(type, static t =>
            {
                var ctor = t.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    return null;
                }
                return Expression.Lambda<Func<object>>(Expression.New(ctor)).Compile();
            });
        }

        [GlobalSetup]
        public void Setup()
        {
            // Pre-warm the cache used by Cached_CreateInstance
            GetOrAddCtorFactory(typeof(SampleVm));
        }

        [Benchmark(Baseline = true)]
        public object Uncached_CreateInstance()
        {
            // Inline original logic (pre-#663 fix): reflection GetConstructor + Invoke
            // on every call, no caching across calls.
            var ctor = typeof(SampleVm).GetConstructor(Type.EmptyTypes);
            return ctor!.Invoke(null);
        }

        [Benchmark]
        public object Cached_CreateInstance()
        {
            return GetOrAddCtorFactory(typeof(SampleVm))!.Invoke();
        }
    }
}
