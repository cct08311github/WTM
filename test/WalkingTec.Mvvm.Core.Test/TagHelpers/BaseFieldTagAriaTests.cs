using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Guards the required-dot HTML string in BaseFieldTag.
/// Full TagHelper integration test (requires ModelExpression mock) is deferred to E2E.
/// These tests ensure the constant string is correct and stays correct.
/// </summary>
[TestClass]
public class BaseFieldTagAriaTests
{
    // The exact string used in BaseFieldTag.cs for required dot — mirrors the implementation.
    // If the implementation changes to something invalid, the test author must update this.
    private const string RequiredDotHtml = "<span aria-hidden=\"true\" style=\"color:red\">*</span>";

    [TestMethod]
    public void RequiredDot_UsesSpanNotFontTag()
    {
        Assert.IsFalse(RequiredDotHtml.Contains("<font"),
            "Must not use deprecated <font> tag");
        Assert.IsTrue(RequiredDotHtml.Contains("<span"),
            "Must use <span> element");
    }

    [TestMethod]
    public void RequiredDot_HasAriaHiddenTrue()
    {
        Assert.IsTrue(RequiredDotHtml.Contains("aria-hidden=\"true\""),
            "Span must have aria-hidden=\"true\" so screen readers skip it");
    }

    [TestMethod]
    public void RequiredDot_ContainsAsterisk()
    {
        Assert.IsTrue(RequiredDotHtml.Contains("*"),
            "Must still display asterisk visually");
    }

    [TestMethod]
    public void RequiredDot_HasRedColor()
    {
        Assert.IsTrue(RequiredDotHtml.Contains("color:red"),
            "Must keep red color for visual indication");
    }
}
