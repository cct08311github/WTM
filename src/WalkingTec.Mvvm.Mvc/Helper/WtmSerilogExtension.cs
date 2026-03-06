using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace WalkingTec.Mvvm.Mvc
{
    public class WtmSerilogOptions
    {
        public bool EnableConsole { get; set; } = true;
        public bool EnableFile { get; set; } = true;
        public string FilePath { get; set; } = "logs/wtm-.log";
        public RollingInterval FileRollingInterval { get; set; } = RollingInterval.Day;
        public int FileRetainedCount { get; set; } = 30;
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
        public bool EnableRequestLogging { get; set; } = true;
        public Action<LoggerConfiguration>? CustomConfig { get; set; }
    }

    public static class WtmSerilogExtension
    {
        private static WtmSerilogOptions? _options;

        internal static WtmSerilogOptions? CurrentOptions => _options;

        public static IServiceCollection AddWtmSerilog(
            this IServiceCollection services,
            IConfiguration config,
            Action<WtmSerilogOptions>? configure = null)
        {
            var options = new WtmSerilogOptions();
            configure?.Invoke(options);
            _options = options;

            var levelMap = options.MinimumLevel switch
            {
                LogLevel.Trace => LogEventLevel.Verbose,
                LogLevel.Debug => LogEventLevel.Debug,
                LogLevel.Information => LogEventLevel.Information,
                LogLevel.Warning => LogEventLevel.Warning,
                LogLevel.Error => LogEventLevel.Error,
                LogLevel.Critical => LogEventLevel.Fatal,
                _ => LogEventLevel.Information
            };

            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Is(levelMap)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .Enrich.FromLogContext();

            if (options.EnableConsole)
            {
                loggerConfig.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
            }

            if (options.EnableFile)
            {
                loggerConfig.WriteTo.File(
                    formatter: new JsonFormatter(),
                    path: options.FilePath,
                    rollingInterval: options.FileRollingInterval,
                    retainedFileCountLimit: options.FileRetainedCount);
            }

            options.CustomConfig?.Invoke(loggerConfig);

            Log.Logger = loggerConfig.CreateLogger();

            services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: true));

            return services;
        }

        public static IApplicationBuilder UseWtmSerilog(this IApplicationBuilder app)
        {
            if (_options?.EnableRequestLogging == true)
            {
                app.UseSerilogRequestLogging(opts =>
                {
                    opts.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
                    {
                        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                        diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"].ToString());
                    };
                });
            }
            return app;
        }
    }
}
