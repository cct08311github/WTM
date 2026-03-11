#nullable enable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;
using OracleSource = WalkingTec.Mvvm.Etl.Pipeline.Sources.OracleSource;
using OracleBulkLoader = WalkingTec.Mvvm.Etl.Pipeline.Loaders.OracleBulkLoader;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Etl.Test.Scheduling;

[TestClass]
public class SchedulerLogicTests
{
    private static EtlJobDefinition CreateJob(
        EtlJobStatus status = EtlJobStatus.Enabled,
        int skipCount = 0) => new()
    {
        Name = "test-job",
        CronExpression = "0 0 * * *",
        SourceCsKey = "test",
        SourceDbType = Core.DBTypeEnum.SqlServer,
        WatermarkType = EtlWatermarkType.FullLoad,
        Status = status,
        SkipCount = skipCount
    };

    // ─── ShouldExecute tests ───

    [TestMethod]
    public void ShouldExecute_Enabled_returns_true()
    {
        var job = CreateJob(EtlJobStatus.Enabled);
        Assert.IsTrue(EtlSchedulerService.ShouldExecute(job));
    }

    [TestMethod]
    public void ShouldExecute_Failed_returns_true()
    {
        // Failed jobs should be retried on next trigger
        var job = CreateJob(EtlJobStatus.Failed);
        Assert.IsTrue(EtlSchedulerService.ShouldExecute(job));
    }

    [TestMethod]
    public void ShouldExecute_Disabled_returns_false()
    {
        var job = CreateJob(EtlJobStatus.Disabled);
        Assert.IsFalse(EtlSchedulerService.ShouldExecute(job));
    }

    [TestMethod]
    public void ShouldExecute_Paused_returns_false()
    {
        var job = CreateJob(EtlJobStatus.Paused);
        Assert.IsFalse(EtlSchedulerService.ShouldExecute(job));
    }

    [TestMethod]
    public void ShouldExecute_Running_returns_false()
    {
        var job = CreateJob(EtlJobStatus.Running);
        Assert.IsFalse(EtlSchedulerService.ShouldExecute(job));
    }

    [TestMethod]
    public void ShouldExecute_SkipCount_positive_returns_false()
    {
        var job = CreateJob(EtlJobStatus.Enabled, skipCount: 2);
        Assert.IsFalse(EtlSchedulerService.ShouldExecute(job));
    }

    [TestMethod]
    public void ShouldExecute_SkipCount_zero_Enabled_returns_true()
    {
        var job = CreateJob(EtlJobStatus.Enabled, skipCount: 0);
        Assert.IsTrue(EtlSchedulerService.ShouldExecute(job));
    }

    // ─── EtlSourceFactory tests ───

    [TestMethod]
    public void EtlSourceFactory_CreateSource_SqlServer_returns_MssqlSource()
    {
        using var source = EtlSourceFactory.CreateSource(Core.DBTypeEnum.SqlServer);
        Assert.IsInstanceOfType(source, typeof(MssqlSource));
    }

    [TestMethod]
    [ExpectedException(typeof(NotSupportedException))]
    public void EtlSourceFactory_CreateSource_unsupported_throws()
    {
        EtlSourceFactory.CreateSource(Core.DBTypeEnum.SQLite);
    }

    [TestMethod]
    public void EtlSourceFactory_CreateLoader_SqlServer_returns_MssqlBulkLoader()
    {
        var loader = EtlSourceFactory.CreateLoader(Core.DBTypeEnum.SqlServer);
        Assert.IsInstanceOfType(loader, typeof(MssqlBulkLoader));
    }

    [TestMethod]
    [ExpectedException(typeof(NotSupportedException))]
    public void EtlSourceFactory_CreateLoader_unsupported_throws()
    {
        EtlSourceFactory.CreateLoader(Core.DBTypeEnum.SQLite);
    }

    [TestMethod]
    public void EtlSourceFactory_CreateSource_Oracle_returns_OracleSource()
    {
        using var source = EtlSourceFactory.CreateSource(Core.DBTypeEnum.Oracle);
        Assert.IsInstanceOfType(source, typeof(OracleSource));
    }

    [TestMethod]
    public void EtlSourceFactory_CreateLoader_Oracle_returns_OracleBulkLoader()
    {
        var loader = EtlSourceFactory.CreateLoader(Core.DBTypeEnum.Oracle);
        Assert.IsInstanceOfType(loader, typeof(OracleBulkLoader));
    }
}
