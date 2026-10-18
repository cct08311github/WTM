using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class WtmMiddlewareBodyBufferTest
    {
        private const string Json = "{\"Name\":\"abc\",\"中文\":1}";

        private static DefaultHttpContext Create(string body, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "PUT";
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.ContentType = contentType;
            return ctx;
        }

        [TestMethod]
        public async Task JsonBody_IsBufferedAndStreamRewound()
        {
            var ctx = Create(Json, "application/json; charset=utf-8");

            await WtmMiddleware.BufferRequestBodyAsync(ctx);

            Assert.AreEqual(Json, ctx.Items["DONOTUSE_REQUESTBODY"]);
            Assert.AreEqual(0, ctx.Request.Body.Position);
            using var reader = new StreamReader(ctx.Request.Body);
            Assert.AreEqual(Json, await reader.ReadToEndAsync());
        }

        [TestMethod]
        public async Task BodyStream_IsNotClosedAfterBuffering()
        {
            var ctx = Create(Json, "application/json");

            await WtmMiddleware.BufferRequestBodyAsync(ctx);

            Assert.IsTrue(ctx.Request.Body.CanRead);
            Assert.IsTrue(ctx.Request.Body.CanSeek);
        }

        [TestMethod]
        public async Task ExistingItem_IsOverwritten()
        {
            var ctx = Create(Json, "application/json");
            ctx.Items["DONOTUSE_REQUESTBODY"] = "old";

            await WtmMiddleware.BufferRequestBodyAsync(ctx);

            Assert.AreEqual(Json, ctx.Items["DONOTUSE_REQUESTBODY"]);
        }

        [TestMethod]
        public async Task NonJsonBody_IsBufferedByDefault()
        {
            var ctx = Create("plain text", "text/plain");

            await WtmMiddleware.BufferRequestBodyAsync(ctx);

            Assert.AreEqual("plain text", ctx.Items["DONOTUSE_REQUESTBODY"]);
            Assert.AreEqual(0, ctx.Request.Body.Position);
        }

        [TestMethod]
        public async Task FormRequest_IsNotBuffered()
        {
            var ctx = Create("a=1&b=2", "application/x-www-form-urlencoded");

            await WtmMiddleware.BufferRequestBodyAsync(ctx);

            Assert.IsFalse(ctx.Items.ContainsKey("DONOTUSE_REQUESTBODY"));
        }

        [TestMethod]
        public async Task EmptyBody_IsNotBuffered()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Body = new MemoryStream();
            ctx.Request.ContentLength = 0;
            ctx.Request.ContentType = "application/json";

            await WtmMiddleware.BufferRequestBodyAsync(ctx);

            Assert.IsFalse(ctx.Items.ContainsKey("DONOTUSE_REQUESTBODY"));
        }

        [TestMethod]
        [DataRow("application/json")]
        [DataRow("application/json; charset=utf-8")]
        [DataRow("Application/JSON")]
        [DataRow("application/problem+json")]
        [DataRow("application/vnd.api+json; charset=utf-8")]
        public async Task JsonOnly_BuffersJsonContentTypes(string contentType)
        {
            var ctx = Create(Json, contentType);

            await WtmMiddleware.BufferRequestBodyAsync(ctx, jsonOnly: true);

            Assert.AreEqual(Json, ctx.Items["DONOTUSE_REQUESTBODY"]);
            Assert.AreEqual(0, ctx.Request.Body.Position);
        }

        [TestMethod]
        [DataRow("text/plain")]
        [DataRow("application/xml")]
        [DataRow("application/octet-stream")]
        [DataRow(null)]
        public async Task JsonOnly_DoesNotBufferOtherContentTypes(string contentType)
        {
            var ctx = Create("<a/>", contentType);

            await WtmMiddleware.BufferRequestBodyAsync(ctx, jsonOnly: true);

            Assert.IsFalse(ctx.Items.ContainsKey("DONOTUSE_REQUESTBODY"));
            Assert.AreEqual(0, ctx.Request.Body.Position);
        }

        [TestMethod]
        public void BufferJsonBodyOnly_DefaultsToFalse()
        {
            Assert.IsFalse(new Configs().BufferJsonBodyOnly);
        }
    }
}
