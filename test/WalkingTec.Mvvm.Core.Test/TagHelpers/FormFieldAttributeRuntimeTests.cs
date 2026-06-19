#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Verifies that [FormField] attribute defaults are applied at runtime by
/// TextBoxTagHelper (via BaseFieldTag.Process).  Tests focus on:
///   - Placeholder seeded from attribute when EmptyText is not set
///   - Explicit EmptyText in markup wins over the attribute
///   - No change when [FormField] is absent
/// ReadonlyOnEdit is covered by a structural / integration-style test since
/// it requires a live BaseCRUDVM in the context items.
/// </summary>
[TestClass]
public class FormFieldAttributeRuntimeTests
{
    // ─── Model fixtures ──────────────────────────────────────────────────

    private sealed class AnnotatedModel
    {
        [FormField(Placeholder = "Enter your name")]
        public string? AnnotatedField { get; set; }

        /// <summary>No [FormField] — must be a no-op.</summary>
        public string? PlainField { get; set; }
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

    /// <summary>
    /// Build a ModelExpression that points at the named property on AnnotatedModel.
    /// The EmptyModelMetadataProvider preserves ContainerType and PropertyName so
    /// GetSingleProperty() can reach the [FormField] attribute via reflection.
    /// </summary>
    private static ModelExpression MakeFieldExpression(string propertyName)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(AnnotatedModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(AnnotatedModel));
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

    // ─── Placeholder tests ────────────────────────────────────────────────

    [TestMethod]
    public void FormField_Placeholder_SeededWhenEmptyTextIsNull()
    {
        // Arrange
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("AnnotatedField"),
            Id = "field_annotated",          // Must be set explicitly to avoid null Container.ModelType
            // EmptyText is deliberately left null — attribute should supply placeholder.
        };
        var context = MakeContext();
        var output = MakeOutput();

        // Act
        helper.Process(context, output);

        // Assert — the attribute placeholder must appear in the output.
        Assert.IsTrue(
            output.Attributes.ContainsName("placeholder"),
            "placeholder attribute must be present");
        Assert.AreEqual(
            "Enter your name",
            output.Attributes["placeholder"].Value?.ToString(),
            "placeholder value must come from [FormField].Placeholder");
    }

    [TestMethod]
    public void FormField_Placeholder_NotApplied_WhenEmptyTextIsSet()
    {
        // Arrange — explicit EmptyText must win over the attribute.
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("AnnotatedField"),
            Id = "field_annotated_explicit",
            EmptyText = "Explicit placeholder from markup",
        };
        var context = MakeContext();
        var output = MakeOutput();

        // Act
        helper.Process(context, output);

        // Assert — the explicit EmptyText must survive; the attribute must not overwrite it.
        Assert.AreEqual(
            "Explicit placeholder from markup",
            output.Attributes["placeholder"].Value?.ToString(),
            "Explicit EmptyText must win over [FormField].Placeholder");
    }

    [TestMethod]
    public void NoFormFieldAttribute_PlaceholderRemainEmpty()
    {
        // Arrange — property has no [FormField], EmptyText is null.
        SetupLocalizer();
        var helper = new TextBoxTagHelper
        {
            Field = MakeFieldExpression("PlainField"),
            Id = "field_plain",
        };
        var context = MakeContext();
        var output = MakeOutput();

        // Act
        helper.Process(context, output);

        // Assert — placeholder must remain the empty-string default (no regression).
        var placeholderValue = output.Attributes.ContainsName("placeholder")
            ? output.Attributes["placeholder"].Value?.ToString()
            : null;
        Assert.IsTrue(
            string.IsNullOrEmpty(placeholderValue),
            "absent [FormField] must not inject a placeholder (regression guard)");
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
    }
}
