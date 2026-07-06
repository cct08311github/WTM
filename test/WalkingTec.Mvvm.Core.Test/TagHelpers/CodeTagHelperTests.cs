using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

// Issue #601 (#470-F) item 2: CodeTagHelper previously emitted a bare `encode`
// attribute, which the vendored layui.code module never reads on EITHER
// bundled tree — 2.6.3's demo/.../layui/lay/modules/code.js reads
// `c.attr("lay-encode")||e.encode`, and the 2.13.8 layui-next bundle's option
// loop reads `attr("lay-"+"encode")` — both are `lay-encode`, never bare
// `encode`. The attribute was therefore ALREADY silently inert even outside
// dialogs; #601 renames the emission to restore the intended
// escape-code-sample behavior, and adds a matching framework_layui.js
// ADD_ATTR entry (alongside CodeTagHelper's already-allowlisted lay-height/
// lay-title siblings) so it also survives ff.SafeHtml in dialog partials.
[TestClass]
public class CodeTagHelperTests
{
    private static TagHelperContext MakeContext()
        => new("wt:code", new TagHelperAttributeList(), new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("pre", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    [TestMethod]
    public void Process_EmitsLayEncodeAttribute_NotBareEncode()
    {
        var helper = new CodeTagHelper();
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("lay-encode"),
            "Must emit lay-encode — the attribute name the vendored layui.code module actually reads on both trees");
        Assert.IsFalse(output.Attributes.ContainsName("encode"),
            "Must NOT emit the bare `encode` attribute — neither vendored layui.code module reads it");
        Assert.AreEqual(true, output.Attributes["lay-encode"].Value);
    }

    [TestMethod]
    public void Process_HeightSet_EmitsLayHeightAttribute()
    {
        var helper = new CodeTagHelper { Height = 300 };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("lay-height"));
        Assert.AreEqual("300px", output.Attributes["lay-height"].Value);
    }

    [TestMethod]
    public void Process_TitleSet_EmitsLayTitleAttribute()
    {
        var helper = new CodeTagHelper { Title = "Example" };
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("lay-title"));
        Assert.AreEqual("Example", output.Attributes["lay-title"].Value);
    }

    [TestMethod]
    public void Process_AlwaysEmitsLayuiCodeClass()
    {
        var helper = new CodeTagHelper();
        var output = MakeOutput();
        helper.Process(MakeContext(), output);

        Assert.IsTrue(output.Attributes.ContainsName("class"));
        Assert.AreEqual("layui-code", output.Attributes["class"].Value);
    }
}
