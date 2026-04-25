#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// Coverage for the relative-date tokens added on top of the
    /// existing @today/@thisWeek/@lastWeek/@thisMonth/@lastMonth/
    /// @last30days/@thisQuarter/@ytd set: @yesterday, @nextWeek,
    /// @nextMonth, @last7days, @last90days, @last365days,
    /// @lastQuarter, @thisYear, @lastYear. Tokens are case-insensitive
    /// and emit a (Gte, Lte) pair on the same field as the original
    /// filter — same contract as the pre-existing tokens.
    /// </summary>
    [TestClass]
    public class AnalysisRelativeDateTokensTests
    {
        private static List<FilterCondition> Resolve(string token)
        {
            var input = new List<FilterCondition>
            {
                new() { Field = "Date", Operator = FilterOperator.Eq, Value = token },
            };
            return AnalysisQueryEngine.ResolveRelativeDates(input);
        }

        private static (DateTime start, DateTime endInclusive) Bounds(List<FilterCondition> resolved)
        {
            Assert.AreEqual(2, resolved.Count, "Token must expand to a Gte/Lte pair.");
            Assert.AreEqual(FilterOperator.Gte, resolved[0].Operator);
            Assert.AreEqual(FilterOperator.Lte, resolved[1].Operator);
            // Lte value is yyyy-MM-dd HH:mm:ss; trim to date for assertions.
            var start = DateTime.Parse(resolved[0].Value);
            var endInclusive = DateTime.Parse(resolved[1].Value.Substring(0, 10));
            return (start, endInclusive);
        }

        // ── @yesterday ──────────────────────────────────────────────────

        [TestMethod]
        public void Yesterday_is_a_single_day_pair_for_dot_minus_one()
        {
            var today = DateTime.Today;
            var (s, e) = Bounds(Resolve("@yesterday"));
            Assert.AreEqual(today.AddDays(-1), s);
            Assert.AreEqual(today.AddDays(-1), e);
        }

        // ── @nextWeek ───────────────────────────────────────────────────

        [TestMethod]
        public void NextWeek_is_full_calendar_week_after_this_week()
        {
            var today = DateTime.Today;
            var monOffset = ((int)today.DayOfWeek + 6) % 7;
            var thisMonday = today.AddDays(-monOffset);

            var (s, e) = Bounds(Resolve("@nextWeek"));
            Assert.AreEqual(thisMonday.AddDays(7), s);
            Assert.AreEqual(thisMonday.AddDays(13), e);
            Assert.AreEqual(7, (e - s).Days + 1);
        }

        // ── @nextMonth ──────────────────────────────────────────────────

        [TestMethod]
        public void NextMonth_starts_first_of_next_month_ends_last_day()
        {
            var today = DateTime.Today;
            var firstNext = new DateTime(today.Year, today.Month, 1).AddMonths(1);
            var lastNext = firstNext.AddMonths(1).AddDays(-1);

            var (s, e) = Bounds(Resolve("@nextMonth"));
            Assert.AreEqual(firstNext, s);
            Assert.AreEqual(lastNext, e);
        }

        // ── @last7days / @last90days / @last365days ─────────────────────

        [TestMethod]
        public void Last7Days_window_is_today_minus_7_through_today()
        {
            var today = DateTime.Today;
            var (s, e) = Bounds(Resolve("@last7days"));
            Assert.AreEqual(today.AddDays(-7), s);
            Assert.AreEqual(today, e);
        }

        [TestMethod]
        public void Last90Days_window_is_today_minus_90_through_today()
        {
            var today = DateTime.Today;
            var (s, e) = Bounds(Resolve("@last90days"));
            Assert.AreEqual(today.AddDays(-90), s);
            Assert.AreEqual(today, e);
        }

        [TestMethod]
        public void Last365Days_window_is_today_minus_365_through_today()
        {
            var today = DateTime.Today;
            var (s, e) = Bounds(Resolve("@last365days"));
            Assert.AreEqual(today.AddDays(-365), s);
            Assert.AreEqual(today, e);
        }

        // ── @lastQuarter ────────────────────────────────────────────────

        [TestMethod]
        public void LastQuarter_is_full_previous_calendar_quarter_not_to_date()
        {
            var today = DateTime.Today;
            var thisQStart = new DateTime(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
            var expectStart = thisQStart.AddMonths(-3);
            var expectEnd = thisQStart.AddDays(-1);

            var (s, e) = Bounds(Resolve("@lastQuarter"));
            Assert.AreEqual(expectStart, s);
            Assert.AreEqual(expectEnd, e);

            // Sanity: previous quarter is exactly 3 months long (89-92 days).
            var span = (e - s).Days + 1;
            Assert.IsTrue(span >= 89 && span <= 92, $"Quarter length {span} out of expected 89..92 range.");
        }

        // ── @thisYear vs @ytd ───────────────────────────────────────────

        [TestMethod]
        public void ThisYear_runs_through_dec_31_distinct_from_ytd()
        {
            var today = DateTime.Today;
            var (s, e) = Bounds(Resolve("@thisYear"));
            Assert.AreEqual(new DateTime(today.Year, 1, 1), s);
            Assert.AreEqual(new DateTime(today.Year, 12, 31), e,
                "@thisYear must use Dec 31 (full-year semantics), unlike @ytd which clamps to today.");
        }

        [TestMethod]
        public void Ytd_clamps_to_today_unlike_thisYear()
        {
            var today = DateTime.Today;
            var ytd = Bounds(Resolve("@ytd"));
            var thisYear = Bounds(Resolve("@thisYear"));

            Assert.AreEqual(ytd.start, thisYear.start, "Both start on Jan 1.");
            // YTD ends today; thisYear ends Dec 31 — they coincide only on the last day of the year.
            if (today.Month != 12 || today.Day != 31)
            {
                Assert.AreNotEqual(ytd.endInclusive, thisYear.endInclusive,
                    "@ytd and @thisYear must differ on any day other than Dec 31.");
            }
        }

        // ── @lastYear ───────────────────────────────────────────────────

        [TestMethod]
        public void LastYear_is_full_previous_calendar_year()
        {
            var today = DateTime.Today;
            var (s, e) = Bounds(Resolve("@lastYear"));
            Assert.AreEqual(new DateTime(today.Year - 1, 1, 1), s);
            Assert.AreEqual(new DateTime(today.Year - 1, 12, 31), e);
        }

        // ── Case-insensitivity (existing convention) ────────────────────

        [TestMethod]
        public void New_tokens_are_case_insensitive()
        {
            // Sanity-check matching the existing @today/@thisweek convention.
            // Engine ToLowerInvariants the key before switch — verify each new
            // token resolves regardless of input casing.
            foreach (var raw in new[]
            {
                "@YESTERDAY", "@Yesterday", "@yesTerDay",
                "@NextWeek", "@LASTQUARTER", "@LAST90DAYS", "@thisYEAR", "@LastYear",
            })
            {
                var resolved = Resolve(raw);
                Assert.AreEqual(2, resolved.Count, $"Token {raw} should resolve.");
            }
        }

        // ── Backwards compatibility: existing tokens unchanged ──────────

        [TestMethod]
        public void Existing_tokens_still_resolve_unchanged()
        {
            // Spot-check all eight pre-existing tokens still expand;
            // catches accidental regression from re-ordering the switch.
            foreach (var token in new[]
            {
                "@today", "@thisWeek", "@lastWeek", "@thisMonth", "@lastMonth",
                "@last30days", "@thisQuarter", "@ytd",
            })
            {
                var resolved = Resolve(token);
                Assert.AreEqual(2, resolved.Count,
                    $"Pre-existing token {token} must still resolve to a Gte/Lte pair.");
                Assert.AreEqual(FilterOperator.Gte, resolved[0].Operator);
                Assert.AreEqual(FilterOperator.Lte, resolved[1].Operator);
            }
        }

        // ── Unknown token ───────────────────────────────────────────────

        [TestMethod]
        public void Unknown_token_throws_clear_error()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                Resolve("@neverHeardOfIt"));
            StringAssert.Contains(ex.Message, "@neverHeardOfIt");
        }
    }
}
