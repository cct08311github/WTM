using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Text;
using WalkingTec.Mvvm.Core;
using System.Linq;

namespace WalkingTec.Mvvm.TagHelpers.LayUI.Chart
{
    public enum ChartThemeEnum { light, dark, vintage, chalk, essos, macarons, roma, walden, westeros, wonderland }

    public enum ChartTypeEnum { Bar, Pie, Line, PieHollow, Scatter }

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
    var xl = '{(NameX == "" ? "" : JsStringEncoder.Escape(NameX) + ":")}';
    var yl = '{(NameY == "" ? "" : JsStringEncoder.Escape(NameY) + ":")}';
    var al = '{(NameAddition == "" ? "" : JsStringEncoder.Escape(NameAddition) + ":")}';
    var cl = '{(NameCategory == "" ? "" : JsStringEncoder.Escape(NameCategory) + ":")}';
    // series / category values come from data and the tooltip is rendered as HTML, so encode them
    // (self-contained helper so it behaves the same on every echarts version)
    var enc = function (v) {{
        return String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/""/g, '&quot;').replace(/'/g, '&#39;');
    }};
    return enc(params.seriesName) + ' <br/>'
                + xl + enc(params.value[0]) + ' <br/>'
                + yl + enc(params.value[1]) + ' <br/>'
                + al + enc(params.value[2]) + ' <br/>'
                + cl + enc(params.value[3]) + ' <br/>';
    }},
}},";
                }
                if (Type == ChartTypeEnum.Line)
                {
                    tooltip = "tooltip: {trigger: 'axis'},";
                }
            }

            var typeSeries = string.Empty;
            if (Type == ChartTypeEnum.PieHollow)
                typeSeries = $"\"type\":\"pie\",\"radius\": [\"40%\", \"70%\"]";
            else
                typeSeries = $"\"type\":\"{Type.ToString().ToLower()}\"";
            if (Type == ChartTypeEnum.Line)
                typeSeries += $",\"smooth\": {OpenSmooth.ToString().ToLower()}";


            string xAxis = "", yAxis = "";
            if (Type != ChartTypeEnum.Pie && Type != ChartTypeEnum.PieHollow)
            {
                if (IsHorizontal == false)
                {
                    xAxis = $"xAxis: {{name:'{JsStringEncoder.Escape(NameX)}',type: 'category'}},";
                    yAxis = $"yAxis: {{name:'{JsStringEncoder.Escape(NameY)}'}},";
                }
                else
                {
                    xAxis = $"xAxis: {{name:'{JsStringEncoder.Escape(NameY)}'}},";
                    yAxis = $"yAxis: {{name:'{JsStringEncoder.Escape(NameX)}',type: 'category'}},";
                }
                if (Type == ChartTypeEnum.Scatter)
                {
                    xAxis = $"xAxis: {{ name:'{JsStringEncoder.Escape(NameX)}',type: 'value',splitLine: {{ lineStyle: {{ type: 'dashed'}} }} }},";
                    yAxis = $"yAxis:{{name:'{JsStringEncoder.Escape(NameY)}',splitLine:{{lineStyle:{{type: 'dashed'}} }},scale: true}},";
                }
            }
            //string scatterlegend = "";
            //if (Type == ChartTypeEnum.Scatter && ShowLegend == true)
            //{
            //    scatterlegend = "true";
            //}
            output.PostElement.AppendHtml($@"
<script>
var {Id}Chart;
var themeTemp ={(Theme == null ? "'default'" : $"'{Theme.ToString()}'")};
{Id}Chart = echarts.init(document.getElementById('{Id}'),themeTemp);
{Id}ChartType = '{typeSeries}';
{Id}ChartLegend = '{ShowLegend.ToString().ToLower()}';
{Id}ChartUrl = '{JsStringEncoder.Escape(TriggerUrl)}';
var {Id}option;
{Id}Chart.setOption({{
    {(string.IsNullOrEmpty(Title) ? "" : $"title:{{text: '{JsStringEncoder.Escape(Title)}'}},")}
    {tooltip}
    {xAxis}
    {yAxis}
}});
setTimeout(function(){{
ff.RefreshChart('{Id}');
}},100);

</script>
");
            base.Process(context, output);
        }
    }
}
