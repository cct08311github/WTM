#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Analysis;

/// <summary>
/// Shared reflection helper for invoking GetSearchQuery() and GetAnalysisFields()
/// on any ListVM instance. Provides consistent null guards, TargetInvocationException
/// unwrapping, and a per-type MethodInfo cache to amortise reflection cost.
/// </summary>
public static class AnalysisVmInvoker
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> _searchQueryCache = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> _analysisFieldsCache = new();

    /// <summary>
    /// Invokes GetSearchQuery() on <paramref name="vm"/> and returns the result as
    /// <see cref="IQueryable"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the method does not exist or returns null.
    /// Inner exceptions from <see cref="TargetInvocationException"/> are unwrapped and rethrown.
    /// </exception>
    public static IQueryable GetSearchQuery(BaseVM vm, Type vmType)
    {
        var method = _searchQueryCache.GetOrAdd(vmType, t =>
        {
            var m = t.GetMethod(
                "GetSearchQuery",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (m == null)
                throw new InvalidOperationException(
                    $"VM type '{t.FullName}' does not have a public GetSearchQuery() method.");
            return m;
        });

        try
        {
            var result = method.Invoke(vm, null);
            if (result is IQueryable q) return q;
            throw new InvalidOperationException(
                $"VM type '{vmType.FullName}' GetSearchQuery() did not return IQueryable.");
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>
    /// Invokes GetAnalysisFields() on <paramref name="vm"/> and returns the result as
    /// <see cref="IList{AnalysisFieldMeta}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the method does not exist or returns null.
    /// Inner exceptions from <see cref="TargetInvocationException"/> are unwrapped and rethrown.
    /// </exception>
    public static IList<AnalysisFieldMeta> GetAnalysisFields(BaseVM vm, Type vmType)
    {
        var method = _analysisFieldsCache.GetOrAdd(vmType, t =>
        {
            var m = t.GetMethod(
                "GetAnalysisFields",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (m == null)
                throw new InvalidOperationException(
                    $"VM type '{t.FullName}' does not have a public GetAnalysisFields() method.");
            return m;
        });

        try
        {
            var result = method.Invoke(vm, null) as IEnumerable<AnalysisFieldMeta>;
            if (result is null)
                throw new InvalidOperationException(
                    $"VM type '{vmType.FullName}' GetAnalysisFields() returned null.");
            return result as IList<AnalysisFieldMeta> ?? [.. result];
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }
}
