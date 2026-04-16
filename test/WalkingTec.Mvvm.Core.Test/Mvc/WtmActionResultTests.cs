using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for issue #789 Phase 3C: WtmActionResult must serialize declarative
    /// actions as JSON and signal the client dispatcher via the X-WTM-Action
    /// response header. The legacy IsScript / eval() path is kept alive only
    /// through the separate FResult class.
    /// </summary>
    [TestClass]
    public class WtmActionResultTests
    {
        private static async Task<(string body, string header, int status)> ExecuteAsync(WtmActionResult result)
        {
            var httpContext = new DefaultHttpContext();
            var responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;

            var actionContext = new ActionContext
            {
                HttpContext = httpContext,
                RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
                ActionDescriptor = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()
            };

            await result.ExecuteResultAsync(actionContext);

            responseBody.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(responseBody);
            var body = await reader.ReadToEndAsync();
            var header = httpContext.Response.Headers.TryGetValue("X-WTM-Action", out var hdr)
                ? hdr.ToString()
                : string.Empty;
            return (body, header, httpContext.Response.StatusCode);
        }

        [TestMethod]
        public async Task Empty_result_emits_actions_array_and_sets_header()
        {
            var (body, header, status) = await ExecuteAsync(new WtmActionResult());

            Assert.AreEqual("application/json", header);
            Assert.AreEqual(200, status);
            var doc = JsonDocument.Parse(body);
            Assert.IsTrue(doc.RootElement.TryGetProperty("actions", out var actions));
            Assert.AreEqual(0, actions.GetArrayLength());
        }

        [TestMethod]
        public async Task CloseDialog_action_serializes_with_type_only()
        {
            var result = new WtmActionResult();
            result.CloseDialog();

            var (body, _, _) = await ExecuteAsync(result);
            var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("actions")[0];
            Assert.AreEqual("closeDialog", first.GetProperty("type").GetString());
            // Null fields must be omitted by JsonIgnoreCondition.WhenWritingNull.
            Assert.IsFalse(first.TryGetProperty("message", out _));
            Assert.IsFalse(first.TryGetProperty("url", out _));
        }

        [TestMethod]
        public async Task Alert_action_serializes_message_and_title()
        {
            var result = new WtmActionResult();
            result.Alert("Saved", "info");

            var (body, _, _) = await ExecuteAsync(result);
            var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("actions")[0];
            Assert.AreEqual("alert", first.GetProperty("type").GetString());
            Assert.AreEqual("Saved", first.GetProperty("message").GetString());
            Assert.AreEqual("info", first.GetProperty("title").GetString());
        }

        [TestMethod]
        public async Task Chain_CloseDialog_RefreshGrid_produces_two_actions_in_order()
        {
            var result = new WtmActionResult();
            result.CloseDialog().RefreshGrid("myGrid");

            var (body, _, _) = await ExecuteAsync(result);
            var doc = JsonDocument.Parse(body);
            var actions = doc.RootElement.GetProperty("actions");
            Assert.AreEqual(2, actions.GetArrayLength());
            Assert.AreEqual("closeDialog", actions[0].GetProperty("type").GetString());
            Assert.AreEqual("refreshGrid", actions[1].GetProperty("type").GetString());
            Assert.AreEqual("myGrid", actions[1].GetProperty("winId").GetString());
        }

        [TestMethod]
        public async Task Reload_action_is_structurally_safe_no_script_field()
        {
            var result = new WtmActionResult();
            result.Reload();

            var (body, _, _) = await ExecuteAsync(result);
            var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("actions")[0];
            Assert.AreEqual("reload", first.GetProperty("type").GetString());
            // No "script" or "code" field exists - the action is declarative only.
            Assert.IsFalse(first.TryGetProperty("script", out _));
            Assert.IsFalse(first.TryGetProperty("code", out _));
        }

        [TestMethod]
        public async Task Redirect_action_carries_url_field()
        {
            var result = new WtmActionResult();
            result.Redirect("/admin/home");

            var (body, _, _) = await ExecuteAsync(result);
            var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("actions")[0];
            Assert.AreEqual("redirect", first.GetProperty("type").GetString());
            Assert.AreEqual("/admin/home", first.GetProperty("url").GetString());
        }

        [TestMethod]
        public async Task ContentType_is_application_json_not_html()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Response.Body = new MemoryStream();
            var actionContext = new ActionContext
            {
                HttpContext = httpContext,
                RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
                ActionDescriptor = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()
            };

            var result = new WtmActionResult();
            result.Alert("hi");
            await result.ExecuteResultAsync(actionContext);

            StringAssert.StartsWith(httpContext.Response.ContentType ?? "", "application/json");
        }

        [TestMethod]
        public async Task RefreshGridRow_falls_through_to_RefreshGrid_like_legacy()
        {
            var result = new WtmActionResult();
            result.RefreshGridRow(123, "myGrid");

            var (body, _, _) = await ExecuteAsync(result);
            var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("actions")[0];
            Assert.AreEqual("refreshGrid", first.GetProperty("type").GetString());
            Assert.AreEqual("myGrid", first.GetProperty("winId").GetString());
        }
    }
}
