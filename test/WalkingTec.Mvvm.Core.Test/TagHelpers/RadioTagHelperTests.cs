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

// Issue #638: RadioTagHelper's per-widget
//   {Id}defaultvalues = [...];
// inline <script> used to be re-emitted ONCE PER RENDERED <input> — inside
// the same for-loop that built the radio <input> elements — instead of once,
// unconditionally, like CheckBoxTagHelper. That meant:
//   - N rendered items -> N redundant (byte-identical) writes, and
//   - 0 rendered items (e.g. the item-url branch, which never populates
//     listItems) -> the global was NEVER published at all, so a ChainChange
//     TARGET radio silently had no defaults to read (the framework_layui.js
//     half of #638, fixed independently, guards the CONSEQUENCE of that gap;
//     this file locks in the EMITTER-side fix: the script moves to
//     PostElement, unconditional, matching CheckBoxTagHelper).
[TestClass]
public class RadioTagHelperTests
{
    private enum Role
    {
        Admin,
        User,
        Guest
    }

    private sealed class DummyModel
    {
        public Role RoleField { get; set; }

        public bool Flag { get; set; }
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
        // Deliberately uses the (containerType, propertyName) overload — see
        // CheckBoxTagHelperTests for the full rationale (RadioTagHelper branches
        // heavily on Field.Metadata.ModelType, which needs the property-level
        // type, not the container-level type the PropertyInfo overload returns).
        var metadata = provider.GetMetadataForProperty(typeof(DummyModel), propertyName);
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static TagHelperContext MakeContext()
        => new("wt:radio", new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    [TestMethod]
    public void Process_WithMultipleItems_EmitsDefaultValuesScriptExactlyOnce()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.User),
            Id = "radio_multi"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        // RoleField is an enum -> ToListItems() renders one <input> per enum
        // value (3: Admin/User/Guest), so the old per-item loop would have
        // emitted the script 3 times.
        var postContent = output.PostContent.GetContent();
        var postElement = output.PostElement.GetContent();

        Assert.AreEqual(1, CountOccurrences(postElement, "radio_multidefaultvalues ="),
            "defaultvalues script must be emitted exactly once, in PostElement");
        Assert.AreEqual(0, CountOccurrences(postContent, "defaultvalues"),
            "PostContent (the per-item loop) must no longer contain the defaultvalues script");

        var inputCount = CountOccurrences(postContent, "<input type=\"radio\"");
        Assert.AreEqual(3, inputCount, "sanity check: 3 radio <input> elements are still rendered");
    }

    [TestMethod]
    public void Process_WithItemUrl_ZeroStaticItems_StillEmitsDefaultValuesScriptExactlyOnce()
    {
        // Issue #638: the item-url branch never populates listItems, so the OLD
        // per-item loop never ran and the global was never published at all —
        // the reachable "chained target has no defaults" configuration that made
        // ff.ChainChange's usedefaultvalue=true path crash. After the fix, the
        // unconditional PostElement emission covers this branch too.
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Admin),
            Id = "radio_itemurl",
            ItemUrl = "/api/roles"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        var postContent = output.PostContent.GetContent();
        var postElement = output.PostElement.GetContent();

        Assert.AreEqual(0, CountOccurrences(postContent, "<input type=\"radio\""),
            "sanity check: item-url branch renders zero static <input> elements");
        Assert.AreEqual(1, CountOccurrences(postElement, "radio_itemurldefaultvalues ="),
            "defaultvalues script must still be emitted exactly once even with 0 rendered items");
    }

    [TestMethod]
    public void Process_DefaultValuesScriptIsInPostElementNotPostContent()
    {
        SetupLocalizer();
        var helper = new RadioTagHelper
        {
            Field = MakeField("RoleField", Role.Guest),
            Id = "radio_location"
        };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        StringAssert.DoesNotMatch(output.PostContent.GetContent(), new System.Text.RegularExpressions.Regex("defaultvalues"));
        StringAssert.Matches(output.PostElement.GetContent(), new System.Text.RegularExpressions.Regex("radio_locationdefaultvalues ="));
    }

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
