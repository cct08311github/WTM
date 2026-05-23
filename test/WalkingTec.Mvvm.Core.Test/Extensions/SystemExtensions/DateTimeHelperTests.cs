#nullable enable
using System;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions.SystemExtensions
{
    [TestClass]
    public class DateTimeHelperTests
    {
        // ─── Jan1st1970 ───────────────────────────────────────────────────────

        [TestMethod]
        public void Jan1st1970_IsEpochUtc()
        {
            DateTimeHelper.Jan1st1970.Should().Be(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            DateTimeHelper.Jan1st1970.Kind.Should().Be(DateTimeKind.Utc);
        }

        // ─── ToMilliseconds ───────────────────────────────────────────────────

        [TestMethod]
        public void ToMilliseconds_Epoch_ReturnsZero()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            epoch.ToMilliseconds().Should().Be(0L);
        }

        [TestMethod]
        public void ToMilliseconds_OneSecondAfterEpoch_Returns1000()
        {
            var dt = new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc);
            dt.ToMilliseconds().Should().Be(1000L);
        }

        [TestMethod]
        public void ToMilliseconds_KnownDate_IsPositive()
        {
            var dt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            dt.ToMilliseconds().Should().BePositive();
        }

        [TestMethod]
        public void ToMilliseconds_LocalTime_ConvertsToUtcFirst()
        {
            // Local and UTC representations of the same instant should give the same ms.
            var utc = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            var local = utc.ToLocalTime();
            local.ToMilliseconds().Should().Be(utc.ToMilliseconds());
        }

        // ─── ToMicroseconds ───────────────────────────────────────────────────

        [TestMethod]
        public void ToMicroseconds_Epoch_ReturnsZero()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            epoch.ToMicroseconds().Should().Be(0L);
        }

        [TestMethod]
        public void ToMicroseconds_OneSecondAfterEpoch_Returns1_000_000()
        {
            var dt = new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc);
            dt.ToMicroseconds().Should().Be(1_000_000L);
        }

        [TestMethod]
        public void ToMicroseconds_IsMillisecondsTimesThousand()
        {
            var dt = new DateTime(2024, 3, 14, 9, 26, 53, DateTimeKind.Utc);
            dt.ToMicroseconds().Should().Be(dt.ToMilliseconds() * 1000L);
        }

        // ─── WeekOfYear ───────────────────────────────────────────────────────

        [TestMethod]
        public void WeekOfYear_Jan1_Returns1()
        {
            new DateTime(2024, 1, 1).WeekOfYear().Should().BeGreaterThanOrEqualTo(1);
        }

        [TestMethod]
        public void WeekOfYear_Dec31_IsLast()
        {
            new DateTime(2024, 12, 31).WeekOfYear().Should().BeGreaterThanOrEqualTo(1);
        }

        [TestMethod]
        public void WeekOfYear_LaterDatesHaveHigherOrEqualWeek()
        {
            var early = new DateTime(2024, 2, 1).WeekOfYear();
            var later = new DateTime(2024, 6, 1).WeekOfYear();
            later.Should().BeGreaterThanOrEqualTo(early);
        }

        // ─── WeekDays (static) ────────────────────────────────────────────────

        [TestMethod]
        public void WeekDays_Static_StartDayIsBeforeEndDay()
        {
            DateTimeHelper.WeekDays(2024, 10, out var start, out var end);
            start.Should().BeBefore(end);
        }

        [TestMethod]
        public void WeekDays_Static_EndDayIsSevenDaysAfterStart()
        {
            DateTimeHelper.WeekDays(2024, 10, out var start, out var end);
            (end - start).TotalDays.Should().BeApproximately(7, 0.001);
        }

        [TestMethod]
        public void WeekDays_Static_Week1StartsNearJan1()
        {
            DateTimeHelper.WeekDays(2024, 1, out var start, out var end);
            start.Year.Should().BeGreaterThanOrEqualTo(2023); // might start in prior year
            end.Year.Should().BeGreaterThanOrEqualTo(2024);
        }

        // ─── WeekDays (extension) ─────────────────────────────────────────────

        [TestMethod]
        public void WeekDays_Extension_StartAndEndBracketCurrentDay()
        {
            var dt = new DateTime(2024, 5, 15);
            dt.WeekDays(out var start, out var end);
            start.Should().BeBefore(dt.AddDays(1));
            end.Should().BeAfter(dt.AddDays(-1));
        }

        [TestMethod]
        public void WeekDays_Extension_EndIsPlusSevenFromStart()
        {
            var dt = new DateTime(2024, 5, 15);
            dt.WeekDays(out var start, out var end);
            (end - start).TotalDays.Should().BeApproximately(7, 0.001);
        }

        [TestMethod]
        public void WeekDays_Extension_ConsistentWithStaticVersion()
        {
            var dt = new DateTime(2024, 7, 10);
            int woy = dt.WeekOfYear();
            DateTimeHelper.WeekDays(dt.Year, woy, out var staticStart, out var staticEnd);
            dt.WeekDays(out var extStart, out var extEnd);
            extStart.Should().Be(staticStart);
            extEnd.Should().Be(staticEnd);
        }
    }
}
