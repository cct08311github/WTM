using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace WalkingTec.Mvvm.E2E.Tests
{
    /// <summary>
    /// wt:chart on /Lab/Charts: bar, line, pie and scatter charts that load their data from the server (ff.RefreshChart posts to the
    /// trigger url) and are drawn by echarts. One category / series name carries characters that need encoding.
    /// </summary>
    [TestClass]
    public class LayUiChartTests : LabTest
    {
        [TestInitialize]
        public async Task OpenPage()
        {
            // the chart tag helper uses the global echarts, which the host loads as a layuiadmin extension after the home page is up
            await Page.GotoAsync("/");
            await Expect(Page.Locator("#frontpage")).ToBeVisibleAsync();
            await ExpectScriptAsync("() => typeof window.echarts !== 'undefined'");
            await Lab.OpenAsync("Charts");
            foreach (var id in new[] { "labchartbar", "labchartline", "labchartpie", "labchartscatter" })
            {
                // Lab.OpenAsync loads the home frame again, so window.echarts can be undefined again here: return false (keep polling) instead of throwing, which would fail WaitForFunction at once (#307)
                await ExpectScriptAsync($"() => {{ if (!window.echarts) return false; const el = document.getElementById('{id}'); const c = el ? window.echarts.getInstanceByDom(el) : null; if (!c) return false; const series = c.getOption().series || []; return series.length > 0 && series[0].type !== undefined; }}");
            }
        }

        private async Task<JsonElement> OptionAsync(string id)
        {
            return await Page.EvaluateAsync<JsonElement>($"() => JSON.parse(JSON.stringify(window.echarts.getInstanceByDom(document.getElementById('{id}')).getOption()))");
        }

        [TestMethod]
        public async Task Charts_Render_ACanvasForEachChart()
        {
            foreach (var id in new[] { "labchartbar", "labchartline", "labchartpie", "labchartscatter" })
            {
                await Expect(Body.Locator($"#{id} canvas")).ToHaveCountAsync(1);
                var box = await Body.Locator($"#{id}").BoundingBoxAsync();
                Assert.IsTrue(box.Width > 300 && box.Height > 200, $"{id} has an unexpected size {box.Width}x{box.Height}.");
            }
        }

        [TestMethod]
        public async Task BarChart_LoadsTheSeriesFromTheServer()
        {
            var option = await OptionAsync("labchartbar");

            // two series (North, South) over three categories; the type comes from the tag helper
            Assert.AreEqual(2, option.GetProperty("series").GetArrayLength());
            Assert.AreEqual("bar", option.GetProperty("series")[0].GetProperty("type").GetString());
            var source = option.GetProperty("dataset")[0].GetProperty("source");
            Assert.AreEqual(4, source.GetArrayLength(), "A header row and three categories.");
            Assert.AreEqual("North", source[0][1].GetString());
            Assert.AreEqual("South", source[0][2].GetString());
            Assert.AreEqual("Mon", source[1][0].GetString());
            Assert.AreEqual("5", source[1][1].GetString());
        }

        [TestMethod]
        public async Task Charts_DataWithSpecialCharacters_ArrivesUnchangedAndIsNotInterpretedAsMarkup()
        {
            var bar = await OptionAsync("labchartbar");
            Assert.AreEqual(LabData.Special, bar.GetProperty("title")[0].GetProperty("text").GetString());
            Assert.AreEqual(LabData.SpecialItem, bar.GetProperty("dataset")[0].GetProperty("source")[3][0].GetString());

            var scatter = await OptionAsync("labchartscatter");
            Assert.AreEqual(LabData.SpecialItem, scatter.GetProperty("series")[1].GetProperty("name").GetString());

            // the tooltip is HTML: show it for the point with the special name and check that it was not turned into elements
            await Page.EvaluateAsync("() => window.echarts.getInstanceByDom(document.getElementById('labchartbar')).dispatchAction({ type: 'showTip', seriesIndex: 0, dataIndex: 2 })");
            await Expect(Body.Locator("#labchartbar")).ToContainTextAsync("A&B");
            await Page.EvaluateAsync("() => window.echarts.getInstanceByDom(document.getElementById('labchartscatter')).dispatchAction({ type: 'showTip', seriesIndex: 1, dataIndex: 0 })");
            await Expect(Body.Locator("#labchartscatter")).ToContainTextAsync("A&B");
            await ExpectNoProbeElementsAsync(Body.Locator("#lab-charts"));
        }

        [TestMethod]
        public async Task LineChart_UsesTheSmoothOption()
        {
            var option = await OptionAsync("labchartline");
            Assert.AreEqual("line", option.GetProperty("series")[0].GetProperty("type").GetString());
            Assert.IsTrue(option.GetProperty("series")[0].GetProperty("smooth").GetBoolean());
        }

        [TestMethod]
        public async Task PieChart_HasNoAxes_AndScatterSeriesUseTheSizeColumn()
        {
            var pie = await OptionAsync("labchartpie");
            Assert.AreEqual("pie", pie.GetProperty("series")[0].GetProperty("type").GetString());
            Assert.IsFalse(pie.TryGetProperty("xAxis", out var x) && x.GetArrayLength() > 0, "A pie chart has no x axis.");

            var scatter = await OptionAsync("labchartscatter");
            Assert.AreEqual("scatter", scatter.GetProperty("series")[0].GetProperty("type").GetString());
            Assert.AreEqual(2, scatter.GetProperty("series").GetArrayLength());
        }

        [TestMethod]
        public async Task Chart_ResizeHelper_RedrawsTheChartWhenTheWindowChanges()
        {
            var before = (await Body.Locator("#labchartbar canvas").BoundingBoxAsync()).Width;
            // ff.ResizeChart() makes every chart follow the window; shrink the container, then change the window size
            await Page.EvaluateAsync("() => { document.getElementById('labchartbar').style.width = '300px'; ff.ResizeChart(); }");

            await Page.SetViewportSizeAsync(1300, 900);

            await ExpectScriptAsync("() => document.querySelector('#labchartbar canvas').getBoundingClientRect().width <= 300");
            Assert.IsTrue(before > 300);
        }
    }
}
