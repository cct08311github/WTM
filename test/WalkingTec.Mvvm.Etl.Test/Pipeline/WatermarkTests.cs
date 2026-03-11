#nullable enable
using System;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class WatermarkTests
{
    [TestMethod]
    public void FullLoad_always_returns_1_equals_1()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        wm.BuildWhereClause().Should().Be("1=1");
    }

    [TestMethod]
    public void Timestamp_with_null_value_returns_full_load()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);
        wm.BuildWhereClause().Should().Be("1=1");
    }

    [TestMethod]
    public void Timestamp_with_value_returns_column_gt_watermark()
    {
        var value = JsonSerializer.Serialize(new DateTime(2026, 3, 10, 2, 0, 0));
        var wm = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", value);
        wm.BuildWhereClause().Should().Be("UpdatedAt > @watermark");
    }

    [TestMethod]
    public void Identity_with_value_returns_column_gt_watermark()
    {
        var value = JsonSerializer.Serialize(185600L);
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", value);
        wm.BuildWhereClause().Should().Be("OrderId > @watermark");
    }

    [TestMethod]
    public void FullLoad_get_parameter_returns_null()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        wm.GetParameterValue().Should().BeNull();
    }

    [TestMethod]
    public void Timestamp_get_parameter_returns_datetime()
    {
        var dt = new DateTime(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc);
        var value = JsonSerializer.Serialize(dt);
        var wm = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", value);

        var param = wm.GetParameterValue();
        param.Should().BeOfType<DateTime>();
    }

    [TestMethod]
    public void Identity_get_parameter_returns_long()
    {
        var value = JsonSerializer.Serialize(185600L);
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", value);

        var param = wm.GetParameterValue();
        param.Should().BeOfType<long>();
        ((long)param!).Should().Be(185600L);
    }

    [TestMethod]
    public void CommitPendingValue_updates_current()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null);
        var dt = new DateTime(2026, 3, 11, 2, 0, 0, DateTimeKind.Utc);
        wm.UpdateFromBatchMax(dt);

        var committed = wm.CommitPendingValue();

        committed.Should().NotBeNullOrEmpty();
        wm.CurrentValue.Should().Be(committed);
    }

    [TestMethod]
    public void DiscardPendingValue_keeps_current()
    {
        var original = JsonSerializer.Serialize(new DateTime(2026, 3, 10, 0, 0, 0));
        var wm = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", original);
        wm.UpdateFromBatchMax(new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc));

        wm.DiscardPendingValue();

        wm.CurrentValue.Should().Be(original);
    }

    [TestMethod]
    public void Timezone_conversion_taipei_to_utc()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.Timestamp, "UpdatedAt", null, "Asia/Taipei");

        // Taipei 10:00 = UTC 02:00
        var taipeiTime = new DateTime(2026, 3, 11, 10, 0, 0);
        wm.UpdateFromBatchMax(taipeiTime);
        var committed = wm.CommitPendingValue();

        var storedUtc = JsonSerializer.Deserialize<DateTime>(committed!);
        storedUtc.Hour.Should().Be(2);
    }

    [TestMethod]
    public void FullLoad_ignores_update_from_batch()
    {
        var wm = new WatermarkStrategy(EtlWatermarkType.FullLoad, null, null);
        wm.UpdateFromBatchMax(new DateTime(2026, 3, 11, 0, 0, 0));
        wm.CommitPendingValue().Should().BeNull();
    }
}
