#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// 預設的 <see cref="IEtlSourceRegistry"/> 實作。
/// 執行緒安全（使用 ConcurrentDictionary）；適合以 Singleton 生命週期注入。
/// </summary>
public sealed class EtlSourceRegistry : IEtlSourceRegistry
{
    // Keys are normalised to lower-case at registration time.
    private readonly ConcurrentDictionary<string, Func<IEtlSource>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string sourceKind, Func<IEtlSource> factory)
    {
        if (string.IsNullOrWhiteSpace(sourceKind))
            throw new ArgumentException("sourceKind must not be null or whitespace.", nameof(sourceKind));
        ArgumentNullException.ThrowIfNull(factory);

        _factories[sourceKind.ToLowerInvariant()] = factory;
    }

    /// <inheritdoc />
    public bool TryCreate(string sourceKind, out IEtlSource? source)
    {
        if (!string.IsNullOrWhiteSpace(sourceKind) &&
            _factories.TryGetValue(sourceKind.ToLowerInvariant(), out var factory))
        {
            source = factory();
            return true;
        }

        source = null;
        return false;
    }

    /// <inheritdoc />
    public IEtlSource Create(string sourceKind)
    {
        if (TryCreate(sourceKind, out var src) && src is not null)
            return src;

        throw new NotSupportedException(
            $"ETL source kind '{sourceKind}' is not registered. " +
            $"Registered kinds: [{string.Join(", ", RegisteredKinds)}].");
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> RegisteredKinds =>
        (IReadOnlyCollection<string>)_factories.Keys;
}
