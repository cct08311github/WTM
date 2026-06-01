#nullable enable
// Security regression tests for WTMContext.CallAPI exception information-leak fix — Issue #124.
// Asserts that the internal catch block no longer exposes ex.ToString() (stack trace +
// connection strings) to callers in production mode (IsQuickDebug == false), and that
// the full detail IS preserved in dev mode (IsQuickDebug == true).

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Security;

[TestClass]
public class WtmContextCallApiInfoLeakTests
{
    /// <summary>
    /// Helper: create a WTMContext whose ServiceProvider injects a mock
    /// IHttpClientFactory that throws the provided exception on any HTTP call.
    /// </summary>
    private static WTMContext CreateContextWithThrowingFactory(
        Exception toThrow, bool isQuickDebug = false)
    {
        // Build a mock HttpMessageHandler that throws
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(toThrow);

        var httpClient = new HttpClient(handler.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        factoryMock.Setup(f => f.CreateClient(string.Empty)).Returns(httpClient);

        // Wire a ServiceProvider that resolves IHttpClientFactory + ILoggerFactory
        var loggerFactory = NullLoggerFactory.Instance;
        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(sp => sp.GetService(typeof(IHttpClientFactory))).Returns(factoryMock.Object);
        spMock.Setup(sp => sp.GetService(typeof(ILoggerFactory))).Returns(loggerFactory);

        // GetRequiredService<T> is an extension method that calls GetService(typeof(T)) under the hood.
        // We need the service locator extension to resolve IHttpClientFactory.
        // Use Microsoft.Extensions.DependencyInjection extension: wrap with ServiceProviderAdapter.
        // Simpler: configure the mock to handle the required-service call pattern.
        spMock.Setup(sp => sp.GetService(typeof(IHttpClientFactory))).Returns(factoryMock.Object);

        CoreProgram.DefaultJsonOption ??= new System.Text.Json.JsonSerializerOptions();

        var wtm = MockWtmContext.CreateWtmContext();
        wtm.ConfigInfo!.IsQuickDebug = isQuickDebug;
        wtm.SetServiceProvider(spMock.Object);
        return wtm;
    }

    [TestMethod]
    public async Task CallAPI_production_exception_does_not_expose_stack_trace()
    {
        // A connection string embedded in an HttpRequestException simulates the scenario
        // where the HTTP client exception message contains sensitive info.
        const string sensitiveDetail = "Connection refused: Server=prod-db.internal;Password=s3cr3t!";
        var ex = new HttpRequestException(sensitiveDetail);
        var wtm = CreateContextWithThrowingFactory(ex, isQuickDebug: false);

        var result = await wtm.CallAPI<string>(null, "http://test/api");

        result.ErrorMsg.Should().NotContain("s3cr3t",
            "production mode must not expose exception detail in ErrorMsg");
        result.ErrorMsg.Should().NotContain("prod-db.internal",
            "production mode must not expose connection string info in ErrorMsg");
        result.ErrorMsg.Should().NotBeNullOrWhiteSpace(
            "a generic error message must still be returned");
        result.ErrorMsg.Should().Contain("error occurred",
            "the generic message should describe what happened");
    }

    [TestMethod]
    public async Task CallAPI_production_exception_does_not_expose_stack_trace_in_full_toString()
    {
        // ex.ToString() includes both the message AND the stack trace.
        // Pre-fix the code set ErrorMsg = ex.ToString() which includes at:... lines.
        var ex = new InvalidOperationException("internal detail with CS=password=hunter2");
        var wtm = CreateContextWithThrowingFactory(ex, isQuickDebug: false);

        var result = await wtm.CallAPI<string>(null, "http://test/api");

        result.ErrorMsg.Should().NotContain("at WalkingTec",
            "production mode must not include stack trace frames");
        result.ErrorMsg.Should().NotContain("hunter2",
            "production mode must not expose connection string embedded in exception message");
    }

    [TestMethod]
    public async Task CallAPI_dev_mode_exception_exposes_full_detail()
    {
        const string sensitiveDetail = "dev-env: Server=dev-db;Password=devpass123";
        var ex = new HttpRequestException(sensitiveDetail);
        var wtm = CreateContextWithThrowingFactory(ex, isQuickDebug: true);

        var result = await wtm.CallAPI<string>(null, "http://test/api");

        // In dev mode, the full ex.ToString() must be present for diagnostics.
        result.ErrorMsg.Should().Contain(sensitiveDetail,
            "dev mode should expose full exception detail to aid debugging");
    }
}
