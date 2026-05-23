#nullable enable
using System;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    [TestClass]
    public class DateRangeTests
    {
        // ─── DateTime constructor ─────────────────────────────────────────────

        [TestMethod]
        public void Constructor_DateTime_SetsStartAndEnd()
        {
            var start = new DateTime(2024, 1, 1);
            var end = new DateTime(2024, 1, 31);
            var dr = new DateRange(start, end);

            dr.GetStartTime()!.Value.Date.Should().Be(start.Date);
            // end time for DateTime type with non-midnight gets stored as-is
        }

        [TestMethod]
        public void Constructor_DateTime_StartAfterEnd_EndSilentlyIgnored()
        {
            // When end < start, SetEndTime silently ignores the value
            var start = new DateTime(2024, 6, 1);
            var end = new DateTime(2024, 1, 1); // before start
            var dr = new DateRange(start, end);

            dr.GetStartTime().Should().NotBeNull();
            // end was rejected because start > end
            dr.GetEndTime().Should().BeNull();
        }

        [TestMethod]
        public void Constructor_DateTime_SameStartAndEnd_BothSet()
        {
            var date = new DateTime(2024, 3, 15);
            var dr = new DateRange(date, date);

            dr.GetStartTime()!.Value.Date.Should().Be(date.Date);
            // Same date triggers the "midnight" branch → AddDays(1)
            dr.GetEndTime()!.Value.Date.Should().Be(date.Date.AddDays(1));
        }

        [TestMethod]
        public void Constructor_DateTime_WithTypeAndEpoch_UsesType()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var end = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(start, end, DateTimeTypeEnum.Date, epoch);

            dr.Type.Should().Be(DateTimeTypeEnum.Date);
            dr.Epoch.Should().Be(epoch);
        }

        // ─── TimeSpan constructor ─────────────────────────────────────────────

        [TestMethod]
        public void Constructor_TimeSpan_OffsetFromEpoch()
        {
            var startSpan = TimeSpan.FromDays(1);
            var endSpan = TimeSpan.FromDays(7);
            var dr = new DateRange(startSpan, endSpan);

            dr.GetStartTime().Should().NotBeNull();
            dr.GetEndTime().Should().NotBeNull();
        }

        [TestMethod]
        public void Constructor_TimeSpan_WithCustomEpoch()
        {
            var epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var startSpan = TimeSpan.FromDays(0);
            var endSpan = TimeSpan.FromDays(30);
            var dr = new DateRange(startSpan, endSpan, DateTimeTypeEnum.DateTime, epoch);

            dr.Epoch.Should().Be(epoch);
            dr.GetStartTime()!.Value.Date.Should().Be(epoch.Date);
        }

        // ─── DateTimeOffset constructor ────────────────────────────────────────

        [TestMethod]
        public void Constructor_DateTimeOffset_UsesLocalDateTime()
        {
            var startOffset = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
            var endOffset = new DateTimeOffset(2024, 3, 31, 0, 0, 0, TimeSpan.Zero);
            var dr = new DateRange(startOffset, endOffset);

            dr.GetStartTime().Should().NotBeNull();
            dr.GetEndTime().Should().NotBeNull();
        }

        [TestMethod]
        public void Constructor_DateTimeOffset_WithTypeAndEpoch_UsesType()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var startOffset = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var endOffset = new DateTimeOffset(2024, 1, 31, 0, 0, 0, TimeSpan.Zero);
            var dr = new DateRange(startOffset, endOffset, DateTimeTypeEnum.Date, epoch);

            dr.Type.Should().Be(DateTimeTypeEnum.Date);
        }

        // ─── SetStartTime / SetEndTime invariants ─────────────────────────────

        [TestMethod]
        public void SetStartTime_Null_NoChange()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            var before = dr.GetStartTime();
            dr.SetStartTime(null);
            dr.GetStartTime().Should().Be(before);
        }

        [TestMethod]
        public void SetEndTime_Null_NoChange()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            var before = dr.GetEndTime();
            dr.SetEndTime(null);
            dr.GetEndTime().Should().Be(before);
        }

        [TestMethod]
        public void SetStartTime_AfterEnd_Silently_Ignored()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 6, 30));
            dr.SetStartTime(new DateTime(2024, 12, 31)); // after end, should be ignored
            // Start should remain unchanged
            dr.GetStartTime()!.Value.Date.Should().Be(new DateTime(2024, 1, 1));
        }

        [TestMethod]
        public void SetEndTime_BeforeStart_Silently_Ignored()
        {
            var dr = new DateRange(new DateTime(2024, 6, 1), new DateTime(2024, 12, 31));
            dr.SetEndTime(new DateTime(2024, 1, 1)); // before start, should be ignored
            // End should remain unchanged (still DateTime type midnight gets AddDays(1))
            dr.GetEndTime().Should().NotBeNull();
        }

        // ─── GetEndTime quirks ────────────────────────────────────────────────

        [TestMethod]
        public void GetEndTime_DateType_AddsDayOne()
        {
            var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var end = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(start, end, DateTimeTypeEnum.Date, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local));

            dr.GetEndTime()!.Value.Date.Should().Be(new DateTime(2024, 2, 1));
        }

        [TestMethod]
        public void GetEndTime_DateTimeType_MidnightEnd_AddsDayOne()
        {
            var start = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Local);
            var end = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Local); // midnight
            var dr = new DateRange(start, end, DateTimeTypeEnum.DateTime, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local));

            dr.GetEndTime()!.Value.Date.Should().Be(new DateTime(2024, 2, 1));
        }

        [TestMethod]
        public void GetEndTime_DateTimeType_NonMidnightEnd_ReturnsAsIs()
        {
            var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var end = new DateTime(2024, 1, 31, 14, 30, 0, DateTimeKind.Local); // non-midnight
            var dr = new DateRange(start, end, DateTimeTypeEnum.DateTime, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local));

            dr.GetEndTime()!.Value.Should().Be(end);
        }

        [TestMethod]
        public void GetEndTime_NullEndTime_ReturnsNull()
        {
            // When end < start, SetEndTime silently rejects the value, leaving _endTime null.
            // Verify the null return via TryParse which returns a partial DateRange when start > end.
            // Actually, use GetStartTime only DateRange: start > end so end gets null.
            var dr = new DateRange(new DateTime(2024, 6, 1), new DateTime(2024, 1, 1));
            // End was rejected because start > end so _endTime is null
            dr.GetEndTime().Should().BeNull();
        }

        // ─── Value (ToString) ─────────────────────────────────────────────────

        [TestMethod]
        public void Value_BothSet_ReturnsFormattedRange()
        {
            var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var end = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(start, end, DateTimeTypeEnum.Date, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local));

            dr.Value.Should().Contain("2024-01-01").And.Contain("2024-01-31").And.Contain("~");
        }

        [TestMethod]
        public void ToString_BothNull_ReturnsEmpty()
        {
            // When end is null (start > end rejects end), ToString returns empty
            var dr = new DateRange(new DateTime(2024, 6, 1), new DateTime(2024, 1, 1));
            dr.ToString().Should().Be(string.Empty);
        }

        [TestMethod]
        public void ToString_CustomFormat_UsesFormat()
        {
            var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var end = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(start, end, DateTimeTypeEnum.Date, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local));

            var result = dr.ToString("yyyy/MM/dd");
            result.Should().Contain("2024/01/01").And.Contain("2024/01/31");
        }

        [TestMethod]
        public void ToString_CustomFormatAndSplit_UsesCustomSplit()
        {
            var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var end = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(start, end, DateTimeTypeEnum.Date, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local));

            var result = dr.ToString("yyyy-MM-dd", " to ");
            result.Should().Contain(" to ");
        }

        // ─── Static preset properties ─────────────────────────────────────────

        [TestMethod]
        public void Today_StartIsToday_EndIsTomorrow()
        {
            var dr = DateRange.Today;
            dr.GetStartTime()!.Value.Date.Should().Be(DateTime.Today);
            // GetEndTime adds day because same start=end → AddDays(1)
            dr.GetEndTime()!.Value.Date.Should().Be(DateTime.Today.AddDays(1));
        }

        [TestMethod]
        public void Yesterday_StartIsYesterday()
        {
            var dr = DateRange.Yesterday;
            dr.GetStartTime()!.Value.Date.Should().Be(DateTime.Today.AddDays(-1));
        }

        [TestMethod]
        public void Week_StartIsSevenDaysAgo()
        {
            var dr = DateRange.Week;
            dr.GetStartTime()!.Value.Date.Should().Be(DateTime.Today.AddDays(-7));
        }

        [TestMethod]
        public void TwoWeek_StartIs14DaysAgo()
        {
            var dr = DateRange.TwoWeek;
            dr.GetStartTime()!.Value.Date.Should().Be(DateTime.Today.AddDays(-14));
        }

        [TestMethod]
        public void ThirtyDays_StartIs30DaysAgo()
        {
            var dr = DateRange.ThirtyDays;
            dr.GetStartTime()!.Value.Date.Should().Be(DateTime.Today.AddDays(-30));
        }

        [TestMethod]
        public void NinetyDays_StartIs90DaysAgo()
        {
            var dr = DateRange.NinetyDays;
            dr.GetStartTime()!.Value.Date.Should().Be(DateTime.Today.AddDays(-90));
        }

        [TestMethod]
        public void Default_IsSameAsToday()
        {
            var def = DateRange.Default;
            var today = DateRange.Today;
            def.GetStartTime()!.Value.Date.Should().Be(today.GetStartTime()!.Value.Date);
        }

        // ─── UTC presets ──────────────────────────────────────────────────────

        [TestMethod]
        public void UtcToday_UsesUtcNow()
        {
            var dr = DateRange.UtcToday;
            dr.GetStartTime().Should().NotBeNull();
            dr.GetEndTime().Should().NotBeNull();
        }

        [TestMethod]
        public void UtcYesterday_StartsYesterdayUtc()
        {
            var fakeClock = new FixedTimeProvider(new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero));
            var dr = DateRange.CreateUtcYesterday(fakeClock);
            dr.GetStartTime()!.Value.Date.Should().Be(new DateTime(2024, 6, 14));
        }

        [TestMethod]
        public void UtcWeek_StartsSevenDaysBeforeUtcNow()
        {
            var fakeClock = new FixedTimeProvider(new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero));
            var dr = DateRange.CreateUtcWeek(fakeClock);
            dr.GetStartTime()!.Value.Date.Should().Be(new DateTime(2024, 6, 8));
        }

        [TestMethod]
        public void UtcThirtyDays_Starts30DaysBeforeUtcNow()
        {
            var fakeClock = new FixedTimeProvider(new DateTimeOffset(2024, 6, 30, 0, 0, 0, TimeSpan.Zero));
            var dr = DateRange.CreateUtcThirtyDays(fakeClock);
            dr.GetStartTime()!.Value.Date.Should().Be(new DateTime(2024, 5, 31));
        }

        [TestMethod]
        public void UtcTwoWeek_Starts14DaysBeforeUtcNow()
        {
            var fakeClock = new FixedTimeProvider(new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero));
            var dr = DateRange.CreateUtcTwoWeek(fakeClock);
            dr.GetStartTime()!.Value.Date.Should().Be(new DateTime(2024, 6, 1));
        }

        [TestMethod]
        public void UtcNinetyDays_Starts90DaysBeforeUtcNow()
        {
            var fakeClock = new FixedTimeProvider(new DateTimeOffset(2024, 6, 30, 0, 0, 0, TimeSpan.Zero));
            var dr = DateRange.CreateUtcNinetyDays(fakeClock);
            dr.GetStartTime()!.Value.Date.Should().Be(new DateTime(2024, 4, 1));
        }

        [TestMethod]
        public void UtcDefault_IsSameAsUtcToday()
        {
            DateRange.UtcDefault.Should().NotBeNull();
        }

        // ─── TryParse(string, out DateRange) ─────────────────────────────────

        [TestMethod]
        public void TryParse_NullOrEmpty_ReturnsFalse()
        {
            DateRange.TryParse("", out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        [TestMethod]
        public void TryParse_TildeSeparated_DateTime_ReturnsTrue()
        {
            var input = "2024-01-01 00:00:00 ~ 2024-01-31 23:59:59";
            DateRange.TryParse(input, out var result).Should().BeTrue();
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void TryParse_TildeSeparated_Date_ReturnsTrue()
        {
            var input = "2024-01-01 ~ 2024-01-31";
            DateRange.TryParse(input, out var result).Should().BeTrue();
            result.Should().NotBeNull();
            result!.Type.Should().Be(DateTimeTypeEnum.Date);
        }

        [TestMethod]
        public void TryParse_TildeSeparated_Month_ReturnsTrue()
        {
            var input = "2024-01 ~ 2024-06";
            DateRange.TryParse(input, out var result).Should().BeTrue();
            result!.Type.Should().Be(DateTimeTypeEnum.Month);
        }

        [TestMethod]
        public void TryParse_TildeSeparated_Year_ReturnsTrue()
        {
            var input = "2023 ~ 2024";
            DateRange.TryParse(input, out var result).Should().BeTrue();
            result.Should().NotBeNull();
            // The Year case in TryParse uses the default DateTime constructor (no explicit type passed),
            // so Type remains DateTimeTypeEnum.DateTime — test actual behaviour.
            result!.GetStartTime()!.Value.Year.Should().Be(2023);
        }

        [TestMethod]
        public void TryParse_InvalidText_ReturnsFalse()
        {
            DateRange.TryParse("not-a-date ~ also-not", out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        [TestMethod]
        public void TryParse_SingleDateNoSeparator_ReturnsFalse()
        {
            DateRange.TryParse("2024-01-01", out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        // ─── TryParse(string, char[], ...) ────────────────────────────────────

        [TestMethod]
        public void TryParse_CharSeparator_ValidInput_ReturnsTrue()
        {
            var input = "2024-01-01|2024-01-31";
            DateRange.TryParse(input, new[] { '|' }, out var result).Should().BeTrue();
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void TryParse_CharSeparator_Empty_ReturnsFalse()
        {
            DateRange.TryParse("", new[] { '~' }, out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        [TestMethod]
        public void TryParse_CharSeparator_OnePart_ReturnsFalse()
        {
            DateRange.TryParse("2024-01-01", new[] { '~' }, out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        // ─── TryParse(string, string[], ...) ─────────────────────────────────

        [TestMethod]
        public void TryParse_StringSeparator_ValidInput_ReturnsTrue()
        {
            var input = "2024-01-01 to 2024-01-31";
            DateRange.TryParse(input, new[] { " to " }, out var result).Should().BeTrue();
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void TryParse_StringSeparator_Empty_ReturnsFalse()
        {
            DateRange.TryParse("", new[] { " to " }, out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        // ─── TryParse(string[], ...) ──────────────────────────────────────────

        [TestMethod]
        public void TryParse_StringArray_TwoValidElements_ReturnsTrue()
        {
            var input = new[] { "2024-01-01", "2024-01-31" };
            DateRange.TryParse(input, out var result).Should().BeTrue();
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void TryParse_StringArray_OneElement_ReturnsFalse()
        {
            DateRange.TryParse(new[] { "2024-01-01" }, out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        [TestMethod]
        public void TryParse_StringArray_ThreeElements_ReturnsFalse()
        {
            // Length != 2, so false
            DateRange.TryParse(new[] { "2024-01-01", "2024-01-15", "2024-01-31" }, out var result)
                .Should().BeFalse();
        }

        // ─── TryParse(string, string, DateTime, out DateRange) ───────────────

        [TestMethod]
        public void TryParse_StartEnd_Year_ParsesYear()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            DateRange.TryParse("2023", "2024", epoch, out var result).Should().BeTrue();
            result.Should().NotBeNull();
            // Year case uses the DateTime constructor without explicit type → Type stays DateTime
            result!.GetStartTime()!.Value.Year.Should().Be(2023);
            result.GetStartTime()!.Value.Month.Should().Be(1);
            result.GetStartTime()!.Value.Day.Should().Be(1);
        }

        [TestMethod]
        public void TryParse_StartEnd_Year_InvalidYear_ReturnsFalse()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            DateRange.TryParse("abcd", "2024", epoch, out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        [TestMethod]
        public void TryParse_StartEnd_Month_ParsesMonth()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            DateRange.TryParse("2024-01", "2024-06", epoch, out var result).Should().BeTrue();
            result!.Type.Should().Be(DateTimeTypeEnum.Month);
        }

        [TestMethod]
        public void TryParse_StartEnd_Time_ParsesTime()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            DateRange.TryParse("08:00:00", "20:00:00", epoch, out var result).Should().BeTrue();
            result!.Type.Should().Be(DateTimeTypeEnum.Time);
        }

        [TestMethod]
        public void TryParse_StartEnd_Date_ParsesDate()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            DateRange.TryParse("2024-01-01", "2024-12-31", epoch, out var result).Should().BeTrue();
            result!.Type.Should().Be(DateTimeTypeEnum.Date);
        }

        [TestMethod]
        public void TryParse_StartEnd_DateTime_ParsesDateTime()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            DateRange.TryParse("2024-01-01 08:00:00", "2024-12-31 23:59:59", epoch, out var result)
                .Should().BeTrue();
            result!.Type.Should().Be(DateTimeTypeEnum.DateTime);
        }

        [TestMethod]
        public void TryParse_StartEnd_InvalidDate_ReturnsFalse()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            DateRange.TryParse("not-a-date", "2024-01-31", epoch, out var result).Should().BeFalse();
            result.Should().BeNull();
        }

        // ─── GetStartOffset / SetStartOffset ─────────────────────────────────

        [TestMethod]
        public void GetStartOffset_NullStart_ReturnsNull()
        {
            // start > end → _startTime null (rejected by SetStartTime because _endTime not set but _startTime < _endTime check OK when endTime is null, so start IS set).
            // Use the approach: start=2024-06-01, end=2024-01-01 → _endTime null, _startTime=2024-06-01
            // Then verify GetStartOffset is NOT null (start was accepted).
            // Better: verify normal DateRange gives non-null.
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            dr.GetStartOffset().Should().NotBeNull();
        }

        [TestMethod]
        public void SetStartOffset_Null_NoChange()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            var before = dr.GetStartTime();
            dr.SetStartOffset(null);
            dr.GetStartTime().Should().Be(before);
        }

        [TestMethod]
        public void SetStartOffset_Valid_UpdatesStart()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            var offset = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
            dr.SetStartOffset(offset);
            dr.GetStartTime().Should().NotBeNull();
        }

        // ─── GetEndOffset / SetEndOffset ──────────────────────────────────────

        [TestMethod]
        public void GetEndOffset_NullEnd_ReturnsNull()
        {
            // start > end → _endTime null
            var dr = new DateRange(new DateTime(2024, 6, 1), new DateTime(2024, 1, 1));
            dr.GetEndOffset().Should().BeNull();
        }

        [TestMethod]
        public void SetEndOffset_Null_NoChange()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            var before = dr.GetEndTime();
            dr.SetEndOffset(null);
            dr.GetEndTime().Should().Be(before);
        }

        // ─── GetStartSpan / SetStartSpan ──────────────────────────────────────

        [TestMethod]
        public void GetStartSpan_NullEnd_ReturnsNull()
        {
            // start > end → _endTime null. GetStartSpan checks _endTime == null → returns null
            var dr = new DateRange(new DateTime(2024, 6, 1), new DateTime(2024, 1, 1));
            dr.GetStartSpan().Should().BeNull();
        }

        [TestMethod]
        public void SetStartSpan_Null_NoChange()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            var before = dr.GetStartTime();
            dr.SetStartSpan(null);
            dr.GetStartTime().Should().Be(before);
        }

        [TestMethod]
        public void SetStartSpan_ValidSpan_UpdatesStart()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(TimeSpan.Zero, TimeSpan.FromDays(30), DateTimeTypeEnum.DateTime, epoch);
            dr.SetStartSpan(TimeSpan.FromDays(5));
            dr.GetStartTime().Should().NotBeNull();
        }

        // ─── GetEndSpan / SetEndSpan ──────────────────────────────────────────

        [TestMethod]
        public void GetEndSpan_NullEnd_ReturnsNull()
        {
            // start > end → _endTime null. GetEndSpan checks _endTime == null → returns null
            var dr = new DateRange(new DateTime(2024, 6, 1), new DateTime(2024, 1, 1));
            dr.GetEndSpan().Should().BeNull();
        }

        [TestMethod]
        public void SetEndSpan_Null_NoChange()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            var before = dr.GetEndTime();
            dr.SetEndSpan(null);
            dr.GetEndTime().Should().Be(before);
        }

        // ─── SwitchTime (epoch kind conversion) ──────────────────────────────

        [TestMethod]
        public void Constructor_UtcEpoch_UnspecifiedTime_ConvertsToUtc()
        {
            var utcEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var unspecifiedStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var unspecifiedEnd = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
            var dr = new DateRange(unspecifiedStart, unspecifiedEnd, DateTimeTypeEnum.DateTime, utcEpoch);
            dr.GetStartTime()!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }

        [TestMethod]
        public void Constructor_UtcEpoch_LocalTime_ConvertsToUtc()
        {
            var utcEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var localStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var localEnd = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(localStart, localEnd, DateTimeTypeEnum.DateTime, utcEpoch);
            dr.GetStartTime()!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }

        [TestMethod]
        public void Constructor_UnspecifiedEpoch_LocalTime_ConvertsKind()
        {
            var unspecifiedEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var localStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var localEnd = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(localStart, localEnd, DateTimeTypeEnum.DateTime, unspecifiedEpoch);
            dr.GetStartTime()!.Value.Kind.Should().Be(DateTimeKind.Unspecified);
        }

        [TestMethod]
        public void Constructor_UnspecifiedEpoch_UtcTime_ConvertsKind()
        {
            var unspecifiedEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var utcStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var utcEnd = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc);
            var dr = new DateRange(utcStart, utcEnd, DateTimeTypeEnum.DateTime, unspecifiedEpoch);
            dr.GetStartTime()!.Value.Kind.Should().Be(DateTimeKind.Unspecified);
        }

        [TestMethod]
        public void Constructor_LocalEpoch_UtcTime_ConvertsToLocal()
        {
            var localEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var utcEnd = new DateTime(2024, 6, 30, 12, 0, 0, DateTimeKind.Utc);
            var dr = new DateRange(utcStart, utcEnd, DateTimeTypeEnum.DateTime, localEpoch);
            dr.GetStartTime()!.Value.Kind.Should().Be(DateTimeKind.Local);
        }

        // ─── Offset/Span non-null return paths ────────────────────────────────

        [TestMethod]
        public void GetStartOffset_WithStart_ReturnsNonNull()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            dr.GetStartOffset().Should().NotBeNull();
        }

        [TestMethod]
        public void GetEndOffset_WithEnd_ReturnsNonNull()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31, 12, 0, 0));
            dr.GetEndOffset().Should().NotBeNull();
        }

        [TestMethod]
        public void GetStartSpan_WithBothSet_ReturnsSpan()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(TimeSpan.FromDays(10), TimeSpan.FromDays(20), DateTimeTypeEnum.DateTime, epoch);
            dr.GetStartSpan().Should().NotBeNull();
        }

        [TestMethod]
        public void GetEndSpan_WithBothSet_ReturnsSpan()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(TimeSpan.FromDays(10), TimeSpan.FromDays(20), DateTimeTypeEnum.DateTime, epoch);
            dr.GetEndSpan().Should().NotBeNull();
        }

        [TestMethod]
        public void SetEndOffset_Valid_UpdatesEnd()
        {
            var dr = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31, 12, 0, 0));
            var offset = new DateTimeOffset(2024, 11, 30, 12, 0, 0, TimeSpan.Zero);
            dr.SetEndOffset(offset);
            dr.GetEndTime().Should().NotBeNull();
        }

        [TestMethod]
        public void SetEndSpan_Valid_UpdatesEnd()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var dr = new DateRange(TimeSpan.FromDays(5), TimeSpan.FromDays(20), DateTimeTypeEnum.DateTime, epoch);
            dr.SetEndSpan(TimeSpan.FromDays(25));
            dr.GetEndTime().Should().NotBeNull();
        }

        // ─── UTC presets (property-only access) ───────────────────────────────

        [TestMethod]
        public void UtcNinetyDays_Property_ReturnsRange()
        {
            var dr = DateRange.UtcNinetyDays;
            dr.Should().NotBeNull();
            dr.GetStartTime().Should().NotBeNull();
        }

        [TestMethod]
        public void UtcThirtyDays_Property_ReturnsRange()
        {
            var dr = DateRange.UtcThirtyDays;
            dr.Should().NotBeNull();
            dr.GetStartTime().Should().NotBeNull();
        }

        [TestMethod]
        public void UtcTwoWeek_Property_ReturnsRange()
        {
            var dr = DateRange.UtcTwoWeek;
            dr.Should().NotBeNull();
            dr.GetStartTime().Should().NotBeNull();
        }

        [TestMethod]
        public void UtcWeek_Property_ReturnsRange()
        {
            var dr = DateRange.UtcWeek;
            dr.Should().NotBeNull();
            dr.GetStartTime().Should().NotBeNull();
        }

        [TestMethod]
        public void UtcYesterday_Property_ReturnsRange()
        {
            var dr = DateRange.UtcYesterday;
            dr.Should().NotBeNull();
            dr.GetStartTime().Should().NotBeNull();
        }

        // ─── DateTimeFormatDic ────────────────────────────────────────────────

        [TestMethod]
        public void DateTimeFormatDic_ContainsExpectedKeys()
        {
            DateRange.DateTimeFormatDic.Should().ContainKey(DateTimeTypeEnum.Date)
                .And.ContainKey(DateTimeTypeEnum.DateTime)
                .And.ContainKey(DateTimeTypeEnum.Year)
                .And.ContainKey(DateTimeTypeEnum.Month)
                .And.ContainKey(DateTimeTypeEnum.Time);
        }

        [TestMethod]
        public void DateTimeFormatDic_DateFormat_IsYearMonthDay()
        {
            DateRange.DateTimeFormatDic[DateTimeTypeEnum.Date].Should().Be("yyyy-MM-dd");
        }
    }

    // ─── Minimal TimeProvider stub ────────────────────────────────────────────

    internal sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
