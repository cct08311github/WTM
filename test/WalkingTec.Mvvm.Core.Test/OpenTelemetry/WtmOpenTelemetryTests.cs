#nullable enable
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.OpenTelemetry;

[TestClass]
public class WtmActivitySourcesTests
{
    [TestMethod]
    public void SourceNames_HaveExpectedValues()
    {
        Assert.AreEqual("WalkingTec.Mvvm.Core", WtmActivitySources.CoreName);
        Assert.AreEqual("WalkingTec.Mvvm.Analysis", WtmActivitySources.AnalysisName);
        Assert.AreEqual("WalkingTec.Mvvm.Etl", WtmActivitySources.EtlName);
    }

    [TestMethod]
    public void StaticSources_AreNotNull()
    {
        Assert.IsNotNull(WtmActivitySources.Core);
        Assert.IsNotNull(WtmActivitySources.Analysis);
        Assert.IsNotNull(WtmActivitySources.Etl);
    }

    [TestMethod]
    public void StaticSources_NamesMatchConstants()
    {
        Assert.AreEqual(WtmActivitySources.CoreName, WtmActivitySources.Core.Name);
        Assert.AreEqual(WtmActivitySources.AnalysisName, WtmActivitySources.Analysis.Name);
        Assert.AreEqual(WtmActivitySources.EtlName, WtmActivitySources.Etl.Name);
    }
}

[TestClass]
public class WtmOpenTelemetryExtensionTests
{
    [TestMethod]
    public void AddWtmOpenTelemetry_NoOptions_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWtmOpenTelemetry();

        // BuildServiceProvider verifies DI registration is consistent
        using var sp = services.BuildServiceProvider();
        Assert.IsNotNull(sp);
    }

    [TestMethod]
    public void AddWtmOpenTelemetry_WithOptions_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWtmOpenTelemetry(o =>
        {
            o.ServiceName = "TestService";
            o.ServiceVersion = "1.0.0";
            o.EnableAspNetCoreInstrumentation = false; // requires HTTP context pipeline
            o.EnableHttpClientInstrumentation = true;
            o.EnableWtmActivitySources = true;
        });

        using var sp = services.BuildServiceProvider();
        Assert.IsNotNull(sp);
    }

    [TestMethod]
    public void AddWtmOpenTelemetry_WithOtlpEndpoint_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWtmOpenTelemetry(o =>
        {
            o.OtlpEndpoint = "http://localhost:4317";
            o.EnableAspNetCoreInstrumentation = false;
        });

        using var sp = services.BuildServiceProvider();
        Assert.IsNotNull(sp);
    }

    [TestMethod]
    public void AddWtmOpenTelemetry_RegistersOpenTelemetryServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        int countBefore = services.Count;

        services.AddWtmOpenTelemetry(o =>
        {
            o.EnableAspNetCoreInstrumentation = false;
        });

        Assert.IsTrue(services.Count > countBefore,
            "AddWtmOpenTelemetry should register at least one service");
    }

    [TestMethod]
    public void AddWtmOpenTelemetry_RegistersOtelHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWtmOpenTelemetry(o =>
        {
            o.EnableAspNetCoreInstrumentation = false;
        });

        // The OTel SDK registers an IHostedService for lifecycle management
        bool hasOtelService = services.Any(sd =>
            sd.ServiceType.FullName?.Contains("OpenTelemetry") == true ||
            sd.ImplementationType?.FullName?.Contains("OpenTelemetry") == true ||
            sd.ImplementationFactory != null &&
                sd.ServiceType.Assembly.GetName().Name?.Contains("OpenTelemetry") == true);

        Assert.IsTrue(hasOtelService, "Expected at least one OpenTelemetry service to be registered");
    }

    [TestMethod]
    public void WtmOpenTelemetryOptions_Defaults_AreCorrect()
    {
        var opts = new WtmOpenTelemetryOptions();

        Assert.AreEqual("WtmApp", opts.ServiceName);
        Assert.IsNull(opts.ServiceVersion);
        Assert.IsTrue(opts.EnableAspNetCoreInstrumentation);
        Assert.IsTrue(opts.EnableHttpClientInstrumentation);
        Assert.IsTrue(opts.EnableWtmActivitySources);
        Assert.IsNull(opts.OtlpEndpoint);
        Assert.IsNull(opts.CustomTracing);
        Assert.IsNull(opts.CustomMetrics);
    }

    [TestMethod]
    public void AddWtmOpenTelemetry_CustomTracing_IsCalled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        bool customTracingCalled = false;

        services.AddWtmOpenTelemetry(o =>
        {
            o.EnableAspNetCoreInstrumentation = false;
            o.CustomTracing = _ => customTracingCalled = true;
        });

        using var sp = services.BuildServiceProvider();
        // Resolve TracerProvider to trigger the builder
        var tracerProvider = sp.GetService<global::OpenTelemetry.Trace.TracerProvider>();

        Assert.IsTrue(customTracingCalled, "CustomTracing delegate should have been invoked");
    }
}
