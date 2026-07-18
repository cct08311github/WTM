#nullable enable
using System;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class EfSetBenchEntity : TopBasePoco
    {
    }

    public class EfSetBenchDataContext
    {
        // Mirrors the shape DbContext.Set<T>() exposes: a parameterless generic method
        // named "Set". EfSetMethodCache.GetClosedSetMethod resolves this by name + arity,
        // so a real IDataContext isn't required to exercise the caching behaviour.
        public IQueryable<T> Set<T>() where T : class => throw new NotSupportedException();
    }

    /// <summary>
    /// Perf(#705): <c>BaseCRUDVM</c>'s soft-relation resolution (DoEdit/DoAdd/IncludeInfo)
    /// previously called
    /// <c>DC.GetType().GetMethod("Set", Type.EmptyTypes)!.MakeGenericMethod(entityType)</c> —
    /// a reflection method lookup PLUS a closed-generic build — for every sub-collection
    /// property, on every save. <c>Uncached_GetMethodPlusMakeGeneric</c> reproduces that
    /// pre-#705 shape; <c>Cached_EfSetMethodCache</c> calls the REAL
    /// <see cref="EfSetMethodCache.GetClosedSetMethod"/> (internal, exposed to this project
    /// via <c>InternalsVisibleTo</c>) — the exact cache shipped in the fix.
    /// </summary>
    [ShortRunJob]
    [MemoryDiagnoser]
    public class EfSetMethodCacheBenchmarks
    {
        private static readonly Type DcType = typeof(EfSetBenchDataContext);
        private static readonly Type EntityType = typeof(EfSetBenchEntity);

        [GlobalSetup]
        public void Setup()
        {
            // Pre-warm the cache used by the Cached_* benchmark.
            EfSetMethodCache.GetClosedSetMethod(DcType, EntityType);
        }

        /// <summary>Pre-#705 shape: fresh GetMethod + MakeGenericMethod, every call.</summary>
        [Benchmark(Baseline = true)]
        public MethodInfo Uncached_GetMethodPlusMakeGeneric()
        {
            return DcType.GetMethod("Set", Type.EmptyTypes)!.MakeGenericMethod(EntityType);
        }

        /// <summary>#705 fix: the real EfSetMethodCache — cached per (dcType, entityType).</summary>
        [Benchmark]
        public MethodInfo Cached_EfSetMethodCache()
        {
            return EfSetMethodCache.GetClosedSetMethod(DcType, EntityType);
        }
    }
}
