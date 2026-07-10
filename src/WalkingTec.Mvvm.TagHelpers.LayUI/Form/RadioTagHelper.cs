using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    [HtmlTargetElement("wt:radio", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class RadioTagHelper : BaseFieldTag
    {
        // Issue #633 (#470-F): see ComboBoxTagHelper's _islandJsonOptions for the
        // full rationale (same shared LoadComboItemsIslandAction DTO, defined in
        // ComboBoxTagHelper.cs).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string YesText { get; set; }

        public string NoText { get; set; }

        public ModelExpression Items { get; set; }

        /// <summary>
        /// 改变选择时触发的js函数，func(data)格式;
        /// <para>
        /// data.elem得到radio原始DOM对象
        /// </para>
        /// <para>
        /// data.value被点击的radio的value值
        /// </para>
        /// </summary>
        public string ChangeFunc { get; set; }


        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Clear();
            output.Attributes.Add("div-for", "radio");
            output.Attributes.Add("wtm-ctype", "radio");
            output.Attributes.Add("wtm-name", Field.Name);

            var modeltype = Field.Metadata.ModelType;
            List<ComboSelectListItem> listItems = [];
            List<string> values = [];
            if (modeltype.IsBoolOrNullableBool())
            {
                if (Field.Model == null)
                {
                    values.Add("False");
                }
                else
                {
                    values.Add(Field.Model.ToString());
                }
            }
            else
            {
                if (Field.Model != null)
                {
                    values.Add(Field.Model.ToString());
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
                // Issue #633 (#470-F): eval-free JSON island — see ComboBoxTagHelper's
                // matching comment for the full rationale (shared action shape, JSON
                // escaping, and the async-ajax timing argument, which applies
                // identically here).
                var loadComboItemsAction = new LoadComboItemsIslandAction
                {
                    ControlType = "radio",
                    Url = ItemUrl,
                    Id = Id,
                    Field = Field.Name,
                    SelectVal = values
                };
                output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{JsonSerializer.Serialize(loadComboItemsAction, _islandJsonOptions)}</script>");
            }
            else
            {
                if (Items?.Model == null)
                {
                    var checktype = modeltype;
                    if ((modeltype.IsGenericType && typeof(List<>).IsAssignableFrom(modeltype.GetGenericTypeDefinition())))
                    {
                        checktype = modeltype.GetGenericArguments()[0];
                    }

                    if (checktype.IsEnumOrNullableEnum())
                    {
                        listItems = checktype.ToListItems(DefaultValue ?? Field.Model);
                    }
                    else if (checktype == typeof(bool) || checktype == typeof(bool?))
                    {
                        bool? df = null;
                        if (bool.TryParse(DefaultValue ?? "", out bool test) == true)
                        {
                            df = test;
                        }
                        listItems = Utils.GetBoolCombo(BoolComboTypes.Custom, df ?? (bool?)Field.Model, YesText, NoText);
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
                            ComboSelectListItem newitem = new ComboSelectListItem();
                            newitem.Text = item?.ToString();
                            newitem.Value = item?.ToString();
                            listItems.Add(newitem);
                        }
                    }
                }
                SetSelected(listItems, values);
            }
            for (int i = 0; i < listItems.Count; i++)
            {
                var item = listItems[i];
                var selected = item.Selected ? " checked" : " ";
                // TLU-SEC-002: HtmlEncode item.Value and item.Text before interpolating into
                // HTML attribute values. Without encoding, a list item whose Value or Text
                // contains quotes or angle brackets can break attribute boundaries (XSS).
                output.PostContent.AppendHtml($@"
        <input type=""radio"" name=""{Field.Name}"" value=""{WebUtility.HtmlEncode(item.Value)}"" title=""{WebUtility.HtmlEncode(item.Text)}"" {selected} />
        <script>
         {Id}defaultvalues = {JsonSerializer.Serialize(values)};
        </script>
");
            }

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
}
