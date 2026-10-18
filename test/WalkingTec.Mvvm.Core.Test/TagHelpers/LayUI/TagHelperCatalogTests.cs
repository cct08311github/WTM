using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Guards the public tag surface (&lt;wt:xxx&gt;) of the LayUI assembly that downstream Razor views depend on.
    /// Removing or renaming a tag helper breaks every view that uses it, so it must fail CI.
    /// </summary>
    [TestClass]
    public class TagHelperCatalogTests
    {
        private static readonly string[] ExpectedTags =
        {
            // form fields
            "wt:textbox", "wt:textarea", "wt:hidden", "wt:display", "wt:combobox", "wt:checkbox", "wt:radio", "wt:switch",
            "wt:datetime", "wt:selector", "wt:upload", "wt:multiupload", "wt:richtextbox", "wt:ueditor", "wt:colorpicker",
            "wt:slider", "wt:transfer", "wt:tree", "wt:image",
            // form + buttons
            "wt:form", "wt:searchpanel", "wt:submitbutton", "wt:resetbutton", "wt:closebutton", "wt:button", "wt:linkbutton",
            "wt:downloadTemplateButton",
            // data display / layout
            "wt:grid", "wt:chart", "wt:tab", "wt:tabheaders", "wt:tabheader", "wt:tabcontents", "wt:tabcontent",
            "wt:card", "wt:panel", "wt:row", "wt:container", "wt:fieldset", "wt:treecontainer", "wt:quote", "wt:code", "wt:flowinfo",
        };

        private static IEnumerable<(Type Type, string Tag)> AllTargets()
        {
            return typeof(TextBoxTagHelper).Assembly.GetTypes()
                .Where(t => typeof(TagHelper).IsAssignableFrom(t) && !t.IsAbstract)
                .SelectMany(t => t.GetCustomAttributes<HtmlTargetElementAttribute>().Select(a => (t, a.Tag)));
        }

        [TestMethod]
        public void LayUIAssembly_ExposesAllExpectedWtTags()
        {
            var tags = AllTargets().Select(x => x.Tag).ToHashSet(StringComparer.Ordinal);

            var missing = ExpectedTags.Where(t => !tags.Contains(t)).ToList();

            Assert.AreEqual(0, missing.Count, "Missing LayUI tag helpers: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void EveryTagHelperTargetsAWtPrefixedElement()
        {
            var bad = AllTargets().Where(x => !x.Tag.StartsWith("wt:", StringComparison.Ordinal)).ToList();

            Assert.AreEqual(0, bad.Count, "Non wt: tag targets: " + string.Join(", ", bad.Select(x => x.Type.Name + "=" + x.Tag)));
        }

        [TestMethod]
        [DataRow(typeof(TextBoxTagHelper), "wt:textbox")]
        [DataRow(typeof(ComboBoxTagHelper), "wt:combobox")]
        [DataRow(typeof(DataTableTagHelper), "wt:grid")]
        [DataRow(typeof(FormTagHelper), "wt:form")]
        [DataRow(typeof(SearchPanelTagHelper), "wt:searchpanel")]
        [DataRow(typeof(SubmitButtonTagHelper), "wt:submitbutton")]
        [DataRow(typeof(TreeTagHelper), "wt:tree")]
        public void TagHelperType_KeepsItsTagName(Type type, string tag)
        {
            var targets = type.GetCustomAttributes<HtmlTargetElementAttribute>().Select(a => a.Tag).ToList();

            CollectionAssert.Contains(targets, tag);
        }
    }
}
