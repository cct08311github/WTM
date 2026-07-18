#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice G: TextAreaTagHelper's ShowCounter feature migrates off
/// the per-widget inline &lt;script&gt;wtmCounter.init(id, counterId, maxLen)&lt;/script&gt;
/// call onto a data-wtm-counter="&lt;counterId&gt;" attribute, consumed by a
/// SINGLE document-level delegated 'input' listener in framework_layui.js
/// (see framework_layui_470_sliceG_islandify_strays.test.js for the JS-side
/// behaviour). No island needed for a one-liner.
/// </summary>
[TestClass]
public class TextAreaCounterTagHelper470Tests
{
    private sealed class DummyModel
    {
        [StringLength(50)]
        public string? Bio { get; set; }

        // No [StringLength]/[MaxLength] — used to verify the counter is
        // silently skipped without either annotation (pre-existing
        // behaviour, unchanged by this slice).
        public string? Unannotated { get; set; }
    }

    // EmptyModelMetadataProvider (used by every other TagHelper test file in
    // this project) deliberately returns an EMPTY ValidatorMetadata list —
    // it never runs DataAnnotations attribute scanning at all, by design.
    // TextAreaTagHelper.GetMaxLength() reads real [StringLength]/[MaxLength]
    // attributes off ValidatorMetadata, so these tests need an
    // IModelMetadataProvider that actually wires up DataAnnotations —
    // obtained the standard ASP.NET Core way, via AddMvcCore().AddDataAnnotations().
    private static readonly IModelMetadataProvider _metadataProvider =
        new ServiceCollection()
            .AddLogging()
            .AddMvcCore()
            .AddDataAnnotations()
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IModelMetadataProvider>();

    private static ModelExpression MakeField(string propertyName, object? modelValue = null)
    {
        var metadata = _metadataProvider.GetMetadataForProperty(typeof(DummyModel), propertyName);
        var modelExplorer = new ModelExplorer(_metadataProvider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:textarea", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("textarea", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestMethod]
    public void ShowCounter_EmitsDataAttributeNotInlineScript()
    {
        var helper = new TextAreaTagHelper
        {
            Field = MakeField("Bio", "hello"),
            Id = "bio_1",
            ShowCounter = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-counter"),
            "ShowCounter must emit a data-wtm-counter attribute on the textarea");
        Assert.AreEqual("bio_1_counter", output.Attributes["data-wtm-counter"].Value?.ToString());

        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtmCounter.init("),
            "Must not emit the legacy inline wtmCounter.init(...) call text anywhere");
        Assert.IsFalse(postHtml.Contains("<script>"),
            "ShowCounter must not emit any inline <script> at all — only the data attribute + counter span");
    }

    [TestMethod]
    public void ShowCounter_EmitsCounterSpanWithInitialCount()
    {
        var helper = new TextAreaTagHelper
        {
            Field = MakeField("Bio", "hello"),
            Id = "bio_2",
            ShowCounter = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "id=\"bio_2_counter\"");
        StringAssert.Contains(postHtml, "5/50",
            "Initial counter text must reflect the current model value's length / maxlength");
    }

    [TestMethod]
    public void ShowCounter_MaxLengthAttributeStillSet()
    {
        // The data-wtm-counter listener reads maxLen from the textarea's own
        // native maxLength DOM property — this attribute must still be
        // emitted (untouched by this slice) or the delegated listener would
        // have nothing to read.
        var helper = new TextAreaTagHelper
        {
            Field = MakeField("Bio", "hello"),
            Id = "bio_3",
            ShowCounter = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("maxlength"));
        Assert.AreEqual("50", output.Attributes["maxlength"].Value?.ToString());
    }

    [TestMethod]
    public void ShowCounterFalse_NoDataAttributeNoCounterSpan()
    {
        var helper = new TextAreaTagHelper
        {
            Field = MakeField("Bio", "hello"),
            Id = "bio_4",
            ShowCounter = false
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-counter"),
            "ShowCounter=false (default) must not emit the data attribute");
        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("wtm-char-counter"),
            "ShowCounter=false must not emit a counter span");
    }

    [TestMethod]
    public void ShowCounterTrue_NoMaxLengthAnnotation_NoDataAttribute()
    {
        // ShowCounter requires [StringLength]/[MaxLength] on the field —
        // without one, GetMaxLength() returns null and the counter is
        // silently skipped (pre-existing behaviour, unchanged by this slice).
        var helper = new TextAreaTagHelper
        {
            Field = MakeField("Unannotated", "x"),
            Id = "bio_5",
            ShowCounter = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsFalse(output.Attributes.ContainsName("data-wtm-counter"));
    }

    [TestMethod]
    public void ShowCounter_CounterIdIsHtmlEncoded()
    {
        // Regression guard for the pre-existing HtmlEncoder.Default.Encode(...)
        // call this slice preserves for the counter <span id="..."> — the raw
        // (unencoded) id is used for the data-wtm-counter ATTRIBUTE value
        // (output.Attributes.Add HTML-encodes automatically), while the
        // manually-built <span id="..."> markup still needs the explicit
        // encode call.
        var helper = new TextAreaTagHelper
        {
            Field = MakeField("Bio", "x"),
            Id = "bio\"6",
            ShowCounter = true
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);
        var postHtml = output.PostElement.GetContent();

        Assert.IsFalse(postHtml.Contains("id=\"bio\"6_counter\""),
            "Raw quote must never break out of the span id attribute");
    }
}
