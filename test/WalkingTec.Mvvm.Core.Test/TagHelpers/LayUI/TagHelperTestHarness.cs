using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Localizer that returns the resource key itself (formatted with its arguments), so rendered
    /// HTML is deterministic and independent of the resx files.
    /// </summary>
    public sealed class KeyEchoLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new LocalizedString(name, name);

        public LocalizedString this[string name, params object[] arguments] =>
            new LocalizedString(name, arguments == null || arguments.Length == 0 ? name : string.Format(name, arguments));

        public System.Collections.Generic.IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
            => Array.Empty<LocalizedString>();
    }

    /// <summary>
    /// Test harness that drives LayUI TagHelpers in-process, the same way the Razor runtime does:
    /// build a <see cref="TagHelperContext"/>/<see cref="TagHelperOutput"/>, run ProcessAsync, then
    /// serialise the output (pre-element, start tag, attributes, content, end tag, post-element).
    ///
    /// What the TagHelpers actually depend on (verified against src/WalkingTec.Mvvm.TagHelpers.LayUI):
    ///  - static THProgram._localizer / CoreProgram._localizer (set by <see cref="LayUITagHelperTestBase"/>)
    ///  - ModelExpression for field-bound tags (built via the real ModelExpressionProvider + DataAnnotations metadata)
    ///  - IOptionsMonitor&lt;Configs&gt; for ComboBox / Tree / DateTime / SearchPanel constructors
    ///  - TagHelperContext.Items: "model" (BaseVM), "formid", "ipr", ... (published by wt:form / wt:row)
    /// None of them reads ViewContext / HttpContext, so no ViewContext is needed.
    /// </summary>
    public static class TagHelperTestHarness
    {
        private static readonly IServiceProvider Services = BuildServices();
        private static readonly IModelMetadataProvider MetadataProvider = Services.GetRequiredService<IModelMetadataProvider>();
        private static readonly ModelExpressionProvider ExpressionProvider = new ModelExpressionProvider(MetadataProvider);

        private static IServiceProvider BuildServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMvcCore().AddDataAnnotations();
            return services.BuildServiceProvider();
        }

        #region options

        public static IOptionsMonitor<Configs> Options(Action<Configs> configure = null)
        {
            var configs = new Configs();
            configure?.Invoke(configs);
            return new StaticOptionsMonitor<Configs>(configs);
        }

        private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        {
            public StaticOptionsMonitor(T value) { CurrentValue = value; }
            public T CurrentValue { get; }
            public T Get(string name) => CurrentValue;
            public IDisposable OnChange(Action<T, string> listener) => null;
        }

        #endregion

        #region model expressions

        /// <summary>
        /// Builds a ModelExpression for <c>model.Property</c>, like <c>asp-for</c>/<c>field="Property"</c> does in a view.
        /// </summary>
        public static ModelExpression Field<TModel, TValue>(TModel model, Expression<Func<TModel, TValue>> property)
        {
            var viewData = new ViewDataDictionary<TModel>(MetadataProvider, new ModelStateDictionary()) { Model = model };
            return ExpressionProvider.CreateModelExpression(viewData, property);
        }

        /// <summary>
        /// ModelExpression that points at the model itself (used for the <c>vm</c>/<c>list-vm</c> attributes).
        /// </summary>
        public static ModelExpression Self<TModel>(TModel model, string name = "")
        {
            var explorer = MetadataProvider.GetModelExplorerForType(typeof(TModel), model);
            return new ModelExpression(name, explorer);
        }

        #endregion

        #region context / rendering

        /// <summary>
        /// Items published by wt:form for descendant tags (the "model" key is mandatory for upload tags).
        /// </summary>
        public static Dictionary<object, object> FormItems(BaseVM vm, string formId = null)
        {
            var items = new Dictionary<object, object> { ["model"] = vm };
            if (formId != null)
            {
                items["formid"] = formId;
            }
            return items;
        }

        public static TagHelperContext CreateContext(string tagName, IDictionary<object, object> items = null)
            => new TagHelperContext(tagName, new TagHelperAttributeList(), items ?? new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));

        public static TagHelperOutput CreateOutput(string tagName, string childContent = "", TagMode mode = TagMode.StartTagAndEndTag)
            => new TagHelperOutput(tagName, new TagHelperAttributeList(), (useCachedResult, encoder) =>
            {
                var content = new DefaultTagHelperContent();
                content.AppendHtml(childContent ?? string.Empty);
                return Task.FromResult<TagHelperContent>(content);
            })
            { TagMode = mode };

        /// <summary>
        /// Runs ProcessAsync (which falls back to Process for sync tag helpers) and returns the rendered HTML.
        /// Mirrors the Razor runtime: when the tag helper did not touch Content, the child content is used.
        /// </summary>
        public static string Render(TagHelper helper, string tagName, IDictionary<object, object> items = null, string childContent = "")
            => Render(helper, tagName, items, childContent, out _);

        public static string Render(TagHelper helper, string tagName, IDictionary<object, object> items, string childContent, out TagHelperOutput output)
        {
            var context = CreateContext(tagName, items);
            output = CreateOutput(tagName, childContent);
            helper.Init(context);
            helper.ProcessAsync(context, output).GetAwaiter().GetResult();
            if (!output.IsContentModified)
            {
                output.Content.SetHtmlContent(childContent ?? string.Empty);
            }
            using var writer = new StringWriter();
            output.WriteTo(writer, HtmlEncoder.Default);
            return writer.ToString();
        }

        #endregion
    }

    /// <summary>
    /// Base class: installs a deterministic localizer into the static slots the TagHelpers read from
    /// and restores the previous values afterwards, so tests stay independent of each other.
    /// </summary>
    public abstract class LayUITagHelperTestBase
    {
        private IStringLocalizer _thLocalizer;
        private IStringLocalizer _coreLocalizer;

        [TestInitialize]
        public void InstallLocalizer()
        {
            _thLocalizer = THProgram._localizer;
            _coreLocalizer = CoreProgram._localizer;
            var localizer = new KeyEchoLocalizer();
            THProgram._localizer = localizer;
            CoreProgram._localizer = localizer;
        }

        [TestCleanup]
        public void RestoreLocalizer()
        {
            THProgram._localizer = _thLocalizer;
            CoreProgram._localizer = _coreLocalizer;
        }

        protected static string Render(TagHelper helper, string tagName, IDictionary<object, object> items = null, string childContent = "")
            => TagHelperTestHarness.Render(helper, tagName, items, childContent);

        protected static void AssertContains(string html, params string[] markers)
        {
            foreach (var marker in markers)
            {
                Assert.IsTrue(html.Contains(marker, StringComparison.Ordinal),
                    $"Expected rendered HTML to contain [{marker}] but it was:{Environment.NewLine}{html}");
            }
        }

        protected static void AssertNotContains(string html, params string[] markers)
        {
            foreach (var marker in markers)
            {
                Assert.IsFalse(html.Contains(marker, StringComparison.Ordinal),
                    $"Expected rendered HTML NOT to contain [{marker}] but it was:{Environment.NewLine}{html}");
            }
        }
    }
}
