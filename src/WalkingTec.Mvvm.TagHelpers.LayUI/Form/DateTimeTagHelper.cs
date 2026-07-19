using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI
{
    public enum DateTimeLangEnum
    {
        /// <summary>
        /// 中文版，默认
        /// </summary>
        CN = 0,
        /// <summary>
        /// 即英文版
        /// </summary>
        EN
    }

    [HtmlTargetElement("wt:datetime", Attributes = REQUIRED_ATTR_NAME, TagStructure = TagStructure.WithoutEndTag)]
    public class DateTimeTagHelper : BaseFieldTag
    {
        /// <summary>
        /// 控件选择类型
        /// 默认值：date
        /// </summary>
        public DateTimeTypeEnum Type { get; set; }

        /// <summary>
        /// 开启时间范围选择，默认 分隔符为 ~
        /// 默认值：false
        /// </summary>
        public bool? Range { get; set; }

        /// <summary>
        /// 时间范围分隔符 默认 ~
        /// </summary>
        public string RangeSplit { get; set; }

        /// <summary>
        /// Max
        /// 1.  如果值为字符类型，则：年月日必须用 -（中划线）分割、时分秒必须用 :（半角冒号）号分割。这里并非遵循 format 设定的格式
        /// 2.  如果值为整数类型，且数字＜86400000，则数字代表天数，如：min: -7，即代表最小日期在7天前，正数代表若干天后
        /// 3.  如果值为整数类型，且数字 ≥ 86400000，则数字代表时间戳，如：max: 4073558400000，即代表最大日期在：公元3000年1月1日
        /// </summary>
        public string Max { get; set; }

        /// <summary>
        /// Min
        /// 1.  如果值为字符类型，则：年月日必须用 -（中划线）分割、时分秒必须用 :（半角冒号）号分割。这里并非遵循 format 设定的格式
        /// 2.  如果值为整数类型，且数字 ＜ 86400000，则数字代表天数，如：min: -7，即代表最小日期在7天前，正数代表若干天后
        /// 3.  如果值为整数类型，且数字 ≥ 86400000，则数字代表时间戳，如：max: 4073558400000，即代表最大日期在：公元3000年1月1日
        /// </summary>
        public string Min { get; set; }

        /// <summary>
        /// 自定义格式 默认值：yyyy-MM-dd
        /// yyyy    年份，至少四位数。如果不足四位，则前面补零
        /// y       年份，不限制位数，即不管年份多少位，前面均不补零
        /// MM      月份，至少两位数。如果不足两位，则前面补零。
        /// M       月份，允许一位数。
        /// dd      日期，至少两位数。如果不足两位，则前面补零。
        /// d       日期，允许一位数。
        /// HH      小时，至少两位数。如果不足两位，则前面补零。
        /// H       小时，允许一位数。
        /// mm      分钟，至少两位数。如果不足两位，则前面补零。
        /// m       分钟，允许一位数。
        /// ss      秒数，至少两位数。如果不足两位，则前面补零。
        /// s       秒数，允许一位数。
        /// </summary>
        public string Format { get; set; }

        /// <summary>
        /// 层叠顺序
        /// 类型：Number，默认值：66666666
        /// 一般用于解决与其它元素的互相被遮掩的问题。如果 position 参数设为 static 时，该参数无效。
        /// </summary>
        public int? ZIndex { get; set; }

        /// <summary>
        /// 是否显示底部栏
        /// </summary>
        public bool? ShowBottom { get; set; }


        /// <summary>
        /// 只出现确定按钮
        /// </summary>
        public bool? ConfirmOnly { get; set; }

        /// <summary>
        /// 语言 默认CN
        /// </summary>
        public DateTimeLangEnum? Lang { get; set; }

        /// <summary>
        /// 是否显示公历节日
        /// 内置了一些我国通用的公历重要节日，通过设置 true 来开启。国际版不会显示。
        /// </summary>
        public bool? Calendar { get; set; }

        /// <summary>
        /// 标注重要日子
        /// 标注          格式                    说明
        /// 每年的日期   {'0-9-18': '国耻'}        0 即代表每一年
        /// 每月的日期   {'0-0-15': '中旬'}        0-0 即代表每年每月（layui 2.1.1/layDate 5.0.4 新增）
        /// 特定的日期   {'2017-8-21': '发布'}     -
        /// </summary>
        public Dictionary<string, string> Mark { get; set; }

        /// <summary>
        /// 控件初始打开的回调，控件在打开时触发。
        /// 回调返回2个参数：初始的日期时间对象、当前实例对象
        /// </summary>
        public string ReadyFunc { get; set; }

        /// <summary>
        /// 日期时间被切换后的回调
        /// 年月日时间被切换时都会触发。
        /// 回调返回4个参数，分别代表：生成的值、日期时间对象、结束的日期时间对象、当前实例对象
        /// </summary>
        public string ChangeFunc { get; set; }

        /// <summary>
        /// 控件选择完毕后的回调
        /// 点击日期、清空、现在、确定均会触发。
        /// 回调返回4个参数，分别代表：生成的值、日期时间对象、结束的日期时间对象、当前实例对象
        /// </summary>
        public string DoneFunc { get; set; }

        /// <summary>启用日期範圍選擇（輸出兩個隱藏 input + LayUI laydate range 模式）</summary>
        public bool IsRange { get; set; }

        /// <summary>範圍開始日期的 hidden input name（IsRange=true 時有效）</summary>
        public string RangeStartName { get; set; }

        /// <summary>範圍結束日期的 hidden input name（IsRange=true 時有效）</summary>
        public string RangeEndName { get; set; }

        /// <summary>範圍選擇器的 placeholder 文字（IsRange=true 時有效）。留空則不顯示 placeholder。</summary>
        public string RangePlaceholder { get; set; } = "";

        public static Dictionary<DateTimeTypeEnum, string> DateTimeFormatDic = new Dictionary<DateTimeTypeEnum, string>()
        {
            { DateTimeTypeEnum.Date,"yyyy-MM-dd"},
            { DateTimeTypeEnum.DateTime,"yyyy-MM-dd HH:mm:ss"},
            { DateTimeTypeEnum.Year,"yyyy"},
            { DateTimeTypeEnum.Month,"yyyy-MM"},
            { DateTimeTypeEnum.Time,"HH:mm:ss"},
        };

        private Configs _configInfo;

        public DateTimeTagHelper(IOptionsMonitor<Configs> configs)
        {
            _configInfo = configs.CurrentValue;
        }

        // Issue #556 (#470-B slice 1): System.Text.Json's default encoder
        // (JavaScriptEncoder.Default) escapes '<', '>', and '&', making the
        // JSON payload safe to embed inside a <script> block without risk of
        // </script> injection — same pattern as DialogInitTagHelper's
        // _jsonOptions.
        private static readonly JsonSerializerOptions _laydateJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Issue #470 Slice H: identifier check for the ReadyFunc/ChangeFunc/
        // DoneFunc island migration below — the SAME identifier class
        // framework_layui.js's #558/#601 guarded window[name] resolver
        // (ff._resolveGuardedWindowFn) enforces: a bare JS identifier, nothing
        // else. Uses `\z` (not `$`) as the end anchor for the same reason as
        // TextBoxTagHelper's #601 _identifierRegex: in .NET, `$` matches at
        // end-of-string OR immediately before a single trailing '\n', but the
        // client-side JS resolver's `/.../.test()` with `$` matches ONLY the
        // absolute end. `\z` keeps both engines in agreement so a value like
        // "myFunc\n" is classified as a non-identifier by BOTH — never
        // silently dropped end-to-end.
        private static readonly Regex _identifierRegex = new(@"^[A-Za-z_$][\w$]*\z", RegexOptions.Compiled);

        private static bool IsPlainIdentifier(string name) =>
            !string.IsNullOrEmpty(name) && _identifierRegex.IsMatch(name);

        // Issue #470 Slice H: the two-hidden-input range path's built-in
        // `done` split has ALWAYS used a hardcoded " - " delimiter (layui's
        // own two-date display separator for its boolean `range: true`
        // mode), independent of the RangeSplit attribute (which only applies
        // to the separate Range+DateRange model-binding mode). Carried as
        // island data (rangeSplitStr) rather than re-hardcoded a second time
        // in framework_layui.js, so this constant stays the single source of
        // truth.
        private const string RangeDoneSplit = " - ";

        /// <summary>
        /// Issue #556 (#470-B slice 1): builds the laydate.render() options object
        /// for the eval-free JSON island, reproducing the exact same configuration
        /// the inline &lt;script&gt; fallback below would have produced. <paramref name="rawMin"/>
        /// and <paramref name="rawMax"/> must be the ORIGINAL (pre-mutation) Min/Max values —
        /// Process() rewrites Min/Max in place into JS-literal text (quoted for date
        /// strings, bare for day-offset integers) for the inline-script code path,
        /// which is not a valid JSON value shape. <paramref name="isRangeMode"/> (Issue #470
        /// Slice H) forces the boolean laydate `range: true` mode used by the
        /// two-hidden-input IsRange path, overriding the RangeSplit-string mode below.
        /// </summary>
        private Dictionary<string, object> BuildLaydateOpts(string rawMin, string rawMax, bool isRangeMode = false)
        {
            var opts = new Dictionary<string, object>
            {
                ["elem"] = "#" + Id,
                ["type"] = Type.ToString().ToLower()
            };
            if (isRangeMode) { opts["range"] = true; }
            else if (!string.IsNullOrEmpty(RangeSplit)) { opts["range"] = RangeSplit; }
            if (!string.IsNullOrEmpty(Format)) { opts["format"] = Format; }
            if (!string.IsNullOrEmpty(rawMin))
            {
                opts["min"] = int.TryParse(rawMin, out int minN) ? (object)minN : rawMin;
            }
            if (!string.IsNullOrEmpty(rawMax))
            {
                opts["max"] = int.TryParse(rawMax, out int maxN) ? (object)maxN : rawMax;
            }
            if (ZIndex.HasValue) { opts["zIndex"] = ZIndex.Value; }
            if (ShowBottom.HasValue) { opts["showBottom"] = ShowBottom.Value; }
            // Mirrors the inline-script ConfirmOnly ternary exactly: btns:['confirm']
            // only when ConfirmOnly is true AND (ShowBottom is unset — defaults to
            // shown — or explicitly true). ShowBottom===false suppresses it even
            // when ConfirmOnly is true, matching the original conditional.
            bool confirmOnlyEffective = ConfirmOnly.HasValue &&
                ((ShowBottom.HasValue && ShowBottom.Value && ConfirmOnly.Value) ||
                 (!ShowBottom.HasValue && ConfirmOnly.Value));
            if (confirmOnlyEffective) { opts["btns"] = new[] { "confirm" }; }
            if (Calendar.HasValue) { opts["calendar"] = Calendar.Value; }
            if (Lang.HasValue) { opts["lang"] = Lang.Value.ToString().ToLower(); }
            if (Mark != null && Mark.Count > 0) { opts["mark"] = Mark; }
            return opts;
        }

        /// <summary>
        /// Issue #470 Slice H: builds the <c>console.warn(...)</c> statement (with a
        /// trailing newline so it can be inlined right before <c>layui.use(...)</c>)
        /// that the legacy inline-&lt;script&gt; fallback emits for a non-identifier
        /// ReadyFunc/ChangeFunc/DoneFunc — mirroring the FormTagHelper #558/#561
        /// non-identifier BeforeSubmit 3-way decision: never silently drop the
        /// developer's handler, but make the deprecated eval-equivalent code path
        /// loud so they migrate to a plain named function instead.
        /// </summary>
        private string BuildDeprecationWarnScript(List<string> nonIdentifierAttrs)
        {
            var attrList = string.Join("/", nonIdentifierAttrs);
            var message = $"[WTM] <wt:datetime id=\"{Id}\"> {attrList} is not a plain function name; " +
                "falling back to the legacy inline <script> laydate init. Use a plain named function " +
                "instead to get the eval-free JSON-island path. See #470.";
            // Issue #651 guard: every JsonSerializer.Serialize(..., _laydateJsonOptions)
            // call in an island-emitting file must route through LayuiIslandJson
            // (the '$'-escape wrapper) — never call JsonSerializer.Serialize directly,
            // even for non-island inline-script text like this warning string.
            var encodedMessage = LayuiIslandJson.Serialize(message, _laydateJsonOptions);
            return $"console.warn({encodedMessage});\n  ";
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            string Value = null;
            output.TagName = "input";
            output.TagMode = TagMode.StartTagOnly;
            output.Attributes.Add("type", "text");
            output.Attributes.Add("name", Field.Name);

            if (Range.HasValue && Range.Value && string.IsNullOrEmpty(RangeSplit))
            {
                RangeSplit = "~";
            }

            if (Field.ModelExplorer.ModelType == typeof(string) || Field.ModelExplorer.ModelType.IsNumber())
            {
                Value = Field.Model?.ToString() ?? Value;
            }
            else if (Range.HasValue && Range.Value && Field.ModelExplorer.ModelType == typeof(DateRange))
            {
                var dateRange = Field.Model as DateRange;
                if (string.IsNullOrEmpty(Format))
                    Value = dateRange?.ToString(DateTimeFormatDic[Type], RangeSplit) ?? Value;
                else
                    Value = dateRange?.ToString(Format, RangeSplit) ?? Value;
            }
            else if (Field.ModelExplorer.ModelType == typeof(TimeSpan) || Field.ModelExplorer.ModelType == typeof(TimeSpan?))
            {
                TimeSpan? df = Field.Model as TimeSpan?;
                if (df == TimeSpan.MinValue)
                {
                    df = null;
                }
                Value = df?.ToString();
            }
            else
            {
                DateTime? df = Field.Model as DateTime?;
                if (df == DateTime.MinValue)
                {
                    df = null;
                }
                if (string.IsNullOrEmpty(Format))
                    Value = df?.ToString(DateTimeFormatDic[Type]) ?? Value;
                else
                    Value = df?.ToString(Format) ?? Value;
            }


            if (string.IsNullOrEmpty(Value))
            {
                if (DefaultValue != null)
                {
                    Value = DefaultValue;
                }
            }


            output.Attributes.Add("value", Value);
            output.Attributes.Add("class", "layui-input");
            if (_configInfo.UIOptions.DateTime.DefaultReadonly)
                output.Attributes.Add("readonly", "readonly");

            // Issue #556 (#470-B slice 1): capture the ORIGINAL Min/Max before the
            // mutation below rewrites them into JS-literal text for the inline
            // <script> fallback (quoted date strings / bare day-offset integers).
            // BuildLaydateOpts needs the pre-mutation values to produce correctly
            // typed JSON (a JSON number for day-offsets, a JSON string for dates).
            var rawMin = Min;
            var rawMax = Max;

            if (!string.IsNullOrEmpty(Min))
            {
                if (int.TryParse(Min, out int minRes))
                {
                    Min = minRes.ToString();
                }
                else
                {
                    Min = $"'{Min}'";
                }
            }
            if (!string.IsNullOrEmpty(Max))
            {
                if (int.TryParse(Max, out int maxRes))
                {
                    Max = maxRes.ToString();
                }
                else
                {
                    Max = $"'{Max}'";
                }
            }

            if (Lang == null)
            {
                if (Enum.TryParse<DateTimeLangEnum>(THProgram._localizer["Sys.LayuiDateLan"], true, out var testlang))
                {
                    Lang = testlang;
                }
            }

            if (!IsRange)
            {
                bool readyPresent = !string.IsNullOrEmpty(ReadyFunc);
                bool changePresent = !string.IsNullOrEmpty(ChangeFunc);
                bool donePresent = !string.IsNullOrEmpty(DoneFunc);
                bool hasCallback = readyPresent || changePresent || donePresent;

                if (!hasCallback)
                {
                    // Issue #556 (#470-B slice 1): eval-free JSON island — no
                    // ready/change/done callback to express, so this field
                    // migrates off the inline <script>. ff.OpenDialog / the
                    // page-ready consumer (framework_layui.js) parse this
                    // island and call layui.laydate.render(action.opts)
                    // directly, reproducing the exact same config as the
                    // inline-script branch below.
                    var opts = BuildLaydateOpts(rawMin, rawMax);
                    var action = new LaydateIslandAction { Opts = opts };
                    var json = LayuiIslandJson.Serialize(action, _laydateJsonOptions);
                    output.PostElement.AppendHtml(
                        $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");
                }
                else
                {
                    // Issue #470 Slice H: 3-way decision mirroring FormTagHelper's
                    // #558/#561 BeforeSubmit migration — a plain-identifier
                    // callback name (the ONLY shape ff._resolveGuardedWindowFn can
                    // safely resolve by name) migrates to the eval-free JSON
                    // island; a non-identifier expression (dotted/call-syntax)
                    // can never be resolved that way and keeps the exact legacy
                    // inline <script> below, loudly deprecated via console.warn.
                    bool readyIsIdentifier = readyPresent && IsPlainIdentifier(ReadyFunc);
                    bool changeIsIdentifier = changePresent && IsPlainIdentifier(ChangeFunc);
                    bool doneIsIdentifier = donePresent && IsPlainIdentifier(DoneFunc);
                    bool allCallbacksMigratable =
                        (!readyPresent || readyIsIdentifier) &&
                        (!changePresent || changeIsIdentifier) &&
                        (!donePresent || doneIsIdentifier);
                    // Issue #753: Slice H shipped BEFORE WtmUIOptions.UseSelectIslandRender
                    // existed and migrated whenever callbacks were plain identifiers,
                    // regardless of the flag — breaking the flag-OFF byte-identical
                    // guarantee #470 Slice J/K/L/M established for ComboBox/Tree/
                    // Transfer/Upload. Gate on the SAME flag those siblings use.
                    bool useDateIsland = UIConfig.UseSelectIslandRender && allCallbacksMigratable;

                    if (useDateIsland)
                    {
                        // Issue #470 Slice H: every supplied callback is a plain
                        // identifier — migrate to the eval-free JSON island.
                        // framework_layui.js resolves each name through the SAME
                        // #558/#601 guarded window[name] lookup bindSubmit/
                        // bindInput use (identifier regex + denylist +
                        // own-property + typeof function; a failed lookup
                        // silently skips just that one callback and never
                        // throws) and wires it to the live laydate instance with
                        // the exact same argument shape the legacy inline
                        // <script> below passes.
                        var opts = BuildLaydateOpts(rawMin, rawMax);
                        var action = new LaydateIslandAction
                        {
                            Opts = opts,
                            ReadyFn = readyIsIdentifier ? ReadyFunc : null,
                            ChangeFn = changeIsIdentifier ? ChangeFunc : null,
                            DoneFn = doneIsIdentifier ? DoneFunc : null
                        };
                        var json = LayuiIslandJson.Serialize(action, _laydateJsonOptions);
                        output.PostElement.AppendHtml(
                            $"<script type=\"application/json\" class=\"wtm-dialog-init\">{json}</script>");
                    }
                    else
                    {
                        // Issue #470 Slice H: at least one of
                        // ReadyFunc/ChangeFunc/DoneFunc is a non-identifier
                        // expression that can't be safely resolved by name —
                        // never silently drop the developer's handler, keep the
                        // EXACT legacy inline <script>, but surface a loud
                        // deprecation warning so they can migrate to a plain
                        // named function and get the eval-free island path.
                        var nonIdentifierAttrs = new List<string>();
                        if (readyPresent && !readyIsIdentifier) { nonIdentifierAttrs.Add(nameof(ReadyFunc)); }
                        if (changePresent && !changeIsIdentifier) { nonIdentifierAttrs.Add(nameof(ChangeFunc)); }
                        if (donePresent && !doneIsIdentifier) { nonIdentifierAttrs.Add(nameof(DoneFunc)); }
                        // Issue #753: only warn when the flag is actually ON and island
                        // render was skipped for a genuine non-identifier callback — when
                        // the flag is OFF this branch is reached unconditionally (even for
                        // fully-migratable callbacks), and must contribute ZERO characters
                        // to stay byte-identical to the pre-#470 base emission.
                        var warnJs = (UIConfig.UseSelectIslandRender && nonIdentifierAttrs.Count > 0)
                            ? BuildDeprecationWarnScript(nonIdentifierAttrs)
                            : string.Empty;

                        var content = $@"
<script>
{warnJs}layui.use(['laydate'],function(){{
  var laydate = layui.laydate;
  var dateIns = laydate.render({{
    elem: '#{Id}',
    type: '{Type.ToString().ToLower()}'
    {(string.IsNullOrEmpty(RangeSplit) ? string.Empty : $",range:'{JavaScriptEncoder.Default.Encode(RangeSplit)}'")}
    {(string.IsNullOrEmpty(Format) ? string.Empty : $",format: '{JavaScriptEncoder.Default.Encode(Format)}'")}
    {(string.IsNullOrEmpty(Min) ? string.Empty : $",min: {Min}")}
    {(string.IsNullOrEmpty(Max) ? string.Empty : $",max: {Max}")}
    {(!ZIndex.HasValue ? string.Empty : $",zIndex: {ZIndex.Value}")}
    {(!ShowBottom.HasValue ? string.Empty : $",showBottom: {ShowBottom.Value.ToString().ToLower()}")}
    {(!ConfirmOnly.HasValue ? string.Empty : ShowBottom.HasValue && ShowBottom.Value && ConfirmOnly.Value || !ShowBottom.HasValue && ConfirmOnly.Value ? $",btns: ['confirm']" : string.Empty)}
    {(!Calendar.HasValue ? string.Empty : $",calendar: {Calendar.Value.ToString().ToLower()}")}
    {(!Lang.HasValue ? string.Empty : $",lang: '{Lang.Value.ToString().ToLower()}'")}
    {(Mark == null || Mark.Count == 0 ? string.Empty : $",mark: {LayuiIslandJson.Serialize(Mark)}")}
    {(string.IsNullOrEmpty(ReadyFunc) ? string.Empty : $",ready: function(value){{{ReadyFunc}(value,dateIns)}}")}
    {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : $",change: function(value,date,endDate){{{ChangeFunc}(value,date,endDate,dateIns)}}")}
    {(string.IsNullOrEmpty(DoneFunc) ? string.Empty : $",done: function(value,date,endDate){{{DoneFunc}(value,date,endDate,dateIns)}}")}
  }});
}})
</script>
";
                        output.PostElement.AppendHtml(content);
                    }
                }
            }

            // Issue #470 Slice H: the two-hidden-input range path used to ALWAYS
            // emit the inline <script> (it needs its own built-in `done` split
            // callback regardless of caller callbacks, which used to be
            // impossible to JSON-express). It now migrates to the island too
            // whenever every supplied callback is a plain identifier — the split
            // itself is carried as island data (RangeStartId/RangeEndId/
            // RangeSplitStr) and framework_layui.js installs the built-in
            // `done` client-side. A non-identifier callback still falls back to
            // the exact legacy inline <script>, loudly deprecated.
            if (IsRange && !string.IsNullOrEmpty(RangeStartName) && !string.IsNullOrEmpty(RangeEndName))
            {
                if (!string.IsNullOrEmpty(RangePlaceholder))
                {
                    output.Attributes.SetAttribute("placeholder", RangePlaceholder);
                }

                var hiddenInputsHtml = $@"
<input type=""hidden"" id=""{RangeStartName}"" name=""{RangeStartName}"" />
<input type=""hidden"" id=""{RangeEndName}"" name=""{RangeEndName}"" />";
                output.PostElement.AppendHtml(hiddenInputsHtml);

                bool rangeReadyPresent = !string.IsNullOrEmpty(ReadyFunc);
                bool rangeChangePresent = !string.IsNullOrEmpty(ChangeFunc);
                bool rangeDonePresent = !string.IsNullOrEmpty(DoneFunc);
                bool rangeReadyIsIdentifier = rangeReadyPresent && IsPlainIdentifier(ReadyFunc);
                bool rangeChangeIsIdentifier = rangeChangePresent && IsPlainIdentifier(ChangeFunc);
                bool rangeDoneIsIdentifier = rangeDonePresent && IsPlainIdentifier(DoneFunc);
                bool rangeAllCallbacksMigratable =
                    (!rangeReadyPresent || rangeReadyIsIdentifier) &&
                    (!rangeChangePresent || rangeChangeIsIdentifier) &&
                    (!rangeDonePresent || rangeDoneIsIdentifier);
                // Issue #753: same gating as the single-field path above — Slice H's
                // range island migrated unconditionally; require the flag.
                bool useRangeIsland = UIConfig.UseSelectIslandRender && rangeAllCallbacksMigratable;

                if (useRangeIsland)
                {
                    // Issue #470 Slice H: the built-in start/end split is carried
                    // as island data (RangeStartId/RangeEndId/RangeSplitStr) —
                    // framework_layui.js's 'laydate' case installs it as the
                    // built-in `done`, chaining any caller-supplied DoneFn AFTER
                    // the split — reproducing the legacy inline range
                    // <script>'s done: body exactly (split first, then the
                    // caller's DoneFunc).
                    var rangeOpts = BuildLaydateOpts(rawMin, rawMax, isRangeMode: true);
                    var rangeAction = new LaydateIslandAction
                    {
                        Opts = rangeOpts,
                        ReadyFn = rangeReadyIsIdentifier ? ReadyFunc : null,
                        ChangeFn = rangeChangeIsIdentifier ? ChangeFunc : null,
                        DoneFn = rangeDoneIsIdentifier ? DoneFunc : null,
                        RangeStartId = RangeStartName,
                        RangeEndId = RangeEndName,
                        RangeSplitStr = RangeDoneSplit
                    };
                    var rangeJson = LayuiIslandJson.Serialize(rangeAction, _laydateJsonOptions);
                    output.PostElement.AppendHtml(
                        $"<script type=\"application/json\" class=\"wtm-dialog-init\">{rangeJson}</script>");
                }
                else
                {
                    var rangeNonIdentifierAttrs = new List<string>();
                    if (rangeReadyPresent && !rangeReadyIsIdentifier) { rangeNonIdentifierAttrs.Add(nameof(ReadyFunc)); }
                    if (rangeChangePresent && !rangeChangeIsIdentifier) { rangeNonIdentifierAttrs.Add(nameof(ChangeFunc)); }
                    if (rangeDonePresent && !rangeDoneIsIdentifier) { rangeNonIdentifierAttrs.Add(nameof(DoneFunc)); }
                    // Issue #753: same ZERO-characters-when-flag-off invariant as the
                    // single-field path above.
                    var rangeWarnJs = (UIConfig.UseSelectIslandRender && rangeNonIdentifierAttrs.Count > 0)
                        ? BuildDeprecationWarnScript(rangeNonIdentifierAttrs)
                        : string.Empty;

                    var rangeScript = $@"
<script>
{rangeWarnJs}layui.use(['laydate'], function() {{
    var laydate = layui.laydate;
    var dateIns = laydate.render({{
        elem: '#{Id}',
        type: '{Type.ToString().ToLower()}',
        range: true
        {(string.IsNullOrEmpty(Format) ? string.Empty : $",format: '{JavaScriptEncoder.Default.Encode(Format)}'")}
        {(string.IsNullOrEmpty(Min) ? string.Empty : $",min: {Min}")}
        {(string.IsNullOrEmpty(Max) ? string.Empty : $",max: {Max}")}
        {(!ZIndex.HasValue ? string.Empty : $",zIndex: {ZIndex.Value}")}
        {(!ShowBottom.HasValue ? string.Empty : $",showBottom: {ShowBottom.Value.ToString().ToLower()}")}
        {(!ConfirmOnly.HasValue ? string.Empty : ShowBottom.HasValue && ShowBottom.Value && ConfirmOnly.Value || !ShowBottom.HasValue && ConfirmOnly.Value ? $",btns: ['confirm']" : string.Empty)}
        {(!Calendar.HasValue ? string.Empty : $",calendar: {Calendar.Value.ToString().ToLower()}")}
        {(!Lang.HasValue ? string.Empty : $",lang: '{Lang.Value.ToString().ToLower()}'")}
        {(Mark == null || Mark.Count == 0 ? string.Empty : $",mark: {LayuiIslandJson.Serialize(Mark)}")}
        {(string.IsNullOrEmpty(ReadyFunc) ? string.Empty : $",ready: function(value){{{ReadyFunc}(value,dateIns)}}")}
        {(string.IsNullOrEmpty(ChangeFunc) ? string.Empty : $",change: function(value,date,endDate){{{ChangeFunc}(value,date,endDate,dateIns)}}")}
        ,done: function(value, date, endDate) {{
            document.getElementById('{RangeStartName}').value = value.split(' - ')[0] || '';
            document.getElementById('{RangeEndName}').value = value.split(' - ')[1] || '';
            {(string.IsNullOrEmpty(DoneFunc) ? string.Empty : $"{DoneFunc}(value,date,endDate,dateIns);")}
        }}
    }});
}});
</script>";
                    output.PostElement.AppendHtml(rangeScript);
                }
            }

            base.Process(context, output);
        }
    }

    // Issue #556 (#470-B slice 1): DTO for the bare (non-wrapped) laydate JSON
    // island — {"type":"laydate","opts":{...}}. ff._normalizeIslandPayload
    // (framework_layui.js) wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects; not part of the public API surface.
    //
    // Issue #470 Slice H: extended with ReadyFn/ChangeFn/DoneFn — caller
    // callback NAMES (compile-time, developer-authored Razor literals from
    // the ReadyFunc/ChangeFunc/DoneFunc TagHelper attributes — NEVER
    // field/request/model data, the same trust class as FormTagHelper's
    // beforeSubmit #558/#561 and TextBoxTagHelper's bindInput #601), present
    // only when the name is a plain identifier
    // (/^[A-Za-z_$][\w$]*\z/ server-side / /^[A-Za-z_$][\w$]*$/ client-side).
    // framework_layui.js resolves each through the SAME #558/#601 guarded
    // ff._resolveGuardedWindowFn lookup before wiring it to the live laydate
    // instance — a failed resolution silently skips just that one callback,
    // never throws, never evals. RangeStartId/RangeEndId/RangeSplitStr are
    // present only for the two-hidden-input IsRange path — see
    // DateTimeTagHelper.RangeDoneSplit.
    internal class LaydateIslandAction
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "laydate";

        [System.Text.Json.Serialization.JsonPropertyName("opts")]
        public Dictionary<string, object> Opts { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("readyFn")]
        public string ReadyFn { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("changeFn")]
        public string ChangeFn { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("doneFn")]
        public string DoneFn { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rangeStartId")]
        public string RangeStartId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rangeEndId")]
        public string RangeEndId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rangeSplitStr")]
        public string RangeSplitStr { get; set; }
    }
}
