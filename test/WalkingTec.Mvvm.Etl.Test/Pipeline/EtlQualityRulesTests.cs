#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline
{
    /// <summary>
    /// 驗證 <see cref="EtlQualityRuleEvaluator"/> 對 NotNull / Range / Regex / In
    /// 的判定，以及 Drop / Continue / Abort 三種 Action 的行為差異與
    /// failure-sample 上限。
    /// </summary>
    [TestClass]
    public class EtlQualityRulesTests
    {
        private static DataTable Table()
        {
            var t = new DataTable("Orders");
            t.Columns.Add("Email", typeof(string));
            t.Columns.Add("Age", typeof(int));
            t.Columns.Add("Status", typeof(string));
            t.Columns.Add("CustomerId", typeof(string));
            return t;
        }

        private static DataRow AddRow(DataTable t, string? email, int? age, string? status, string? cust)
        {
            var r = t.NewRow();
            r["Email"] = (object?)email ?? DBNull.Value;
            r["Age"] = (object?)age ?? DBNull.Value;
            r["Status"] = (object?)status ?? DBNull.Value;
            r["CustomerId"] = (object?)cust ?? DBNull.Value;
            t.Rows.Add(r);
            return r;
        }

        [TestMethod]
        public void NotNull_drops_null_rows()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "PAID", "C1");
            AddRow(t, "b@x.com", 25, "NEW", null);  // CustomerId null
            AddRow(t, "c@x.com", 40, "PAID", "C3");

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "CustomerId", RuleType = EtlQualityRuleType.NotNull },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Drop, out var failed, out var samples);

            Assert.AreEqual(1, failed);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.IsTrue(samples[0].Contains("CustomerId"));
            Assert.IsTrue(samples[0].Contains("NotNull"));
        }

        [TestMethod]
        public void Range_rejects_out_of_bounds()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "OK", "C1");
            AddRow(t, "b@x.com", 200, "OK", "C2"); // out of [0..120]
            AddRow(t, "c@x.com", -1, "OK", "C3");  // below 0

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Drop, out var failed, out _);

            Assert.AreEqual(2, failed);
            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(30, result.Rows[0]["Age"]);
        }

        [TestMethod]
        public void Regex_rejects_invalid_email()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "OK", "C1");
            AddRow(t, "no-at-sign", 30, "OK", "C2");
            AddRow(t, "x@y.z", 30, "OK", "C3");

            var rules = new List<EtlQualityRule>
            {
                new()
                {
                    Column = "Email",
                    RuleType = EtlQualityRuleType.Regex,
                    Pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
                },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Drop, out var failed, out _);

            Assert.AreEqual(1, failed);
            Assert.AreEqual(2, result.Rows.Count);
        }

        [TestMethod]
        public void In_rejects_non_whitelisted_status()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");
            AddRow(t, "b@x.com", 30, "EVIL", "C2"); // not in
            AddRow(t, "c@x.com", 30, "PAID", "C3");

            var rules = new List<EtlQualityRule>
            {
                new()
                {
                    Column = "Status",
                    RuleType = EtlQualityRuleType.In,
                    Allowed = new List<string> { "NEW", "PAID", "SHIPPED" },
                },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Drop, out var failed, out _);

            Assert.AreEqual(1, failed);
            Assert.AreEqual(2, result.Rows.Count);
        }

        [TestMethod]
        public void Continue_keeps_violating_rows_audit_only()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");
            AddRow(t, "b@x.com", 30, "NEW", null);   // violation

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "CustomerId", RuleType = EtlQualityRuleType.NotNull },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Continue, out var failed, out _);

            Assert.AreEqual(1, failed);
            Assert.AreEqual(2, result.Rows.Count, "Continue 模式違規列照樣保留");
            Assert.AreSame(t, result, "Continue 模式回傳原表，不 clone");
        }

        [TestMethod]
        public void Abort_throws_on_first_violation()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");
            AddRow(t, "b@x.com", 999, "NEW", "C2"); // out of range

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 },
            };

            Assert.ThrowsException<EtlQualityRuleViolationException>(() =>
                EtlQualityRuleEvaluator.Apply(
                    t, rules, EtlQualityRuleAction.Abort, out _, out _));
        }

        [TestMethod]
        public void Multiple_rules_AND_together()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");      // pass
            AddRow(t, "b@x.com", 200, "NEW", "C2");     // age fail
            AddRow(t, "no-at", 30, "NEW", "C3");        // email fail
            AddRow(t, "c@x.com", 30, "EVIL", "C4");     // status fail

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 },
                new() { Column = "Email", RuleType = EtlQualityRuleType.Regex, Pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$" },
                new() { Column = "Status", RuleType = EtlQualityRuleType.In, Allowed = new List<string> { "NEW", "PAID" } },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Drop, out var failed, out _);

            Assert.AreEqual(3, failed);
            Assert.AreEqual(1, result.Rows.Count);
        }

        [TestMethod]
        public void Sample_capped_at_MaxFailureSamples()
        {
            var t = Table();
            for (int i = 0; i < EtlQualityRuleEvaluator.MaxFailureSamples + 5; i++)
            {
                AddRow(t, "x@y.z", 30, "NEW", null); // 全違規
            }
            var rules = new List<EtlQualityRule>
            {
                new() { Column = "CustomerId", RuleType = EtlQualityRuleType.NotNull },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Drop, out var failed, out var samples);

            Assert.AreEqual(EtlQualityRuleEvaluator.MaxFailureSamples + 5, failed);
            Assert.AreEqual(EtlQualityRuleEvaluator.MaxFailureSamples, samples.Count);
            Assert.AreEqual(0, result.Rows.Count);
        }

        [TestMethod]
        public void Abort_with_captureRows_true_attaches_offending_row_to_exception()
        {
            // #673(c): when captureRows is on, Abort must attach the offending row to
            // the exception BEFORE throwing so the caller can dead-letter it — additive
            // diagnostic only, does not change the throw-and-fail semantics.
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");
            var badRow = AddRow(t, "b@x.com", 999, "NEW", "C2"); // out of range

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 },
            };

            var ex = Assert.ThrowsException<EtlQualityRuleViolationException>(() =>
                EtlQualityRuleEvaluator.Apply(
                    t, rules, EtlQualityRuleAction.Abort, out _, out _,
                    captureRows: true, out _));

            Assert.IsNotNull(ex.OffendingRow, "captureRows=true must attach the offending row");
            Assert.AreSame(badRow, ex.OffendingRow);
            Assert.IsNotNull(ex.ViolationReason);
            Assert.IsTrue(ex.ViolationReason!.Contains("Age"));
        }

        [TestMethod]
        public void Abort_with_captureRows_false_does_not_attach_offending_row()
        {
            // Symmetric with the Drop path's capture gate — hot-path callers that never
            // enable dead-letter pay zero extra cost and see identical exception shape.
            var t = Table();
            AddRow(t, "b@x.com", 999, "NEW", "C2"); // out of range

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 },
            };

            var ex = Assert.ThrowsException<EtlQualityRuleViolationException>(() =>
                EtlQualityRuleEvaluator.Apply(
                    t, rules, EtlQualityRuleAction.Abort, out _, out _,
                    captureRows: false, out _));

            Assert.IsNull(ex.OffendingRow);
            Assert.IsNull(ex.ViolationReason);
        }

        [TestMethod]
        public void Abort_exception_message_is_identical_regardless_of_captureRows()
        {
            // The additive constructor must never change Message — legacy callers
            // parsing/logging the message string see byte-identical output.
            DataTable Make()
            {
                var t = Table();
                AddRow(t, "b@x.com", 999, "NEW", "C2");
                return t;
            }
            var rules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 },
            };

            var exCaptured = Assert.ThrowsException<EtlQualityRuleViolationException>(() =>
                EtlQualityRuleEvaluator.Apply(Make(), rules, EtlQualityRuleAction.Abort, out _, out _, captureRows: true, out _));
            var exNotCaptured = Assert.ThrowsException<EtlQualityRuleViolationException>(() =>
                EtlQualityRuleEvaluator.Apply(Make(), rules, EtlQualityRuleAction.Abort, out _, out _, captureRows: false, out _));

            Assert.AreEqual(exNotCaptured.Message, exCaptured.Message);
        }

        [TestMethod]
        public void Missing_column_throws_ArgumentException()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");
            var rules = new List<EtlQualityRule>
            {
                new() { Column = "NonExistent", RuleType = EtlQualityRuleType.NotNull },
            };
            Assert.ThrowsException<ArgumentException>(() =>
                EtlQualityRuleEvaluator.Apply(
                    t, rules, EtlQualityRuleAction.Drop, out _, out _));
        }

        [TestMethod]
        public void Empty_rules_returns_original_table_unchanged()
        {
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");
            var result = EtlQualityRuleEvaluator.Apply(
                t, new List<EtlQualityRule>(), EtlQualityRuleAction.Drop, out var failed, out var samples);

            Assert.AreEqual(0, failed);
            Assert.AreEqual(0, samples.Count);
            Assert.AreSame(t, result);
        }

        [TestMethod]
        public void Range_skips_null_values_silently()
        {
            // 規則只管「有值就在範圍內」；null 由 NotNull 規則處理
            var t = Table();
            AddRow(t, "a@x.com", 30, "NEW", "C1");
            AddRow(t, "b@x.com", null, "NEW", "C2");    // null Age

            var rules = new List<EtlQualityRule>
            {
                new() { Column = "Age", RuleType = EtlQualityRuleType.Range, Min = 0, Max = 120 },
            };
            var result = EtlQualityRuleEvaluator.Apply(
                t, rules, EtlQualityRuleAction.Drop, out var failed, out _);

            Assert.AreEqual(0, failed);
            Assert.AreEqual(2, result.Rows.Count);
        }
    }
}
