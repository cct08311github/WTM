#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc;

/// <summary>
/// Configuration options for <see cref="WtmOpenTelemetryExtension.AddWtmOpenTelemetry"/>.
/// </summary>
public class WtmOpenTelemetryOptions
{
    /// <summary>OTel service name reported to the backend (default: <c>WtmApp</c>).</summary>
    public string ServiceName { get; set; } = "WtmApp";

    /// <summary>Optional service version string injected into the OTel resource.</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>Instrument ASP.NET Core HTTP requests (traces + metrics). Default: <c>true</c>.</summary>
    public bool EnableAspNetCoreInstrumentation { get; set; } = true;

    /// <summary>Instrument outgoing <see cref="System.Net.Http.HttpClient"/> calls. Default: <c>true</c>.</summary>
    public bool EnableHttpClientInstrumentation { get; set; } = true;

    /// <summary>
    /// Collect spans emitted by <see cref="WtmActivitySources"/> (Core, Analysis, ETL).
    /// Default: <c>true</c>.
    /// </summary>
    public bool EnableWtmActivitySources { get; set; } = true;

    /// <summary>
    /// OTLP exporter endpoint (e.g. <c>http://localhost:4317</c> for Jaeger / Grafana Tempo).
    /// When <c>null</c> no OTLP exporter is registered.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// Additional tracing configuration applied after all built-in sources are added.
    /// Use this to register extra <see cref="ActivitySource"/> names or custom exporters.
    /// </summary>
    public Action<TracerProviderBuilder>? CustomTracing { get; set; }

    /// <summary>
    /// Additional metrics configuration applied after all built-in instruments are added.
    /// </summary>
    public Action<MeterProviderBuilder>? CustomMetrics { get; set; }
}

/// <summary>
/// <see cref="IServiceCollection"/> extensions for WTM OpenTelemetry integration.
/// </summary>
public static class WtmOpenTelemetryExtension
{
    /// <summary>
    /// Registers OpenTelemetry tracing and metrics with WTM-specific defaults.
    /// </summary>
    /// <remarks>
    /// Minimal usage (OTLP to local Jaeger/Tempo):
    /// <code>
    /// services.AddWtmOpenTelemetry(o =>
    /// {
    ///     o.ServiceName    = "MyWtmApp";
    ///     o.OtlpEndpoint   = "http://localhost:4317";
    /// });
    /// </code>
    /// </remarks>
    public static IServiceCollection AddWtmOpenTelemetry(
        this IServiceCollection services,
        Action<WtmOpenTelemetryOptions>? configure = null)
    {
        var options = new WtmOpenTelemetryOptions();
        configure?.Invoke(options);

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: options.ServiceVersion);
            })
            .WithTracing(tracing =>
            {
                if (options.EnableAspNetCoreInstrumentation)
                    tracing.AddAspNetCoreInstrumentation();

                if (options.EnableHttpClientInstrumentation)
                    tracing.AddHttpClientInstrumentation();

                if (options.EnableWtmActivitySources)
                {
                    tracing.AddSource(WtmActivitySources.CoreName);
                    tracing.AddSource(WtmActivitySources.AnalysisName);
                    tracing.AddSource(WtmActivitySources.EtlName);
                }

                if (options.OtlpEndpoint != null)
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(options.OtlpEndpoint));

                options.CustomTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                if (options.EnableAspNetCoreInstrumentation)
                    metrics.AddAspNetCoreInstrumentation();

                if (options.EnableHttpClientInstrumentation)
                    metrics.AddHttpClientInstrumentation();

                if (options.OtlpEndpoint != null)
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(options.OtlpEndpoint));

                options.CustomMetrics?.Invoke(metrics);
            });

        return services;
    }
}
