using System;
using System.Globalization;
using System.Linq;

using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using WalkingTec.Mvvm.Core;

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

        /// <summary>
        /// True when the text only contains the characters of a JavaScript number or number array
        /// (digits, '.', '-', ',', '[', ']' and spaces).
        /// </summary>
        private static bool IsSafeDefaultValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            foreach (var c in value)
            {
                if (!(c >= '0' && c <= '9') && c != '.' && c != '-' && c != ',' && c != '[' && c != ']' && c != ' ')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The bound value as a JavaScript number literal, or null when it is not a finite number.
        /// The literal is built from the parsed number with the invariant culture, so it only contains
        /// digits, '.', '-', '+' and 'E' whatever the current culture or the original text is.
        /// </summary>
        private static string ToScriptNumber(object model)
        {
            double d;
            switch (model)
            {
                case string s:
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    {
                        return null;
                    }
                    break;
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                case float _:
                case double _:
                case decimal _:
                    d = Convert.ToDouble(model, CultureInfo.InvariantCulture);
                    break;
                default:
                    return null;
            }
            return double.IsFinite(d) ? d.ToString(CultureInfo.InvariantCulture) : null;
        }

        /// <summary>
        /// The text of a number or of any other bound value for the hidden input, formatted with the invariant culture:
        /// a decimal comma, a culture specific minus sign or digit grouping of the server must not reach the posted value
        /// (layui writes the same invariant text when the handle is dragged). Non-numeric values keep their own text.
        /// </summary>
        private static string ToInputValue(object model)
        {
            return model == null ? null : Convert.ToString(model, CultureInfo.InvariantCulture);
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

            // DefaultValue is emitted as a JavaScript expression (a number or an array such as [10,20]),
            // so only that shape is accepted; anything else is dropped instead of reaching the script.
            string safeDefault = IsSafeDefaultValue(DefaultValue) ? DefaultValue : null;

            // An array default is split into its elements. A single element ([5]) is treated as one value:
            // non-range sliders get the plain number, range sliders use it for both ends ([5,5]) because
            // layui expects two values there. An array without any element is dropped like any invalid value.
            string scriptDefault = safeDefault;
            string[] defaultItems = null;
            if (safeDefault != null && safeDefault.StartsWith('[') && safeDefault.EndsWith(']'))
            {
                var tmp = safeDefault.TrimStart('[').TrimEnd(']').Replace(" ", string.Empty);
                defaultItems = tmp.Split(",", StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x).ToArray();
                if (defaultItems.Length == 1)
                {
                    scriptDefault = range ? $"[{defaultItems[0]},{defaultItems[0]}]" : defaultItems[0];
                }
                else if (defaultItems.Length == 0)
                {
                    scriptDefault = null;
                }
            }

            // The handle is drawn from the bound value (the one written to the hidden inputs) when there is one.
            // A bound 0 does not override an explicit default-value: a value type cannot tell "not set" from 0,
            // and such a slider has always started at its default-value. Without a default-value a bound 0 is only
            // written when the scale starts below 0 (otherwise layui puts the handle at 0 anyway).
            if (Field?.Model != null || Field1?.Model != null)
            {
                var bound0 = Field?.Model == null ? "0" : ToScriptNumber(Field.Model);
                var bound1 = !range ? null : (Field1?.Model == null ? "0" : ToScriptNumber(Field1.Model));
                if (bound0 != null && (!range || bound1 != null))
                {
                    var isZero = double.Parse(bound0, CultureInfo.InvariantCulture) == 0
                        && (bound1 == null || double.Parse(bound1, CultureInfo.InvariantCulture) == 0);
                    if (!isZero || (scriptDefault == null && (Min ?? 0) < 0))
                    {
                        scriptDefault = range ? $"[{bound0},{bound1}]" : bound0;
                    }
                }
            }

            string value0 = ToInputValue(Field?.Model);
            string value1 = ToInputValue(Field1?.Model);
            if (Field?.Model != null || Field1?.Model != null)
            {
                value0 = Field?.Model == null ? "0" : ToInputValue(Field.Model);
                value1 = Field1?.Model == null ? "0" : ToInputValue(Field1.Model);
            }
            else if(string.IsNullOrEmpty(safeDefault) == false)
            {
                if (defaultItems != null)
                {
                    if (defaultItems.Length >= 2)
                    {
                        value0 = defaultItems[0];
                        value1 = defaultItems[1];
                    }
                    else if (defaultItems.Length == 1)
                    {
                        value0 = defaultItems[0];
                        value1 = defaultItems[0];
                    }
                }
                else
                {
                    value0 = safeDefault;
                }
            }

            var content = $@"
<input type='hidden' name='{Field.Name}' value='{HtmlAttributeEncoder.Encode(value0)}' class='layui-input'>
{(Field1 == null ? string.Empty : $"<input type='hidden' name='{Field1.Name}' value='{HtmlAttributeEncoder.Encode(value1)}' class='layui-input'>")}
<script>
layui.use(['slider'],function(){{
  var $ = layui.$;
  var _id = '{_idPrefix}{Id}';
  var slider = layui.slider;
  function defaultFunc(value,sliderIns) {{
    {(range ? $"$('input[name=\"{Field.Name}\"]').val(value[0]);$('input[name=\"{Field1.Name}\"]').val(value[1]);" : $"$('input[name=\"{Field.Name}\"]').val(value);")}
  }}
  var sliderIns = slider.render({{
    elem: '#'+_id
    {(SliderType == null ? string.Empty : $",type:'{SliderType.Value.ToString().ToLower()}'")}
    {(Min == null ? string.Empty : $",min:{Min.Value.ToString(CultureInfo.InvariantCulture)}")}
    {(Max == null ? string.Empty : $",max:{Max.Value.ToString(CultureInfo.InvariantCulture)}")}
    {(!range ? string.Empty : $",range:true")}
    {(scriptDefault == null ? string.Empty : $",value:{scriptDefault}")}
    ,step:{Step.ToString(CultureInfo.InvariantCulture)}
    {(!Disabled ? string.Empty : ",disabled:true")}
    ,showstep:{ShowStep.ToString().ToLower()}
    ,tips:{Tips.ToString().ToLower()}
    ,input:{Input.ToString().ToLower()}
    {(SliderType == null || SliderType.Value == SliderTypeEnum.Default ? string.Empty : (SliderHeight == null ? string.Empty : $",height:{SliderHeight.Value.ToString(CultureInfo.InvariantCulture)}"))}
    {(string.IsNullOrEmpty(Theme) ? string.Empty : $",theme: '{JsStringEncoder.Escape(Theme)}'")}
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
            base.Process(context, output);
        }
    }
}
