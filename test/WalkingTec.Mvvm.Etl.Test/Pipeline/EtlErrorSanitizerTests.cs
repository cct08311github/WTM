#nullable enable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class EtlErrorSanitizerTests
{
    [TestMethod]
    public void Sanitize_plain_exception_returns_message_without_stack_trace()
    {
        Exception ex;
        try { throw new InvalidOperationException("something went wrong"); }
        catch (Exception e) { ex = e; }

        var result = EtlErrorSanitizer.Sanitize(ex);

        Assert.AreEqual("something went wrong", result);
        Assert.IsFalse(result.Contains("at WalkingTec"), "Stack trace should not appear in sanitized message");
    }

    [TestMethod]
    public void Sanitize_redacts_Password_in_message()
    {
        var ex = new Exception("Login failed for server; Password=S3cr3t;Database=MyDb");

        var result = EtlErrorSanitizer.Sanitize(ex);

        Assert.IsFalse(result.Contains("S3cr3t"), "Password value must be redacted");
        Assert.IsTrue(result.Contains("[redacted]"), "Redaction placeholder must be present");
    }

    [TestMethod]
    public void Sanitize_redacts_Data_Source_in_message()
    {
        var ex = new Exception("Cannot open server; Data Source=192.168.1.1;User Id=sa;Password=abc123");

        var result = EtlErrorSanitizer.Sanitize(ex);

        Assert.IsFalse(result.Contains("192.168.1.1"), "Data Source value must be redacted");
        Assert.IsFalse(result.Contains("abc123"), "Password value must be redacted");
    }

    [TestMethod]
    public void Sanitize_truncates_message_exceeding_2000_chars()
    {
        var ex = new Exception(new string('x', 3000));

        var result = EtlErrorSanitizer.Sanitize(ex);

        Assert.AreEqual(2000, result.Length);
    }

    [TestMethod]
    public void Sanitize_message_without_sensitive_data_is_unchanged()
    {
        var ex = new Exception("ETL step failed: source returned 0 rows");

        var result = EtlErrorSanitizer.Sanitize(ex);

        Assert.AreEqual("ETL step failed: source returned 0 rows", result);
    }
}
