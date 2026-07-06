#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Test.VM;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

// Issue #601 (#470-F) item 3: SelectorTagHelper used to emit a non-standard
// `<hidden id='{Id}' name='{Field.Name}' />` element. DOMPurify strips the
// whole element (not in ALLOWED_TAGS) in dialog partials/PostForm redraws.
//
// Verdict (dead — evidence in SelectorTagHelper.cs's #601 comment at the
// removal site): `<hidden>` is not a real HTML element, so browsers never
// treat it as form-associated — it never contributed a value to
// FormData/serialize()/`:input`, regardless of DOMPurify. A full-repo sweep
// found exactly one consumer resolving this bare id — ff.clearSelector(id)'s
// `$("#"+id).val("")` in framework_layui.js — which is a write-only no-op:
// nothing ever reads that value back. Converting it to a real
// `<input type="hidden">` (the "alive" remediation) was rejected: for a
// List<T>-bound selector, ASP.NET Core's list model binding aggregates every
// same-named form value, so an extra empty-valued input would inject a
// spurious empty entry into the bound collection on every submit (a genuine
// double-submit regression) — see demo DataPrivilege/Create.cshtml's
// `field="SelectedItemsID"` (a List&lt;string&gt;) for the real-world shape of
// this risk. These tests lock in the removal and the surrounding markup that
// must be unaffected by it.
[TestClass]
public class SelectorTagHelperTests
{
    private sealed class DummyModel
    {
        public List<string>? SelectedIds { get; set; }
    }

    private static void SetupLocalizer()
    {
        var localizerMock = new Mock<Microsoft.Extensions.Localization.IStringLocalizer>();
        localizerMock.Setup(x => x[It.IsAny<string>()])
            .Returns((string s) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        localizerMock.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string s, object[] _) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        THProgram._localizer = localizerMock.Object;
        CoreProgram._localizer = localizerMock.Object;
    }

    private static ModelExpression MakeField(string propertyName, object? modelValue = null)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static ModelExpression MakeVm(object model)
    {
        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(model.GetType());
        var modelExplorer = new ModelExplorer(provider, metadata, model);
        return new ModelExpression(string.Empty, modelExplorer);
    }

    private static ModelExpression MakeStudentField(string propertyName)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(Student).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(Student));
        var modelExplorer = new ModelExplorer(provider, metadata, null);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:selector", new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("input", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static SelectorTagHelper CreateHelper(string id, string seed)
    {
        var listVm = new SelectorStudentListVM
        {
            Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory))
        };
        return new SelectorTagHelper
        {
            Field = MakeField("SelectedIds"),
            ListVM = MakeVm(listVm),
            TextBind = MakeStudentField("Name"),
            Id = id
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }

    [TestMethod]
    public async Task ProcessAsync_NoSelection_NeverEmitsHiddenTag()
    {
        SetupLocalizer();
        var helper = CreateHelper("sel_test1", System.Guid.NewGuid().ToString());
        var output = MakeOutput();

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("<hidden"),
            "The non-standard <hidden> element must no longer be emitted at all — it was dead " +
            "(never form-associated, never read back; see SelectorTagHelper.cs's #601 removal comment)");
    }

    [TestMethod]
    public async Task ProcessAsync_WithSelection_NeverEmitsHiddenTag()
    {
        SetupLocalizer();
        var helper = CreateHelper("sel_test2", System.Guid.NewGuid().ToString());
        helper.Field = MakeField("SelectedIds", new List<string> { "1", "2" });
        var output = MakeOutput();

        await helper.ProcessAsync(MakeContext(), output);

        var postHtml = output.PostElement.GetContent();
        Assert.IsFalse(postHtml.Contains("<hidden"),
            "The <hidden> element must be absent regardless of whether the selector has a current selection");
    }

    [TestMethod]
    public async Task ProcessAsync_ContainerDivAndRealHiddenInputsStillEmitted()
    {
        // Regression guard: removing the dead <hidden id='{Id}' .../> element
        // must not disturb the REAL value-carrying markup — the
        // #{Id}_Container div (which framework_layui.js's write-back path at
        // framework_layui.js:2621 resolves via
        // `#{Id}_Container input[type=hidden]`) and its closing tag.
        SetupLocalizer();
        var helper = CreateHelper("sel_test3", System.Guid.NewGuid().ToString());
        var output = MakeOutput();

        await helper.ProcessAsync(MakeContext(), output);

        var preHtml = output.PreElement.GetContent();
        var postHtml = output.PostElement.GetContent();
        StringAssert.Contains(preHtml, "id=\"sel_test3_Container\"",
            "The value-carrying container div must still be emitted");
        StringAssert.Contains(postHtml, "</div>",
            "The container div's closing tag must still be emitted immediately after the per-item hidden inputs");
    }

    [TestMethod]
    public async Task ProcessAsync_DisplayInputStillHasCorrectIdAndName()
    {
        SetupLocalizer();
        var helper = CreateHelper("sel_test4", System.Guid.NewGuid().ToString());
        var output = MakeOutput();

        await helper.ProcessAsync(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("id"));
        Assert.AreEqual("sel_test4_Display", output.Attributes["id"].Value);
        Assert.IsTrue(output.Attributes.ContainsName("name"));
        Assert.AreEqual("SelectedIds_Display", output.Attributes["name"].Value);
    }
}
