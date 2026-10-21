using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// ListExtension.ToChartData builds the JSON the LayUI wt:chart consumes (#221).
    /// The "Golden" strings below were captured from the implementation that built the JSON by string
    /// concatenation, so ordinary data must keep producing exactly the same text.
    /// </summary>
    [TestClass]
    public class ToChartDataTest
    {
        private static ChartData D(string category, double value, double valueX, string series, double addition)
            => new ChartData { Category = category, Value = value, ValueX = valueX, Series = series, Addition = addition };

        private static string[] Out(object o)
        {
            var t = o.GetType();
            return new[]
            {
                (string)t.GetProperty("dataset").GetValue(o),
                (string)t.GetProperty("series").GetValue(o),
                (string)t.GetProperty("legend").GetValue(o),
            };
        }

        private static void AssertJsonValid(string[] o)
        {
            foreach (var s in o)
            {
                using var doc = JsonDocument.Parse(s);
            }
        }

        [TestMethod]
        public void Golden_Bar()
        {
            var data = new List<ChartData> { D("Mon", 1.5, 0, "A", 0), D("Mon", 3, 0, "B", 0), D("Tue", 2, 0, "A", 0), D("Wed", 4.25, 0, null, 0) };
            CollectionAssert.AreEqual(GOLDEN_BAR, Out(data.ToChartData(seriesname: "Info")));
        }

        [TestMethod]
        public void Golden_NonAsciiText()
        {
            var data = new List<ChartData> { D("星期一", 10, 0, "銷售", 0), D("星期二", 20, 0, "銷售", 0), D("星期一", 5, 0, "成本", 0) };
            CollectionAssert.AreEqual(GOLDEN_CJK, Out(data.ToChartData(seriesname: "Info")));
        }

        [TestMethod]
        public void Golden_Scatter()
        {
            var data = new List<ChartData> { D("c1", 1.5, 10, "S1", 100), D("c2", 2, 20.5, "S1", 250), D("c3", 3, 30, "S2", 50) };
            CollectionAssert.AreEqual(GOLDEN_SCATTER, Out(data.ToChartData(seriesname: "Info")));
        }

        [TestMethod]
        public void Golden_ScatterWithRadius()
        {
            var data = new List<ChartData> { D("c1", 1.5, 10, "S1", 100), D("c2", 2, 20.5, "S1", 250), D("c3", 3, 30, "S2", 50) };
            CollectionAssert.AreEqual(GOLDEN_SCATTER_R50, Out(data.ToChartData(50)));
        }

        [TestMethod]
        public void Golden_EmptyAndNull()
        {
            CollectionAssert.AreEqual(GOLDEN_EMPTY, Out(new List<ChartData>().ToChartData()));
            CollectionAssert.AreEqual(GOLDEN_EMPTY, Out(((List<ChartData>)null).ToChartData()));
        }

        private const string Hostile = "a\"b\\c</script><img src=x onerror=alert(1)>'\r\n\t\u2028";

        [TestMethod]
        public void SpecialCharacters_Bar_ProduceValidJsonThatRoundTrips()
        {
            var data = new List<ChartData> { D(Hostile, 1, 0, Hostile + "S", 0), D("x", 2, 0, "S2", 0) };
            var o = Out(data.ToChartData(seriesname: "In\"fo"));

            AssertJsonValid(o);
            using var dataset = JsonDocument.Parse(o[0]);
            var rows = dataset.RootElement.GetProperty("source").EnumerateArray().ToList();
            Assert.AreEqual("In\"fo", rows[0][0].GetString());
            Assert.AreEqual(Hostile + "S", rows[0][1].GetString());
            Assert.AreEqual(Hostile, rows[1][0].GetString());
            Assert.AreEqual("1", rows[1][1].GetString());
            using var legend = JsonDocument.Parse(o[2]);
            Assert.AreEqual(Hostile + "S", legend.RootElement.GetProperty("data")[0].GetString());
        }

        [TestMethod]
        public void SpecialCharacters_Scatter_ProduceValidJsonThatRoundTrips()
        {
            var data = new List<ChartData> { D(Hostile, 1, 5, Hostile + "S", 10), D("d", 2, 6, "S2", 20) };
            var o = Out(data.ToChartData());

            AssertJsonValid(o);
            using var dataset = JsonDocument.Parse(o[0]);
            var src = dataset.RootElement[0].GetProperty("source")[0];
            Assert.AreEqual(5, src[0].GetDouble());
            Assert.AreEqual(Hostile, src[3].GetString());
            Assert.AreEqual(Hostile + "S", src[4].GetString());
            Assert.AreEqual(Hostile + "S", dataset.RootElement[1].GetProperty("transform").GetProperty("config").GetProperty("value").GetString());
            using var series = JsonDocument.Parse(o[1]);
            Assert.AreEqual(Hostile + "S", series.RootElement[0].GetProperty("name").GetString());
            Assert.AreEqual(1, series.RootElement[0].GetProperty("datasetIndex").GetInt32());
            StringAssert.StartsWith(series.RootElement[0].GetProperty("symbolSize").GetString(), "function(data) {return data[2] / (");
        }

        [TestMethod]
        public void SeriesNamesWithFunctionLikePrefix_GetLeadingSpace()
        {
            // the name must not be parsed as a function by the host's JSONfns
            foreach (var name in new[] { "function(){}()", "_PxEgEr_/x/", "_NuFrRa_()=>1" })
            {
                var data = new List<ChartData> { D("c", 1, 5, name, 10), D("d", 2, 6, "S2", 20) };
                var o = Out(data.ToChartData());
                AssertJsonValid(o);
                using var series = JsonDocument.Parse(o[1]);
                var shown = series.RootElement[0].GetProperty("name").GetString();
                Assert.AreEqual(name, shown.TrimStart());
                Assert.IsFalse(shown.StartsWith(name.Substring(0, 8), StringComparison.Ordinal), name);
                using var legend = JsonDocument.Parse(o[2]);
                Assert.AreEqual(shown, legend.RootElement.GetProperty("data")[0].GetString());
                using var dataset = JsonDocument.Parse(o[0]);
                Assert.AreEqual(shown, dataset.RootElement[0].GetProperty("source")[0][4].GetString());
            }
        }

        [TestMethod]
        public void NonFiniteNumbers_StillProduceValidJson()
        {
            var data = new List<ChartData> { D("c", double.NaN, 5, "S", double.PositiveInfinity), D("d", 2, 6, "S", 20) };
            AssertJsonValid(Out(data.ToChartData()));
        }

        [TestMethod]
        public void Numbers_UseInvariantCulture()
        {
            var old = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var bar = new List<ChartData> { D("Mon", 1.5, 0, "A", 0) };
                Assert.AreEqual("{\"source\":[[\"Info\",\"A\"],[\"Mon\",\"1.5\"]]}", Out(bar.ToChartData())[0]);
                var scatter = new List<ChartData> { D("c", 1.5, 2.5, "A", 3.5) };
                var o = Out(scatter.ToChartData());
                AssertJsonValid(o);
                StringAssert.Contains(o[0], "[2.5,1.5,3.5,");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = old;
            }
        }

        private static readonly string[] GOLDEN_BAR = new[] { "{\"source\":[[\"Info\",\"A\",\"B\",\"Data\"],[\"Mon\",\"1.5\",\"3\",\"0\"],[\"Tue\",\"2\",\"0\",\"0\"],[\"Wed\",\"0\",\"0\",\"4.25\"]]}", "[{\"type\":\"charttype\"},{\"type\":\"charttype\"},{\"type\":\"charttype\"}]", "{\"data\":[\"A\",\"B\",\"Data\"]}" };
        private static readonly string[] GOLDEN_CJK = new[] { "{\"source\":[[\"Info\",\"銷售\",\"成本\"],[\"星期一\",\"10\",\"5\"],[\"星期二\",\"20\",\"0\"]]}", "[{\"type\":\"charttype\"},{\"type\":\"charttype\"}]", "{\"data\":[\"銷售\",\"成本\"]}" };
        private static readonly string[] GOLDEN_SCATTER = new[] { "[{\"source\":[[10,1.5,100,\"c1\",\"S1\"],[20.5,2,250,\"c2\",\"S1\"],[30,3,50,\"c3\",\"S2\"]]},{\"transform\": {\"type\": \"filter\",\"config\": {\"dimension\": 4,\"value\": \"S1\"}}},{\"transform\": {\"type\": \"filter\",\"config\": {\"dimension\": 4,\"value\": \"S2\"}}}]", "[{\"type\":\"charttype\",\"encode\":{\"x\":0,\"y\":1,\"tooltip\":[2,3]},\"name\": \"S1\",\"datasetIndex\": 1,\"symbolSize\": \"function(data) {return data[2] / (250 / 100)}\"},{\"type\":\"charttype\",\"encode\":{\"x\":0,\"y\":1,\"tooltip\":[2,3]},\"name\": \"S2\",\"datasetIndex\": 2,\"symbolSize\": \"function(data) {return data[2] / (250 / 100)}\"}]", "{\"data\":[\"S1\",\"S2\"]}" };
        private static readonly string[] GOLDEN_SCATTER_R50 = new[] { "[{\"source\":[[10,1.5,100,\"c1\",\"S1\"],[20.5,2,250,\"c2\",\"S1\"],[30,3,50,\"c3\",\"S2\"]]},{\"transform\": {\"type\": \"filter\",\"config\": {\"dimension\": 4,\"value\": \"S1\"}}},{\"transform\": {\"type\": \"filter\",\"config\": {\"dimension\": 4,\"value\": \"S2\"}}}]", "[{\"type\":\"charttype\",\"encode\":{\"x\":0,\"y\":1,\"tooltip\":[2,3]},\"name\": \"S1\",\"datasetIndex\": 1,\"symbolSize\": \"function(data) {return data[2] / (250 / 50)}\"},{\"type\":\"charttype\",\"encode\":{\"x\":0,\"y\":1,\"tooltip\":[2,3]},\"name\": \"S2\",\"datasetIndex\": 2,\"symbolSize\": \"function(data) {return data[2] / (250 / 50)}\"}]", "{\"data\":[\"S1\",\"S2\"]}" };
        private static readonly string[] GOLDEN_EMPTY = new[] { "{\"source\":[]}", "[]", "{\"data\":[]}" };
    }
}
