#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Scheduling;

/// <summary>
/// 追蹤執行中 Job 的進度（in-memory ConcurrentDictionary）。
/// 由 EtlQuartzJob 透過 IProgress 寫入，由 Monitor Controller 讀取。
/// </summary>
public class EtlProgressTracker
{
    private readonly ConcurrentDictionary<Guid, EtlProgress> _progress = new();

    public void Update(EtlProgress progress)
    {
        _progress[progress.JobId] = progress;
    }

    public EtlProgress? Get(Guid jobId)
    {
        return _progress.TryGetValue(jobId, out var p) ? p : null;
    }

    public IReadOnlyList<EtlProgress> GetAll()
    {
        return _progress.Values.ToList();
    }

    public void Remove(Guid jobId)
    {
        _progress.TryRemove(jobId, out _);
    }
}
