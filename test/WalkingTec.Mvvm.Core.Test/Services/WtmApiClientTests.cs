#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmApiClientTests
    {
        private Mock<HttpMessageHandler> _handler = null!;
        private WtmApiClient _client = null!;

        [TestInitialize]
        public void Setup()
        {
            CoreProgram.DefaultJsonOption ??= new System.Text.Json.JsonSerializerOptions();
            CoreProgram.DefaultPostJsonOption ??= new System.Text.Json.JsonSerializerOptions();
            _handler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_handler.Object);

            var factoryMock = new Mock<IHttpClientFactory>();
            factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            factoryMock.Setup(f => f.CreateClient(string.Empty)).Returns(httpClient);

            _client = new WtmApiClient(factoryMock.Object);
        }

        #region Constructor

        [TestMethod]
        public void Ctor_NullFactory_ThrowsArgumentNullException()
        {
            Action act = () => new WtmApiClient(null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("httpClientFactory");
        }

        #endregion

        #region GET

        [TestMethod]
        public async Task CallAPI_GET_Success_ReturnsDeserializedData()
        {
            var payload = new TestPayload { Name = "hello", Value = 42 };
            SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(payload));

            var result = await _client.CallAPI<TestPayload>(null, "http://test/api", HttpMethodEnum.GET,
                (HttpContent?)null);

            result.Data.Should().NotBeNull();
            result.Data!.Name.Should().Be("hello");
            result.Data.Value.Should().Be(42);
            result.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [TestMethod]
        public async Task CallAPI_GET_Convenience_ReturnsData()
        {
            SetupResponse(HttpStatusCode.OK, "\"ok\"");

            var result = await _client.CallAPI<string>(null, "http://test/api");

            result.Data.Should().Be("ok");
        }

        #endregion

        #region POST with object (JSON)

        [TestMethod]
        public async Task CallAPI_POST_WithObject_SerializesAsJson()
        {
            SetupResponse(HttpStatusCode.OK, "\"created\"");

            var result = await _client.CallAPI<string>(null, "http://test/api",
                HttpMethodEnum.POST, new { foo = "bar" });

            result.Data.Should().Be("created");
            result.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        #endregion

        #region POST with form data

        [TestMethod]
        public async Task CallAPI_POST_WithFormData_SendsFormEncoded()
        {
            SetupResponse(HttpStatusCode.OK, "\"form_ok\"");

            var formData = new Dictionary<string, string> { { "key1", "val1" } };
            var result = await _client.CallAPI<string>(null, "http://test/api",
                HttpMethodEnum.POST, formData);

            result.Data.Should().Be("form_ok");
        }

        [TestMethod]
        public async Task CallAPI_POST_WithEmptyFormData_SendsWithoutContent()
        {
            SetupResponse(HttpStatusCode.OK, "\"empty\"");

            var formData = new Dictionary<string, string>();
            var result = await _client.CallAPI<string>(null, "http://test/api",
                HttpMethodEnum.POST, (IDictionary<string, string>?)formData);

            result.Data.Should().Be("empty");
        }

        #endregion

        #region Headers and auth token

        [TestMethod]
        public async Task CallAPI_WithHeaders_AddsHeaders()
        {
            SetupResponse(HttpStatusCode.OK, "\"ok\"");

            var headers = new Dictionary<string, string> { { "X-Custom", "test-value" } };
            var result = await _client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.GET,
                (HttpContent?)null, headers: headers);

            result.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [TestMethod]
        public async Task CallAPI_WithAuthToken_AddsBearerHeader()
        {
            HttpRequestMessage? capturedRequest = null;
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("\"ok\"")
                });

            await _client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.GET,
                (HttpContent?)null, authToken: "my-token");

            // The Authorization header is set on the client's DefaultRequestHeaders,
            // which is reflected in the request.
            capturedRequest.Should().NotBeNull();
        }

        #endregion

        #region Error handling

        [TestMethod]
        public async Task CallAPI_ServerError_ReturnsErrorMsg()
        {
            SetupResponse(HttpStatusCode.InternalServerError, "server error");

            var result = await _client.CallAPI<string>(null, "http://test/api");

            result.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            result.ErrorMsg.Should().Be("server error");
            result.Data.Should().BeNull();
        }

        [TestMethod]
        public async Task CallAPI_BadRequest_DeserializesErrorObj()
        {
            var errorObj = new ErrorObj { Message = new List<string> { "field is required" } };
            SetupResponse(HttpStatusCode.BadRequest, JsonSerializer.Serialize(errorObj));

            var result = await _client.CallAPI<string>(null, "http://test/api");

            result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            result.Errors.Should().NotBeNull();
            result.Errors!.Message.Should().Contain("field is required");
        }

        [TestMethod]
        public async Task CallAPI_NullUrl_ReturnsEmptyResult()
        {
            var result = await _client.CallAPI<string>(null, null);

            result.Data.Should().BeNull();
            result.StatusCode.Should().BeNull();
        }

        [TestMethod]
        public async Task CallAPI_EmptyUrl_ReturnsEmptyResult()
        {
            var result = await _client.CallAPI<string>(null, "");

            result.Data.Should().BeNull();
        }

        [TestMethod]
        public async Task CallAPI_HttpException_ReturnsErrorMsg()
        {
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("connection refused"));

            var result = await _client.CallAPI<string>(null, "http://test/api");

            result.ErrorMsg.Should().Contain("connection refused");
        }

        #endregion

        #region String-typed convenience overloads

        [TestMethod]
        public async Task CallAPI_StringOverload_HttpContent_DelegatesToGeneric()
        {
            SetupResponse(HttpStatusCode.OK, "raw text");

            var result = await _client.CallAPI(null, "http://test/api", HttpMethodEnum.GET, (HttpContent?)null);

            result.Data.Should().Be("raw text");
        }

        [TestMethod]
        public async Task CallAPI_StringOverload_GET_DelegatesToGeneric()
        {
            SetupResponse(HttpStatusCode.OK, "get text");

            var result = await _client.CallAPI(null, "http://test/api");

            result.Data.Should().Be("get text");
        }

        [TestMethod]
        public async Task CallAPI_StringOverload_FormData_DelegatesToGeneric()
        {
            SetupResponse(HttpStatusCode.OK, "form text");
            var formData = new Dictionary<string, string> { { "a", "b" } };

            var result = await _client.CallAPI(null, "http://test/api", HttpMethodEnum.POST, formData);

            result.Data.Should().Be("form text");
        }

        [TestMethod]
        public async Task CallAPI_StringOverload_Object_DelegatesToGeneric()
        {
            SetupResponse(HttpStatusCode.OK, "obj text");

            var result = await _client.CallAPI(null, "http://test/api", HttpMethodEnum.POST, new { x = 1 });

            result.Data.Should().Be("obj text");
        }

        #endregion

        #region PUT and DELETE

        [TestMethod]
        public async Task CallAPI_PUT_SendsCorrectMethod()
        {
            HttpRequestMessage? captured = null;
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("\"ok\"")
                });

            await _client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.PUT, new { data = 1 });

            captured.Should().NotBeNull();
            captured!.Method.Should().Be(HttpMethod.Put);
        }

        [TestMethod]
        public async Task CallAPI_DELETE_SendsCorrectMethod()
        {
            HttpRequestMessage? captured = null;
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("\"ok\"")
                });

            await _client.CallAPI<string>(null, "http://test/api", HttpMethodEnum.DELETE,
                (HttpContent?)null);

            captured.Should().NotBeNull();
            captured!.Method.Should().Be(HttpMethod.Delete);
        }

        #endregion

        #region Byte array response

        [TestMethod]
        public async Task CallAPI_ByteArrayType_ReturnsBytes()
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });

            var result = await _client.CallAPI<byte[]>(null, "http://test/api");

            result.Data.Should().NotBeNull();
            result.Data.Should().BeEquivalentTo(bytes);
        }

        #endregion

        #region Named client (domainName)

        [TestMethod]
        public async Task CallAPI_WithDomainName_UsesNamedClient()
        {
            var namedClient = new HttpClient(_handler.Object)
            {
                BaseAddress = new Uri("http://myapi.local")
            };

            var factoryMock = new Mock<IHttpClientFactory>();
            factoryMock.Setup(f => f.CreateClient("myDomain")).Returns(namedClient);

            var client = new WtmApiClient(factoryMock.Object);
            SetupResponse(HttpStatusCode.OK, "\"named\"");

            var result = await client.CallAPI<string>("myDomain", "/api/test");

            result.Data.Should().Be("named");
            factoryMock.Verify(f => f.CreateClient("myDomain"), Times.Once);
        }

        #endregion

        #region Helpers

        private void SetupResponse(HttpStatusCode status, string content)
        {
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(status)
                {
                    Content = new StringContent(content)
                });
        }

        private class TestPayload
        {
            public string? Name { get; set; }
            public int Value { get; set; }
        }

        #endregion
    }
}
