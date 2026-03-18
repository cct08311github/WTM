#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Security;

/// <summary>
/// Unit tests for WtmConnectionStringSanitizer (#559).
/// Verifies sensitive connection string fields are redacted before appearing in logs.
/// </summary>
[TestClass]
public class WtmConnectionStringSanitizerTests
{
    // ─── Redaction tests ────────────────────────────────────────────────

    [TestMethod]
    public void Sanitize_Password_is_redacted()
    {
        var input = "Server=myserver;Database=mydb;Password=s3cr3t!;";
        var result = WtmConnectionStringSanitizer.Sanitize(input);

        StringAssert.Contains(result, "Password=[redacted]");
        Assert.IsFalse(result.Contains("s3cr3t!"), "Raw password must not survive sanitization");
    }

    [TestMethod]
    public void Sanitize_Pwd_is_redacted()
    {
        var input = "Server=myserver;Pwd=abc123;";
        var result = WtmConnectionStringSanitizer.Sanitize(input);

        StringAssert.Contains(result, "Pwd=[redacted]");
        Assert.IsFalse(result.Contains("abc123"));
    }

    [TestMethod]
    public void Sanitize_UserId_is_redacted()
    {
        var input = "Data Source=oracle;User Id=admin;Password=pwd;";
        var result = WtmConnectionStringSanitizer.Sanitize(input);

        Assert.IsFalse(result.Contains("admin"), "UserId value must be redacted");
        Assert.IsFalse(result.Contains("pwd"), "Password value must be redacted");
    }

    [TestMethod]
    public void Sanitize_case_insensitive()
    {
        var input = "SERVER=x;PASSWORD=secret;DATABASE=db";
        var result = WtmConnectionStringSanitizer.Sanitize(input);

        Assert.IsFalse(result.Contains("secret"), "Case-insensitive match required for PASSWORD");
    }

    [TestMethod]
    public void Sanitize_preserves_non_sensitive_segments()
    {
        var input = "Server=myserver;Database=mydb;Password=s3cr3t;Port=1433";
        var result = WtmConnectionStringSanitizer.Sanitize(input);

        StringAssert.Contains(result, "Server=myserver");
        StringAssert.Contains(result, "Database=mydb");
        StringAssert.Contains(result, "Port=1433");
    }

    // ─── No-op tests (no redaction needed) ──────────────────────────────

    [TestMethod]
    public void Sanitize_plain_string_without_equals_is_unchanged()
    {
        var input = "Hello World — no connection string here";
        var result = WtmConnectionStringSanitizer.Sanitize(input);
        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public void Sanitize_innocent_key_value_pair_unchanged()
    {
        var input = "Status=OK;Code=200;Reason=NotFound";
        var result = WtmConnectionStringSanitizer.Sanitize(input);
        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public void Sanitize_empty_string_unchanged()
    {
        Assert.AreEqual("", WtmConnectionStringSanitizer.Sanitize(""));
    }

    [TestMethod]
    public void Sanitize_whitespace_only_unchanged()
    {
        Assert.AreEqual("   ", WtmConnectionStringSanitizer.Sanitize("   "));
    }

    // ─── Exception-message scenario ──────────────────────────────────────

    [TestMethod]
    public void Sanitize_scrubs_connection_string_embedded_in_exception_message()
    {
        // Simulates ex.Message from SqlException when connection fails
        var exMessage = "Cannot open server 'myserver' requested by the login. " +
                        "Connection string: Server=myserver;User Id=sa;Password=P@ssw0rd;Database=prod";
        var result = WtmConnectionStringSanitizer.Sanitize(exMessage);

        Assert.IsFalse(result.Contains("P@ssw0rd"), "Password must be redacted from exception message");
        Assert.IsFalse(result.Contains("User Id=sa"), "User Id must be redacted");
        StringAssert.Contains(result, "myserver"); // non-sensitive parts preserved
    }
}
