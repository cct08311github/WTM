#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
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

    [TestMethod]
    public void Identity_int_column_updates_watermark()
    {
        // SQL Server INT 欄位從 DataReader 讀出為 int（非 long），必須正確處理
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", null);

        wm.UpdateFromBatchMax((int)99999);
        var committed = wm.CommitPendingValue();

        committed.Should().NotBeNullOrEmpty();
        var stored = JsonSerializer.Deserialize<long>(committed!);
        stored.Should().Be(99999L);
    }

    [TestMethod]
    public void Identity_long_column_updates_watermark()
    {
        // BIGINT 欄位回傳 long，需照常運作
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", null);

        wm.UpdateFromBatchMax(185600L);
        var committed = wm.CommitPendingValue();

        committed.Should().NotBeNullOrEmpty();
        var stored = JsonSerializer.Deserialize<long>(committed!);
        stored.Should().Be(185600L);
    }

    [TestMethod]
    public void Identity_unsupported_type_does_not_crash()
    {
        // 傳入不支援型別（例如 string）不應拋出，只是靜默忽略
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", null);

        wm.UpdateFromBatchMax("not-a-number");
        wm.CommitPendingValue().Should().BeNull();
    }

    // ── #485: Broadened Identity coercion (decimal / short / byte) ──────────

    [TestMethod]
    public void Identity_decimal_column_updates_watermark()
    {
        // Oracle NUMBER 欄位從 DataReader 讀出為 decimal；必須正確推進 watermark
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", null);

        wm.UpdateFromBatchMax(42_000_000m);
        var committed = wm.CommitPendingValue();

        committed.Should().NotBeNullOrEmpty();
        var stored = JsonSerializer.Deserialize<long>(committed!);
        stored.Should().Be(42_000_000L);
    }

    [TestMethod]
    public void Identity_short_column_updates_watermark()
    {
        // SMALLINT 欄位回傳 short；必須正確推進 watermark
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "SeqId", null);

        wm.UpdateFromBatchMax((short)32_000);
        var committed = wm.CommitPendingValue();

        committed.Should().NotBeNullOrEmpty();
        var stored = JsonSerializer.Deserialize<long>(committed!);
        stored.Should().Be(32_000L);
    }

    [TestMethod]
    public void Identity_byte_column_updates_watermark()
    {
        // TINYINT 欄位回傳 byte；必須正確推進 watermark
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "StepId", null);

        wm.UpdateFromBatchMax((byte)200);
        var committed = wm.CommitPendingValue();

        committed.Should().NotBeNullOrEmpty();
        var stored = JsonSerializer.Deserialize<long>(committed!);
        stored.Should().Be(200L);
    }

    // ── #485 correctness fix: null / DBNull must NOT advance watermark and must NOT warn ──

    [TestMethod]
    public void Identity_null_max_does_not_advance_and_does_not_warn()
    {
        // 空批次或 watermark 欄位 max 為 null 時，CurrentValue 應保持不變，且不記錄警告
        var loggedLevels = new List<LogLevel>();
        var mockLogger = new Mock<ILogger>();
        mockLogger
            .Setup(l => l.IsEnabled(It.IsAny<LogLevel>()))
            .Returns(true);
        mockLogger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((level, _, _, _, _) =>
                loggedLevels.Add(level));

        var originalValue = System.Text.Json.JsonSerializer.Serialize(500L);
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", originalValue, logger: mockLogger.Object);

        wm.UpdateFromBatchMax(null!);

        // watermark must NOT advance
        wm.CommitPendingValue().Should().Be(originalValue,
            "a null batch max (empty batch) must not reset the watermark");

        // no warning should be logged
        loggedLevels.Should().NotContain(
            l => l >= LogLevel.Warning,
            "a null max is normal 'no new rows' — it is not a misconfiguration");
    }

    [TestMethod]
    public void Identity_DBNull_max_does_not_advance_and_does_not_warn()
    {
        // ADO.NET DataReader 在欄位為 NULL 時回傳 DBNull.Value；行為應與 null 相同
        var loggedLevels = new List<LogLevel>();
        var mockLogger = new Mock<ILogger>();
        mockLogger
            .Setup(l => l.IsEnabled(It.IsAny<LogLevel>()))
            .Returns(true);
        mockLogger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((level, _, _, _, _) =>
                loggedLevels.Add(level));

        var originalValue = System.Text.Json.JsonSerializer.Serialize(500L);
        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", originalValue, logger: mockLogger.Object);

        wm.UpdateFromBatchMax(DBNull.Value);

        // watermark must NOT advance
        wm.CommitPendingValue().Should().Be(originalValue,
            "a DBNull batch max must not reset the watermark");

        // no warning should be logged
        loggedLevels.Should().NotContain(
            l => l >= LogLevel.Warning,
            "DBNull.Value from an ADO.NET reader is normal 'no new rows' — not a misconfiguration");
    }

    [TestMethod]
    public void Identity_uncoercible_value_logs_warning_and_does_not_advance()
    {
        // 傳入無法轉換的值（string）應記錄 Warning 且不應推進 watermark（不崩潰）
        var loggedMessages = new List<string>();
        var mockLogger = new Mock<ILogger>();
        mockLogger
            .Setup(l => l.IsEnabled(It.IsAny<LogLevel>()))
            .Returns(true);
        mockLogger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((level, _, state, _, formatter) =>
            {
                if (level >= LogLevel.Warning)
                    loggedMessages.Add(formatter.DynamicInvoke(state, null) as string ?? string.Empty);
            });

        var wm = new WatermarkStrategy(EtlWatermarkType.Identity, "OrderId", null, logger: mockLogger.Object);

        wm.UpdateFromBatchMax("not-a-number");

        // watermark must NOT advance
        wm.CommitPendingValue().Should().BeNull();

        // a warning must have been emitted
        loggedMessages.Should().ContainMatch(
            "*Identity watermark coercion*",
            "a structured warning message should be logged when the value cannot be coerced to long");
    }
}
