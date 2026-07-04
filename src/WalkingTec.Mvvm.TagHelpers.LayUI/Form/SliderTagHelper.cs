using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    /// <summary>
    /// 滑块类型
    /// </summary>
    public enum SliderTypeEnum
    {
        // 水平滑块
        Default = 0,
        // 垂直滑块
        Vertical
    }

    /// <summary>
    /// 划块
    /// </summary>
    [HtmlTargetElement("wt:slider", Attributes = REQUIRED_ATTR_NAME1, TagStructure = TagStructure.WithoutEndTag)]
    [HtmlTargetElement("wt:slider", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class SliderTagHelper : BaseFieldTag
    {
        private const string REQUIRED_ATTR_NAME1 = "field,[range=true],field1";
        private const string _idPrefix = "_slider";

        /// <summary>
        /// 绑定的字段 必填
        /// </summary>
        public ModelExpression Field1 { get; set; }

        /// <summary>
        /// 滑块类型，可选值有：default（水平滑块）、vertical（垂直滑块）
        /// 默认为 default（水平滑块）
        /// </summary>
        public SliderTypeEnum? SliderType { get; set; }

        /// <summary>
        /// 滑动条最小值
        /// 默认为 0
        /// </summary>
        public int? Min { get; set; }

        /// <summary>
        /// 滑动条最大值
        /// 默认为 100
        /// </summary>
        public int? Max { get; set; }

        /// <summary>
        /// 滑块初始值，默认为数字
        /// 若开启了滑块为范围拖拽（即 Field1 绑定到属性上了），则需赋值数组，异表示开始和结尾的区间，如：value: [30, 60]
        /// </summary>
        public new string DefaultValue { get; set; }

        /// <summary>
        /// 拖动的步长
        /// 默认为正整数 1
        /// </summary>
        public uint Step { get; set; } = 1;

        /// <summary>
        /// 是否显示间断点
        /// 默认 false
        /// </summary>
        public bool ShowStep { get; set; }

        /// <summary>
        /// 是否显示文字提示
        /// 默认 true
        /// </summary>
        public bool Tips { get; set; } = true;

        /// <summary>
        /// 是否显示输入框（注意：若 Field1 绑定到属性上了 则强制无效）
        /// 点击输入框的上下按钮，以及输入任意数字后回车或失去焦点，均可动态改变滑块
        /// 默认 false
        /// </summary>
        public bool Input { get; set; }

        /// <summary>
        /// 滑动条高度，需配合 SliderType:Vertical 参数使用
        /// 默认 200
        /// </summary>
        public int? SliderHeight { get; set; }

        /// <summary>
        /// 主题颜色，以便用在不同的主题风格下
        /// 默认 #009688
        /// </summary>
        public string Theme { get; set; }

        /// <summary>
        /// 数值改变的回调
        /// 在滑块数值被改变时触发。该回调非常重要，可动态获得滑块当前的数值。你可以将得到的数值，赋值给隐藏域，或者进行一些其它操作。
        /// param0: 当前值
        /// param1: 当前 slider 实例
        /// </summary>
        public string ChangeFunc { get; set; }

        /// <summary>
        /// 当鼠标放在圆点和滑块拖拽时均会触发提示层。
        /// 其默认显示的文本是它的对应数值，你也可以自定义提示内容
        /// param0: 当前值
        /// param1: 当前 slider 实例
        /// </summary>
        public string OnTipsFunc { get; set; }

        // Issue #552 (#470-E): System.Text.Json's default encoder escapes '<', '>',
        // and '&', making the JSON payload safe to embed inside a <script> block
        // without risk of </script> injection — same pattern as
        // DateTimeTagHelper's _laydateJsonOptions (#556).
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Issue #552 (#470-E): interprets the already-validated <c>safeDefaultValue</c>
        /// string (numeric, "[n,n]" range-pair, or the "0" fallback — see the
        /// validation block in <see cref="Process"/>) into the JSON value shape
        /// layui.slider.render's <c>value</c> option expects: a plain number for a
        /// single slider, or a two-element number array for a range slider.
        /// Returns <see langword="null"/> when there is no default value to emit
        /// (the <c>value</c> key is then omitted from the island entirely, exactly
        /// like the inline-script branch's <c>string.IsNullOrEmpty</c> guard).
        /// </summary>
        private static object ParseSliderValueForJson(string safeDefaultValue)
        {
            if (string.IsNullOrEmpty(safeDefaultValue)) { return null; }
            if (safeDefaultValue.StartsWith('[') && safeDefaultValue.EndsWith(']'))
            {
                var inner = safeDefaultValue.TrimStart('[').TrimEnd(']').Replace(" ", string.Empty);
                var parts = inner.Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
                {
                    return new object[] { a, b };
                }
                return 0d;
            }
            if (double.TryParse(safeDefaultValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
            return 0d;
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.TagMode = TagMode.StartTagAndEndTag;
            output.Attributes.Add("id", $"{_idPrefix}{Id}");

            // 是否启用范围选择
            var range = Field1 != null;
            Input = !range && Input;

            if (SliderType == SliderTypeEnum.Vertical && Input)
            {
                SliderHeight = (SliderHeight ?? 200) + 35;
                output.Attributes.Add("style", "margin-top: 35px;display: inline-block;");
            }

            string value0 = Field?.Model?.ToString();
            string value1 = Field1?.Model?.ToString();
            if (Field?.Model != null || Field1?.Model != null)
            {
                value0 = Field?.Model == null ? "0" : Field.Model.ToString();
                value1 = Field1?.Model == null ? "0" : Field1.Model.ToString();
            }
            else if(string.IsNullOrEmpty(DefaultValue) == false)
            {
                if (DefaultValue.StartsWith('[') && DefaultValue.EndsWith(']'))
                {
                    var tmp = DefaultValue.TrimStart('[').TrimEnd(']').Replace(" ", string.Empty);
                    var arr = tmp.Split(",", StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x).ToArray();
                    if (Field1 != null && arr.Length >= 2)
                    {
                        value0 = arr[0];
                        value1 = arr[1];
                    }
                    else if (arr.Length >= 1)
                    {
                        value0 = arr[0];
                    }
                }
                else
                {
                    value0 = DefaultValue;
                }
            }

            // Numeric-validate DefaultValue before JS emission to prevent JS injection.
            string safeDefaultValue = "";
            if (DefaultValue != null)
            {
                if (double.TryParse(DefaultValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    safeDefaultValue = DefaultValue;
                }
                else if (DefaultValue.StartsWith('[') && DefaultValue.EndsWith(']'))
                {
                    // Range slider: [n,n] — validate both parts
                    var inner = DefaultValue.TrimStart('[').TrimEnd(']').Replace(" ", "");
                    var parts = inner.Split(',');
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _) &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        safeDefaultValue = DefaultValue;
                    }
                    else
                    {
                        safeDefaultValue = "0";
                    }
                }
                else
                {
                    safeDefaultValue = "0";
                }
            }

            // Issue #552 (#470-E): ChangeFunc / OnTipsFunc are developer-supplied JS
            // function names invoked with live slider callback arguments (and, for
            // OnTipsFunc, a return value) — neither can be JSON-expressed, so their
            // presence keeps this field on the legacy inline <script> path below,
            // unchanged from before. A callback-free field migrates to the eval-free
            // JSON island: ff.OpenDialog / the page-ready consumer (framework_layui.js)
            // parse the island and call layui.slider.render(action.opts) directly,
            // reproducing the exact same configuration — including the mandatory
            // change-callback that writes the picked value back into the bound
            // hidden input(s), which is framework wiring rather than a developer
            // callback and is reproduced natively in the JS action handler.
            bool hasCallback = !string.IsNullOrEmpty(ChangeFunc) || !string.IsNullOrEmpty(OnTipsFunc);

            if (!hasCallback)
            {
                var fieldId0 = $"{_idPrefix}{Id}_v0";
                var fieldId1 = range ? $"{_idPrefix}{Id}_v1" : null;

                var opts = new Dictionary<string, object>
                {
                    ["elem"] = "#" + _idPrefix + Id
                };
                if (SliderType != null) { opts["type"] = SliderType.Value.ToString().ToLower(); }
                if (Min != null) { opts["min"] = Min.Value; }
                if (Max != null) { opts["max"] = Max.Value; }
                if (range) { opts["range"] = true; }
                var jsonValue = ParseSliderValueForJson(safeDefaultValue);
                if (jsonValue != null) { opts["value"] = jsonValue; }
                opts["step"] = Step;
                if (Disabled) { opts["disabled"] = true; }
                opts["showstep"] = ShowStep;
                opts["tips"] = Tips;
                opts["input"] = Input;
                if (SliderType == SliderTypeEnum.Vertical && SliderHeight != null) { opts["height"] = SliderHeight.Value; }
                if (!string.IsNullOrEmpty(Theme)) { opts["theme"] = Theme; }

                var action = new SliderIslandAction
                {
                    Opts = opts,
                    FieldId0 = fieldId0,
                    FieldId1 = fieldId1
                };
                var json = JsonSerializer.Serialize(action, _islandJsonOptions);

                var islandContent = $@"
<input type='hidden' id='{fieldId0}' name='{WebUtility.HtmlEncode(Field.Name)}' value='{WebUtility.HtmlEncode(value0 ?? "")}' class='layui-input'>
{(Field1 == null ? string.Empty : $"<input type='hidden' id='{fieldId1}' name='{WebUtility.HtmlEncode(Field1.Name)}' value='{WebUtility.HtmlEncode(value1 ?? "")}' class='layui-input'>")}
<script type=""application/json"" class=""wtm-dialog-init"">{json}</script>
";
                output.PostElement.AppendHtml(islandContent);
            }
            else
            {
                var content = $@"
<input type='hidden' name='{WebUtility.HtmlEncode(Field.Name)}' value='{WebUtility.HtmlEncode(value0 ?? "")}' class='layui-input'>
{(Field1 == null ? string.Empty : $"<input type='hidden' name='{WebUtility.HtmlEncode(Field1.Name)}' value='{WebUtility.HtmlEncode(value1 ?? "")}' class='layui-input'>")}
<script>
layui.use(['slider'],function(){{
  var $ = layui.$;
  var _id = '{_idPrefix}{Id}';
  var slider = layui.slider;
  function defaultFunc(value,sliderIns) {{
    {(range ? $"$('input[name=\"{JavaScriptEncoder.Default.Encode(Field.Name)}\"]').val(value[0]);$('input[name=\"{JavaScriptEncoder.Default.Encode(Field1!.Name)}\"]').val(value[1]);" : $"$('input[name=\"{JavaScriptEncoder.Default.Encode(Field.Name)}\"]').val(value);")}
  }}
  var sliderIns = slider.render({{
    elem: '#'+_id
    {(SliderType == null ? string.Empty : $",type:'{SliderType.Value.ToString().ToLower()}'")}
    {(Min == null ? string.Empty : $",min:{Min.Value}")}
    {(Max == null ? string.Empty : $",max:{Max.Value}")}
    {(!range ? string.Empty : $",range:true")}
    {(string.IsNullOrEmpty(safeDefaultValue) ? string.Empty : $",value:{safeDefaultValue}")}
    ,step:{Step}
    {(!Disabled ? string.Empty : ",disabled:true")}
    ,showstep:{ShowStep.ToString().ToLower()}
    ,tips:{Tips.ToString().ToLower()}
    ,input:{Input.ToString().ToLower()}
    {(SliderType == null || SliderType.Value == SliderTypeEnum.Default ? string.Empty : (SliderHeight == null ? string.Empty : $",height:{SliderHeight.Value}"))}
    {(string.IsNullOrEmpty(Theme) ? string.Empty : $",theme: '{Theme}'")}
    ,change: function(value){{defaultFunc(value,sliderIns);
    {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : $"{ChangeFunc}(value,sliderIns)")}
    }}
    {(string.IsNullOrEmpty(OnTipsFunc) ? string.Empty : $",setTips: function(value){{return {OnTipsFunc}(value,sliderIns);}}")}
  }});
  {
    (SliderType == SliderTypeEnum.Vertical ?
      (Input ? "$('#'+_id+' .layui-slider-input').attr('style','right:unset;top:-40px');" : string.Empty) :
      $"$('#'+_id).attr('style','min-height: 18px;padding-top: 18px;');{(Input ? "$('#'+_id+' .layui-slider-input').attr('style','top:3px;');" : string.Empty)}")
  }
}})
</script>
";

                output.PostElement.AppendHtml(content);
            }
            base.Process(context, output);
        }
    }

    // Issue #552 (#470-E): DTO for the bare (non-wrapped) slider JSON island —
    // {"type":"slider","opts":{...},"fieldId0":"...","fieldId1":"..."}.
    // ff._normalizeIslandPayload (framework_layui.js) wraps this into the
    // {actions:[...]} shape ff.DispatchAction expects; not part of the public
    // API surface.
    internal class SliderIslandAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "slider";

        [System.Text.Json.Serialization.JsonPropertyName("opts")]
        public Dictionary<string, object> Opts { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("fieldId0")]
        public string FieldId0 { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("fieldId1")]
        public string FieldId1 { get; set; }
    }
}
