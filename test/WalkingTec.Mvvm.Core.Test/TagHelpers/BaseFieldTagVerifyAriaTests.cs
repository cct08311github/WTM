#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Verifies Feature 1 (EnableAutoVerify lay-verify projection) and
/// Feature 2 (EnableAria ARIA attributes) added to BaseFieldTag.
/// Both features default to false (off) to preserve existing behavior.
/// </summary>
[TestClass]
public class BaseFieldTagVerifyAriaTests
{
    // ─── Model fixtures ──────────────────────────────────────────────────

    private sealed class VerifyModel
    {
        [Required]
        public string? RequiredField { get; set; }

        [EmailAddress]
        public string? EmailField { get; set; }

        [Url]
        public string? UrlField { get; set; }

        [Phone]
        public string? PhoneField { get; set; }

        public int NumericField { get; set; }

        public decimal DecimalField { get; set; }

        [RegularExpression(@"^\d{4}$", ErrorMessage = "Must be 4 digits")]
        public string? RegexField { get; set; }

        [StringLength(100, MinimumLength = 3)]
        public string? StringLengthField { get; set; }

        [MaxLength(50)]
        public string? MaxLengthField { get; set; }

        [MinLength(2)]
        public string? MinLengthField { get; set; }

        public string? PlainField { get; set; }

        // For ARIA tests
        public string? HiddenLabelField { get; set; }
    }

    // ─── Helper builders ─────────────────────────────────────────────────

    private static void SetupLocalizer()
    {
        var localizerMock = new Mock<Microsoft.Extensions.Localization.IStringLocalizer>();
        localizerMock
            .Setup(x => x[It.IsAny<string>()])
            .Returns((string s) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        localizerMock
            .Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string s, object[] _) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        THProgram._localizer = localizerMock.Object;
    }

    private static ModelExpression MakeFieldExpression(string propertyName)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(VerifyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(VerifyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, null);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext(Dictionary<object, object>? items = null)
        => new("wt:textbox",
               new TagHelperAttributeList(),
               items ?? new Dictionary<object, object>(),
               "test-id");

    private static TagHelperOutput MakeOutput()
        => new("input",
               new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestCleanup]
    public void Cleanup()
    {
        // Reset static options after every test to prevent cross-test interference.
        BaseFieldTag.SetUIOptions(new WtmUIOptions());
    }

    // ─── Feature 1: EnableAutoVerify ─────────────────────────────────────

    [TestMethod]
    public void AutoVerify_DefaultOff_NoLayVerifyAdded()
    {
        // Default WtmUIOptions has EnableAutoVerify=false — no lay-verify should appear
        // for a field that only has [EmailAddress] (not [Required]).
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("EmailField"),
            Id = "field_email_off",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        // With EnableAutoVerify off, lay-verify must NOT be added for EmailAddress
        if (output.Attributes.ContainsName("lay-verify"))
        {
            var verifyVal = output.Attributes["lay-verify"].Value?.ToString() ?? string.Empty;
            Assert.IsFalse(verifyVal.Contains("email"),
                "email token must NOT appear in lay-verify when EnableAutoVerify is off");
        }
        // If attribute is absent entirely, that also passes (no lay-verify at all is fine).
    }

    [TestMethod]
    public void AutoVerify_Required_AppendsRequiredToken()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("RequiredField"),
            Id = "field_req",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("lay-verify"),
            "lay-verify must be present for a [Required] field");
        var verifyVal = output.Attributes["lay-verify"].Value?.ToString() ?? string.Empty;
        Assert.IsTrue(verifyVal.Contains("required"),
            "required token must be in lay-verify");
    }

    [TestMethod]
    public void AutoVerify_Email_AppendsEmailToken()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("EmailField"),
            Id = "field_email",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("lay-verify"),
            "lay-verify must be present");
        var verifyVal = output.Attributes["lay-verify"].Value?.ToString() ?? string.Empty;
        Assert.IsTrue(verifyVal.Contains("email"),
            "email token must be in lay-verify for [EmailAddress] field");
    }

    [TestMethod]
    public void AutoVerify_Url_AppendsUrlToken()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("UrlField"),
            Id = "field_url",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var verifyVal = output.Attributes.ContainsName("lay-verify")
            ? output.Attributes["lay-verify"].Value?.ToString() ?? string.Empty
            : string.Empty;
        Assert.IsTrue(verifyVal.Contains("url"),
            "url token must be in lay-verify for [Url] field");
    }

    [TestMethod]
    public void AutoVerify_Numeric_AppendsnumberToken()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("NumericField"),
            Id = "field_numeric",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var verifyVal = output.Attributes.ContainsName("lay-verify")
            ? output.Attributes["lay-verify"].Value?.ToString() ?? string.Empty
            : string.Empty;
        Assert.IsTrue(verifyVal.Contains("number"),
            "number token must be in lay-verify for int property");
    }

    [TestMethod]
    public void AutoVerify_Decimal_AppendsnumberToken()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("DecimalField"),
            Id = "field_decimal",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var verifyVal = output.Attributes.ContainsName("lay-verify")
            ? output.Attributes["lay-verify"].Value?.ToString() ?? string.Empty
            : string.Empty;
        Assert.IsTrue(verifyVal.Contains("number"),
            "number token must be in lay-verify for decimal property");
    }

    [TestMethod]
    public void AutoVerify_StringLength_EmitsMaxlength()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("StringLengthField"),
            Id = "field_strlen",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("maxlength"),
            "maxlength must be emitted for [StringLength(100,...)]");
        Assert.AreEqual("100", output.Attributes["maxlength"].Value?.ToString(),
            "maxlength must be 100");
    }

    [TestMethod]
    public void AutoVerify_StringLength_EmitsMinlength()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("StringLengthField"),
            Id = "field_strlen_min",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("minlength"),
            "minlength must be emitted for [StringLength(100, MinimumLength = 3)]");
        Assert.AreEqual("3", output.Attributes["minlength"].Value?.ToString(),
            "minlength must be 3");
    }

    [TestMethod]
    public void AutoVerify_MaxLength_EmitsMaxlength()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("MaxLengthField"),
            Id = "field_maxlen",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("maxlength"),
            "maxlength must be emitted for [MaxLength(50)]");
        Assert.AreEqual("50", output.Attributes["maxlength"].Value?.ToString(),
            "maxlength must be 50");
    }

    [TestMethod]
    public void AutoVerify_MinLength_EmitsMinlength()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("MinLengthField"),
            Id = "field_minlen",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("minlength"),
            "minlength must be emitted for [MinLength(2)]");
        Assert.AreEqual("2", output.Attributes["minlength"].Value?.ToString(),
            "minlength must be 2");
    }

    [TestMethod]
    public void AutoVerify_PlainField_NoVerifyAdded()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAutoVerify = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("PlainField"),
            Id = "field_plain_verify",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        // PlainField is a string with no validation attributes — lay-verify must be absent
        // (string is not numeric so no "number" token either)
        if (output.Attributes.ContainsName("lay-verify"))
        {
            var verifyVal = output.Attributes["lay-verify"].Value?.ToString() ?? string.Empty;
            Assert.IsTrue(string.IsNullOrEmpty(verifyVal.Trim(',').Trim()),
                "PlainField (no validation attrs) must not produce lay-verify tokens");
        }
    }

    // ─── Feature 2: EnableAria ────────────────────────────────────────────

    [TestMethod]
    public void Aria_DefaultOff_NoAriaAttributes()
    {
        SetupLocalizer();
        // EnableAria defaults to false — no aria-label, aria-describedby, aria-invalid
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("PlainField"),
            Id = "field_aria_off",
            LabelText = "My Label",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("aria-label"),
            "aria-label must NOT be added when EnableAria is off");
        Assert.IsFalse(output.Attributes.ContainsName("aria-describedby"),
            "aria-describedby must NOT be added when EnableAria is off");
        Assert.IsFalse(output.Attributes.ContainsName("aria-invalid"),
            "aria-invalid must NOT be added when EnableAria is off");
    }

    [TestMethod]
    public void Aria_Enabled_AriaInvalidEmitted()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAria = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("PlainField"),
            Id = "field_aria_invalid",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("aria-invalid"),
            "aria-invalid must be present when EnableAria is true");
        Assert.AreEqual("false", output.Attributes["aria-invalid"].Value?.ToString(),
            "aria-invalid initial value must be false");
    }

    [TestMethod]
    public void Aria_Enabled_HideLabelTrue_AriaLabelEmitted()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAria = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("HiddenLabelField"),
            Id = "field_hidden",
            HideLabel = true,
            LabelText = "Hidden Field Label",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("aria-label"),
            "aria-label must be emitted when EnableAria=true and HideLabel=true");
        Assert.AreEqual("Hidden Field Label",
            output.Attributes["aria-label"].Value?.ToString(),
            "aria-label value must equal LabelText");
    }

    [TestMethod]
    public void Aria_Enabled_HideLabelFalse_NoAriaLabel()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAria = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("PlainField"),
            Id = "field_visible",
            HideLabel = false,
            LabelText = "Visible Label",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("aria-label"),
            "aria-label must NOT be added when the label is visible (HideLabel=false)");
    }

    [TestMethod]
    public void Aria_Label_HtmlEncoded_SpecialChars()
    {
        SetupLocalizer();
        BaseFieldTag.SetUIOptions(new WtmUIOptions { EnableAria = true });
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("HiddenLabelField"),
            Id = "field_encoded",
            HideLabel = true,
            LabelText = "<script>alert('xss')</script>",
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("aria-label"),
            "aria-label must be present");
        var ariaLabel = output.Attributes["aria-label"].Value?.ToString() ?? string.Empty;
        Assert.IsFalse(ariaLabel.Contains("<script>"),
            "Raw <script> tag must not appear in aria-label (must be HTML-encoded)");
        Assert.IsTrue(ariaLabel.Contains("&lt;script&gt;") || ariaLabel.Contains("&lt;"),
            "aria-label must be HTML-encoded");
    }
}
