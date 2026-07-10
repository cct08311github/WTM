using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Attributes;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:checkbox", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class CheckBoxTagHelper : BaseFieldTag
    {
        // Issue #633 (#470-F): see ComboBoxTagHelper's _islandJsonOptions for the
        // full rationale (same shared LoadComboItemsIslandAction DTO, defined in
        // ComboBoxTagHelper.cs). This TagHelper is the one emitter that DOES set
        // Disabled — WhenWritingNull only matters here for staying byte-identical
        // with the other three emitters' options object shape.
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// 选项
        /// </summary>
        public ModelExpression Items { get; set; }

        /// <summary>
        /// 改变选择时触发的js函数，func(data)格式;
        /// <para>
        /// data.elem得到checkbox原始DOM对象
        /// </para>
        /// <para>
        /// data.elem.checked是否被选中，true或者false
        /// </para>
        /// <para>
        /// data.value复选框value值，也可以通过data.elem.value得到
        /// </para>
        /// <para>
        /// othis得到美化后的DOM对象
        /// </para>
        /// </summary>
        public string ChangeFunc { get; set; }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var modelType = Field.Metadata.ModelType;
            List<ComboSelectListItem> listItems = [];
            List<string> values = [];
            if (Field?.Name?.Contains("[") == true)
            {
                values.AddRange(Field.ModelExplorer.Container.Model.GetPropertySiblingValues(Field.Name));
            }
            else
            {
                if (modelType.IsList())
                {
                    var ilist = Field.Model as IList;
                    if (ilist != null)
                    {
                        foreach (var item in ilist)
                        {
                            values.Add(item.ToString());
                        }
                    }
                }
                else if (modelType.IsBoolOrNullableBool())
                {
                    values.Add(Field.Model.ToString());
                }
                else
                {
                    if (Field.Model != null)
                    {
                        values.Add(Field.Model.ToString());
                    }
                }
            }

            if (values.Count == 0)
            {
                if (DefaultValue != null)
                {
                    values = DefaultValue.Split(',').ToList();
                }
            }

            if (string.IsNullOrEmpty(ItemUrl) == false)
            {
                foreach (var item in values)
                {
                    listItems.Add(new ComboSelectListItem
                    {
                        Text = "",
                        Value = item?.ToString(),
                        Selected = true
                    });

                }
                // Issue #633 (#470-F): eval-free JSON island — see ComboBoxTagHelper's
                // matching comment for the full rationale (shared action shape, JSON
                // escaping, and the async-ajax timing argument, which applies
                // identically here: LoadComboItems('checkbox', ...) only mutates the
                // DOM from inside its $.get callback, well after both this island's
                // dispatch and the placeholder <input> elements above already exist).
                var loadComboItemsAction = new LoadComboItemsIslandAction
                {
                    ControlType = "checkbox",
                    Url = ItemUrl,
                    Id = Id,
                    Field = Field.Name,
                    SelectVal = values,
                    Disabled = Disabled
                };
                output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{JsonSerializer.Serialize(loadComboItemsAction, _islandJsonOptions)}</script>");
            }
            else
            {

                if (Items?.Model == null)
                {
                    if (modelType.IsList())
                    {
                        var innerType = modelType.GetGenericArguments()[0];
                        if (innerType.IsEnumOrNullableEnum())
                        {
                            listItems = innerType.ToListItems();
                        }
                    }
                    else if (modelType.IsBoolOrNullableBool())
                    {
                        listItems = [new ComboSelectListItem { Value = "true", Text = "|" }];
                    }
                }
                else
                {
                    if (typeof(IEnumerable<ComboSelectListItem>).IsAssignableFrom(Items.Metadata.ModelType))
                    {
                        if (typeof(IEnumerable<TreeSelectListItem>).IsAssignableFrom(Items.Metadata.ModelType))
                        {
                            listItems = (Items.Model as IEnumerable<TreeSelectListItem>).FlatTreeSelectList().Cast<ComboSelectListItem>().ToList();
                        }
                        else
                        {
                            listItems = (Items.Model as IEnumerable<ComboSelectListItem>).ToList();
                        }
                    }
                    else if (Items.Metadata.ModelType.IsList())
                    {
                        var exports = (Items.Model as IList);
                        foreach (var item in exports)
                        {
                            listItems.Add(new ComboSelectListItem
                            {
                                Text = item?.ToString(),
                                Value = item?.ToString()
                            });
                        }
                    }
                }
                SetSelected(listItems, values);
            }
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Clear();
            output.Attributes.Add("div-for", "checkbox");
            output.Attributes.Add("wtm-ctype", "checkbox");
            output.Attributes.Add("wtm-name", Field.Name);
            // Issue #632 (redesigned — data as markup, not script): the field's
            // default selection travels as a data-wtm-defaults attribute on THIS
            // SAME div — the element ff.ChainChange already resolves as `target`
            // via `$('#'+formid).find('#'+linkto.value)`, since BaseFieldTag sets
            // `id="{Id}"` on it. It is present synchronously, at HTML-parse time,
            // on every render path (full page, dialog fragment, PostForm redraw) —
            // no DOMContentLoaded/island-dispatch timing dependency, which is
            // exactly what the REJECTED fieldDefaults-island-as-authoritative
            // design could not guarantee (see the commit body for the race that
            // design lost to: the island write happens at DOMContentLoaded, but
            // the only reader — ff.ChainChange — can fire earlier via a
            // setTimeout(..., 100) anchored at the SOURCE widget's parse point).
            // `target.html('')` in ChainChange's "clear" step only ever clears
            // this div's CHILDREN, never the div itself or its attributes, so the
            // attribute survives ChainChange's own clear-and-repopulate cycle too.
            // Serialized with the same HTML-safe default JSON encoder used
            // elsewhere in this file (escapes '<'/'>'/'&' as \uXXXX) and then
            // HTML-attribute-encoded by ASP.NET Core's TagHelperOutput.Attributes
            // pipeline (a plain string Add(), never raw HtmlContent) — a value
            // containing '"' cannot break out of the attribute.
            output.Attributes.Add("data-wtm-defaults", JsonSerializer.Serialize(values, _islandJsonOptions));

            if (string.IsNullOrEmpty(ChangeFunc) == false)
            {
                output.Attributes.Add("wtm-cf", FormatFuncName(ChangeFunc, false));
            }
            for (int i = 0; i < listItems.Count; i++)
            {
                var item = listItems[i];
                var selected = item.Selected ? " checked" : " ";
                // TLU-SEC-001: HtmlEncode item.Value and item.Text before interpolating into
                // HTML attribute values. Without encoding, a list item whose Value or Text
                // contains quotes or angle brackets can break attribute boundaries (XSS).
                output.PostContent.AppendHtml($@"
<input type=""checkbox"" name=""{Field.Name}"" value=""{WebUtility.HtmlEncode(item.Value)}"" title=""{WebUtility.HtmlEncode(item.Text)}"" {selected} {(Disabled ? "disabled" : string.Empty)}/>");
            }
            // Issue #632 (redesigned): BACK-COMPAT — app-authored JS that reads
            // window[Id + 'defaultvalues'] directly still gets it published.
            //
            // Issue #646 (Codex adversarial review, pre-10.14.4): #632 replaced the
            // legacy inline <script>{Id}defaultvalues=...} with ONLY the eval-free
            // wtm-dialog-init JSON island below, which on a full page is consumed at
            // DOMContentLoaded (ff._consumePageReadyIslands). That broke app code that
            // reads the global from an inline <script> immediately AFTER this widget's
            // markup — it saw `undefined` until DOMContentLoaded, a real timing
            // regression vs. the pre-#632 synchronous, parse-time publication. The
            // FRAMEWORK's own consumer was and remains unaffected — ff.ChainChange
            // reads the race-free data-wtm-defaults attribute above, never this
            // global — so this fix is purely about restoring the app-facing contract.
            //
            // Fix: unconditionally re-emit the inline <script>{Id}defaultvalues=...}
            // write here too (byte-identical to pre-#632, same default — HTML-safe —
            // JsonSerializer.Serialize encoder), alongside the island. The TagHelper
            // runs server-side and cannot see the client-only #627 kill-switch flag,
            // so it cannot conditionally omit the inline script — both paths are
            // always emitted:
            //  - Default (kill-switch OFF): the inline write executes at parse time,
            //    giving synchronous back-compat; the island redundantly re-sets the
            //    same global at DOMContentLoaded (harmless).
            //  - Kill-switch ON under strict CSP (app opted in): the browser blocks
            //    this inline script, so the island becomes the sole (deferred)
            //    publisher — acceptable, since the app explicitly chose CSP over the
            //    sync guarantee. See the 'fieldDefaults' DispatchAction case
            //    (framework_layui.js) for why action.id is validated with a plain
            //    non-empty-string check rather than an identifier grammar.
            //
            // Net effect: <wt:checkbox> is NOT CSP-clean by default — it rejoins the
            // #470 "still emits inline script" hard-blocker list (see
            // docs/csp-hardening.md). A synchronous global fundamentally requires an
            // inline script; CSP-cleanliness forbids one. Don't try to have both.
            var fieldDefaultsAction = new FieldDefaultsIslandAction
            {
                Id = Id,
                Values = values
            };
            output.PostElement.AppendHtml($@"
<input type=""hidden"" name=""_DONOTUSE_{Field.Name}"" value=""1"" />
<script>
 {Id}defaultvalues = {JsonSerializer.Serialize(values)};
</script>
<script type=""application/json"" class=""wtm-dialog-init"">{JsonSerializer.Serialize(fieldDefaultsAction, _islandJsonOptions)}</script>
");
            base.Process(context, output);

        }

        private void SetSelected(List<ComboSelectListItem> source, IList data)
        {
            if (data == null)
            {
                return;
            }
            var textAndValue = false;
            if (data.GetType().GetGenericArguments()[0] == typeof(ComboSelectListItem))
            {
                textAndValue = true;
            }
            foreach (var item in source)
            {
                foreach (var item2 in data)
                {
                    if (textAndValue == true)
                    {
                        if (item.Value?.ToString().ToLower() == (item2 as ComboSelectListItem).Value?.ToString().ToLower())
                        {
                            item.Selected = true;
                            break;
                        }
                        else
                        {
                            item.Selected = false;
                        }
                    }
                    else
                    {
                        if (item.Value?.ToString().ToLower() == item2?.ToString().ToLower())
                        {
                            item.Selected = true;
                            break;
                        }
                        else
                        {
                            item.Selected = false;
                        }
                    }
                }
            }

        }
    }

    // Issue #632 (redesigned, #470 slice 1): DTO for the bare (non-wrapped)
    // fieldDefaults JSON island — {"type":"fieldDefaults","id":"...","values":[...]}.
    // Shared by CheckBoxTagHelper (this file) and RadioTagHelper. Consumed by the
    // 'fieldDefaults' DispatchAction case (framework_layui.js) to publish
    // window[id + 'defaultvalues'] for app-authored JS — BACK-COMPAT ONLY; the
    // framework's own consumer (ff.ChainChange) reads the data-wtm-defaults HTML
    // attribute emitted alongside this island on the same element, never this
    // island itself.
    //
    // Unlike LoadComboItemsIslandAction.Id (also unvalidated) this DTO's Id is
    // documented explicitly: the 'fieldDefaults' DispatchAction case validates it
    // with a plain non-empty-string check, NOT an identifier grammar (unlike
    // bindSubmit/bindValidate/bindInput's action.id/name, which resolve-and-CALL
    // an existing window[] property as a FUNCTION and therefore need the strict
    // ASCII identifier + denylist + own-property + typeof-function guard). This
    // action only ever WRITES JSON data behind a fixed, non-configurable
    // 'defaultvalues' suffix — never resolves or invokes anything — so an
    // identifier-shaped id like '__proto__' still only ever produces the harmless
    // property name "__proto__defaultvalues", and a non-ASCII id (WTM's own
    // Utils.GetIdByName only strips '.'/'['/']'/'-', so ids derived from
    // non-ASCII model/property names — e.g. the ConsoleDemo's
    // 不要用中文模型名_View_模型名 fixture — are valid, real ids in production)
    // is not silently rejected the way an ASCII-only /^[A-Za-z_$][\w$]*$/ grammar
    // would reject it.
    internal sealed class FieldDefaultsIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "fieldDefaults";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("values")]
        public List<string> Values { get; set; }
    }
}
