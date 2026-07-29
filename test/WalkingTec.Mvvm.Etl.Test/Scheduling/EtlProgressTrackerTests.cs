#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

[TestClass]
public class EtlProgressTrackerTests
{
    private EtlProgressTracker _tracker = null!;

    [TestInitialize]
    public void Setup()
    {
        _tracker = new EtlProgressTracker();
    }

    [TestMethod]
    public void Update_and_Get_returns_latest_progress()
    {
        var jobId = Guid.NewGuid();
        var p1 = new EtlProgress { JobId = jobId, Phase = "Extract", ProcessedRows = 100 };
        var p2 = new EtlProgress { JobId = jobId, Phase = "Load", ProcessedRows = 200 };

        _tracker.Update(p1);
        _tracker.Update(p2);

        var result = _tracker.Get(jobId, callerTenantCode: null);
        Assert.IsNotNull(result);
        Assert.AreEqual("Load", result!.Phase);
        Assert.AreEqual(200, result.ProcessedRows);
    }

    [TestMethod]
    public void Get_returns_null_for_unknown_job()
    {
        Assert.IsNull(_tracker.Get(Guid.NewGuid(), callerTenantCode: null));
    }

    [TestMethod]
    public void GetAll_returns_all_tracked_jobs()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _tracker.Update(new EtlProgress { JobId = id1, Phase = "Extract" });
        _tracker.Update(new EtlProgress { JobId = id2, Phase = "Load" });

        var all = _tracker.GetAll(callerTenantCode: null);
        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public void Remove_clears_progress()
    {
        var jobId = Guid.NewGuid();
        _tracker.Update(new EtlProgress { JobId = jobId, Phase = "Extract" });

        _tracker.Remove(jobId);

        Assert.IsNull(_tracker.Get(jobId, callerTenantCode: null));
        Assert.AreEqual(0, _tracker.GetAll(callerTenantCode: null).Count);
    }

    [TestMethod]
    public void Remove_nonexistent_does_not_throw()
    {
        _tracker.Remove(Guid.NewGuid()); // should not throw
    }

    [TestMethod]
    public void Multiple_jobs_are_independent()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _tracker.Update(new EtlProgress { JobId = id1, Phase = "A", ProcessedRows = 10 });
        _tracker.Update(new EtlProgress { JobId = id2, Phase = "B", ProcessedRows = 20 });

        _tracker.Remove(id1);

        Assert.IsNull(_tracker.Get(id1, callerTenantCode: null));
        Assert.IsNotNull(_tracker.Get(id2, callerTenantCode: null));
        Assert.AreEqual(20, _tracker.Get(id2, callerTenantCode: null)!.ProcessedRows);
    }
}
