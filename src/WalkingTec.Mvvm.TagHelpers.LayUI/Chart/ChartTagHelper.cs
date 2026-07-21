using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WalkingTec.Mvvm.Core;
using System.Linq;

namespace WalkingTec.Mvvm.TagHelpers.LayUI.Chart
{
    public enum ChartThemeEnum { light, dark, vintage, chalk, essos, macarons, roma, walden, westeros, wonderland }

    /// <summary>
    /// Chart type enum for wt:chart tag helper.
    /// Existing values are preserved; new L6 types are additive and opt-in.
    /// The enum value is lowercased and passed directly to the JS renderer as
    /// config.chartType, which buildChartOption() in framework_dashboard.js maps
    /// to the appropriate ECharts option shape.
    /// </summary>
    public enum ChartTypeEnum
    {
        // ── Existing types (unchanged behaviour) ──────────────────────────────
        Bar,
        Pie,
        Line,
        PieHollow,
        Scatter,
        // ── L6 new types ──────────────────────────────────────────────────────
        Gauge,
        Funnel,
        Radar,
        Heatmap,
        Sankey
    }

    [HtmlTargetElement("wt:chart", TagStructure = TagStructure.WithoutEndTag)]
    public class ChartTagHelper : BaseElementTag
    {
        //public ModelExpression Field { get; set; }

        private string _chartIdUserSet;

        private string _id;
        public new string Id
        {
            get
            {
                if (string.IsNullOrEmpty(_id))
                {
                    if (string.IsNullOrEmpty(_chartIdUserSet))
                    {
                        _id = "chart" + Guid.NewGuid().ToString("N");
                    }
                    else
                    {
                        _id = _chartIdUserSet;
                    }
                }
                return _id;
            }
            set
            {
                _id = value;
                _chartIdUserSet = value;
            }
        }


        public string Title { get; set; }

        public bool? ShowLegend { get; set; }

        public bool? ShowTooltip { get; set; }

        public ChartThemeEnum? Theme { get; set; }

        public ChartTypeEnum Type { get; set; }

        public bool IsHorizontal { get; set; }
        //折线图弧度
        public bool OpenSmooth { get; set; }

        public string TriggerUrl { get; set; }
        public int Radius { get; set; } = 100;

        public string NameX { get; set; } = "";
        public string NameY { get; set; } = "";
        public string NameAddition { get; set; } = "";
        public string NameCategory { get; set; } = "";

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.TagName = "div";
            output.Attributes.Add("ischart", "1");
            output.Attributes.SetAttribute("id", Id);
            output.TagMode = TagMode.StartTagAndEndTag;
            //var cd = Field?.Model as List<ChartData>;
            //if (cd == null)
            //{
            //    output.Content.SetContent("Field must be set, and has to be of type List<ChartData>");
            //    return;
            //}

            string tooltip = "";

            if (ShowLegend == null)
            {
                ShowLegend = true;
            }
            if (ShowTooltip == null)
            {
                ShowTooltip = true;
            }
            if (ShowTooltip == true)
            {
                tooltip = "tooltip: {},";
                if (Type == ChartTypeEnum.Scatter)
                {
                    tooltip = @$"tooltip:{{
formatter: function (params) {{
    var xl = '{(NameX == "" ? "" : NameX + ":")}';
    var yl = '{(NameY == "" ? "" : NameY + ":")}';
    var al = '{(NameAddition == "" ? "" : NameAddition + ":")}';
    var cl = '{(NameCategory == "" ? "" : NameCategory + ":")}';
    return params.seriesName + ' <br/>'
                + xl + params.value[0] + ' <br/>'
                + yl + params.value[1] + ' <br/>'
                + al + params.value[2] + ' <br/>'
                + cl + params.value[3] + ' <br/>';
    }},
}},";
                }
                if (Type == ChartTypeEnum.Line)
                {
                    tooltip = "tooltip: {trigger: 'axis'},";
                }
            }

            // Build the typeSeries fragment forwarded to the legacy ff.RefreshChart path.
            // For new L6 types (gauge, funnel, radar, heatmap, sankey) the JS renderer
            // (buildChartOption) builds the full option itself; we just pass the type string.
            var typeSeries = string.Empty;
            if (Type == ChartTypeEnum.PieHollow)
                typeSeries = $"\"type\":\"pie\",\"radius\": [\"40%\", \"70%\"]";
            else
                typeSeries = $"\"type\":\"{Type.ToString().ToLower()}\"";
            if (Type == ChartTypeEnum.Line)
                typeSeries += $",\"smooth\": {OpenSmooth.ToString().ToLower()}";


            // Types that manage their own axes inside buildChartOption on the JS side
            // do not need server-side xAxis/yAxis script fragments.
            bool noCartesianAxes = Type == ChartTypeEnum.Pie
                || Type == ChartTypeEnum.PieHollow
                || Type == ChartTypeEnum.Gauge
                || Type == ChartTypeEnum.Funnel
                || Type == ChartTypeEnum.Radar
                || Type == ChartTypeEnum.Heatmap
                || Type == ChartTypeEnum.Sankey;

            string xAxis = "", yAxis = "";
            if (!noCartesianAxes)
            {
                if (IsHorizontal == false)
                {
                    xAxis = $"xAxis: {{name:'{NameX}',type: 'category'}},";
                    yAxis = $"yAxis: {{name:'{NameY}'}},";
                }
                else
                {
                    xAxis = $"xAxis: {{name:'{NameY}'}},";
                    yAxis = $"yAxis: {{name:'{NameX}',type: 'category'}},";
                }
                if (Type == ChartTypeEnum.Scatter)
                {
                    xAxis = $"xAxis: {{ name:'{NameX}',type: 'value',splitLine: {{ lineStyle: {{ type: 'dashed'}} }} }},";
                    yAxis = $"yAxis:{{name:'{NameY}',splitLine:{{lineStyle:{{type: 'dashed'}} }},scale: true}},";
                }
            }
            //string scatterlegend = "";
            //if (Type == ChartTypeEnum.Scatter && ShowLegend == true)
            //{
            //    scatterlegend = "true";
            //}

            // Issue #470 Slice N1: opt-in (WtmUIOptions.UseSelectIslandRender,
            // default OFF — the SAME flag Slices J/K/L/M/N1's renderTreeContainer
            // use) eval-free 'renderChart' island render. ChartTagHelper has no
            // developer-facing callback attribute at all (unlike ComboBox/Tree/
            // Transfer's ChangeFunc or TreeContainer's ClickFunc) — so there is
            // no identifier-vs-non-identifier 3-way decision to make here; the
            // ONLY gate is UseSelectIslandRender itself (same as UploadTagHelper,
            // #470 Slice L).
            if (UIConfig.UseSelectIslandRender)
            {
                var renderChartAction = new RenderChartIslandAction
                {
                    Id = Id,
                    Theme = Theme?.ToString(),
                    ChartType = typeSeries,
                    Legend = ShowLegend == true,
                    Url = TriggerUrl,
                    Title = string.IsNullOrEmpty(Title) ? null : Title,
                    ShowTooltip = ShowTooltip == true,
                    ChartTypeName = Type.ToString().ToLower(),
                    IsHorizontal = IsHorizontal,
                    NoCartesianAxes = noCartesianAxes,
                    NameX = NameX,
                    NameY = NameY,
                    NameAddition = NameAddition,
                    NameCategory = NameCategory
                };
                output.PostElement.AppendHtml($@"<script type=""application/json"" class=""wtm-dialog-init"">{LayuiIslandJson.Serialize(renderChartAction, _islandJsonOptions)}</script>");
            }
            else
            {
                output.PostElement.AppendHtml($@"
<script>
var {Id}Chart;
var themeTemp ={(Theme == null ? "'default'" : $"'{Theme.ToString()}'")};
{Id}Chart = echarts.init(document.getElementById('{Id}'),themeTemp);
{Id}ChartType = '{typeSeries}';
{Id}ChartLegend = '{ShowLegend.ToString().ToLower()}';
{Id}ChartUrl = '{TriggerUrl}';
var {Id}option;
{Id}Chart.setOption({{
    {(string.IsNullOrEmpty(Title) ? "" : $"title:{{text: '{Title}'}},")}
    {tooltip}
    {xAxis}
    {yAxis}
}});
setTimeout(function(){{
ff.RefreshChart('{Id}');
}},100);

</script>
");
            }
            base.Process(context, output);
        }

        // Issue #470 Slice N1 (mirrors ComboBoxTagHelper's/TransferTagHelper's
        // _islandJsonOptions): island DTOs omit null members so the optional
        // Theme/Title fields contribute zero JSON when unset.
        private static readonly JsonSerializerOptions _islandJsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    // Issue #470 Slice N1: DTO for the bare (non-wrapped) 'renderChart' JSON
    // island — the opt-in (WtmUIOptions.UseSelectIslandRender, default OFF —
    // the SAME flag #470 Slices J/K/L/M/N1's renderTreeContainer use) eval-free
    // replacement for the inline `{Id}Chart = echarts.init(...);
    // {Id}Chart.setOption(...)` &lt;script&gt; ChartTagHelper otherwise emits.
    // ff._renderChartAction (framework_layui.js) is the sole consumer;
    // ff._normalizeIslandPayload wraps this into the {actions:[...]} shape
    // ff.DispatchAction expects. Not part of the public API surface.
    internal sealed class RenderChartIslandAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "renderChart";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        // Lowercase ChartThemeEnum name (e.g. "dark"), or null for the legacy
        // inline render's 'default' fallback.
        [JsonPropertyName("theme")]
        public string Theme { get; set; }

        // Raw `"type":"..."` JSON fragment — mirrors window[id+'ChartType']
        // exactly (ff.RefreshChart regex-substitutes this into fetched series
        // data; NOT itself valid standalone JSON).
        [JsonPropertyName("chartType")]
        public string ChartType { get; set; }

        [JsonPropertyName("legend")]
        public bool Legend { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("showTooltip")]
        public bool ShowTooltip { get; set; }

        // Lowercase ChartTypeEnum name (e.g. "scatter", "line", "bar") — the
        // renderer's tooltip/axis variant selector.
        [JsonPropertyName("chartTypeName")]
        public string ChartTypeName { get; set; }

        [JsonPropertyName("isHorizontal")]
        public bool IsHorizontal { get; set; }

        [JsonPropertyName("noCartesianAxes")]
        public bool NoCartesianAxes { get; set; }

        [JsonPropertyName("nameX")]
        public string NameX { get; set; }

        [JsonPropertyName("nameY")]
        public string NameY { get; set; }

        [JsonPropertyName("nameAddition")]
        public string NameAddition { get; set; }

        [JsonPropertyName("nameCategory")]
        public string NameCategory { get; set; }
    }
}
