#nullable enable
using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for <see cref="EtlColumnNameValidator"/> — #680 defense-in-depth
/// allowlist for staging column names entering the ETL pipeline.
/// </summary>
[TestClass]
public class EtlColumnNameValidatorTests
{
    // ─── IsValid: legal names ──────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("OrderNo")]
    [DataRow("Amount")]
    [DataRow("UpdatedAt")]
    [DataRow("order_no")]
    [DataRow("Col123")]
    [DataRow("COL#1")]
    [DataRow("COL$1")]
    [DataRow("_leading_underscore")]
    [DataRow("resp_code")] // #680 P3 rider: must not be rejected as an "sp_" false-positive
    public void IsValid_returns_true_for_conforming_names(string name)
    {
        EtlColumnNameValidator.IsValid(name).Should().BeTrue($"'{name}' matches the allowlist");
    }

    // ─── IsValid: Unicode (CJK) names — MEDIUM fix, widened allowlist ──────
    // The allowlist was originally ASCII-only (^[A-Za-z0-9_#$]+$), which would
    // have rejected CJK column headers (e.g. from CSV/Excel sources) routine
    // in this framework's primary (Chinese) audience — a breaking behaviour
    // change for a legitimate, common use case, not just a hostile-input
    // guard. Widened to ^[\p{L}\p{N}_#$]+$ (Unicode letters/digits) so CJK
    // passes while every SQL-meta/whitespace/punctuation character motivating
    // the original guard is still rejected below.

    [DataTestMethod]
    [DataRow("订单编号")]  // pure CJK: "order number"
    [DataRow("客户名称")]  // pure CJK: "customer name"
    [DataRow("订单No")]    // mixed CJK + ASCII
    [DataRow("金额$")]     // CJK + allowed symbol
    [DataRow("列_1")]      // CJK + underscore + digit
    public void IsValid_returns_true_for_conforming_unicode_CJK_names(string name)
    {
        EtlColumnNameValidator.IsValid(name).Should().BeTrue(
            $"'{name}' is a legitimate CJK column name and must be accepted by the Unicode-aware allowlist");
    }

    // ─── IsValid: hostile / non-conforming names ───────────────────────────

    [DataTestMethod]
    [DataRow("Order]No")]           // MSSQL bracket-close injection
    [DataRow("Order;DROP TABLE X")] // statement separator
    [DataRow("Order Name")]         // space
    [DataRow("Order'Name")]         // quote
    [DataRow("Order\"Name")]        // double-quote
    [DataRow("Order--comment")]     // SQL comment
    [DataRow("订单]编号")]           // CJK + MSSQL bracket-close injection — Unicode widening must not weaken this
    [DataRow("订单;编号")]           // CJK + statement separator
    [DataRow("订单 编号")]           // CJK + space
    [DataRow("订单'编号")]           // CJK + quote
    [DataRow("😀OrderNo")]          // emoji is Unicode category So (Symbol), not \p{L}/\p{N} — must stay rejected
    [DataRow("́OrderNo")]      // leading bare combining acute accent (Mn) — not \p{L}/\p{N} — must stay rejected
    [DataRow("")]
    [DataRow(null)]
    public void IsValid_returns_false_for_hostile_names(string? name)
    {
        EtlColumnNameValidator.IsValid(name).Should().BeFalse($"'{name}' must not match the allowlist");
    }

    // ─── ValidateOrThrow ────────────────────────────────────────────────────

    [TestMethod]
    public void ValidateOrThrow_does_not_throw_for_all_legal_names()
    {
        var names = new List<string> { "OrderNo", "Amount", "UpdatedAt", "resp_code" };

        var act = () => EtlColumnNameValidator.ValidateOrThrow(names);

        act.Should().NotThrow();
    }

    [TestMethod]
    public void ValidateOrThrow_throws_ArgumentException_naming_the_offending_column()
    {
        var names = new List<string> { "OrderNo", "Order]Name", "Amount" };

        var act = () => EtlColumnNameValidator.ValidateOrThrow(names);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Order]Name*");
    }

    [TestMethod]
    public void ValidateOrThrow_fails_fast_on_the_first_violation()
    {
        // "Order;Drop" is the first offender; "Also Bad" would also fail but
        // must never be reached/reported — fail-fast, mirrors IsSafeWhereClause.
        var names = new List<string> { "Order;Drop", "Also Bad" };

        var act = () => EtlColumnNameValidator.ValidateOrThrow(names);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Order;Drop*")
            .Which.Message.Should().NotContain("Also Bad");
    }
}
