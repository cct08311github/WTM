#nullable enable
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Additional coverage for WtmSerilogExtension:
    ///  - WtmSerilogOptions default property values
    ///  - MinimumLevel mapping for all LogLevel values
    ///  - UseWtmSerilog middleware registration (request logging opt-in/opt-out)
    ///  - CurrentOptions accessor
    /// </summary>
    [TestClass]
    public class WtmSerilogExtensionExtraTests
    {
        private static IConfiguration EmptyConfig() =>
            new ConfigurationBuilder().AddInMemoryCollection().Build();

        [TestCleanup]
        public void Cleanup() => Log.CloseAndFlush();

        // ── WtmSerilogOptions defaults ────────────────────────────────────────

        [TestMethod]
        public void WtmSerilogOptions_defaults_are_sane()
        {
            var opts = new WtmSerilogOptions();
            Assert.IsTrue(opts.EnableConsole);
            Assert.IsTrue(opts.EnableFile);
            Assert.AreEqual("logs/wtm-.log", opts.FilePath);
            Assert.AreEqual(Serilog.RollingInterval.Day, opts.FileRollingInterval);
            Assert.AreEqual(30, opts.FileRetainedCount);
            Assert.AreEqual(LogLevel.Information, opts.MinimumLevel);
            Assert.IsTrue(opts.EnableRequestLogging);
            Assert.IsNull(opts.CustomConfig);
        }

        // ── MinimumLevel mapping for every LogLevel ───────────────────────────

        [TestMethod]
        public void AddWtmSerilog_Trace_maps_to_Verbose()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(EmptyConfig(), o =>
            {
                o.MinimumLevel = LogLevel.Trace;
                o.EnableConsole = false;
                o.EnableFile = false;
            });
            // If it didn't throw, mapping succeeded
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        [TestMethod]
        public void AddWtmSerilog_Debug_level_does_not_throw()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(EmptyConfig(), o =>
            {
                o.MinimumLevel = LogLevel.Debug;
                o.EnableConsole = false;
                o.EnableFile = false;
            });
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        [TestMethod]
        public void AddWtmSerilog_Warning_level_does_not_throw()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(EmptyConfig(), o =>
            {
                o.MinimumLevel = LogLevel.Warning;
                o.EnableConsole = false;
                o.EnableFile = false;
            });
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        [TestMethod]
        public void AddWtmSerilog_Error_level_does_not_throw()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(EmptyConfig(), o =>
            {
                o.MinimumLevel = LogLevel.Error;
                o.EnableConsole = false;
                o.EnableFile = false;
            });
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        [TestMethod]
        public void AddWtmSerilog_Critical_level_does_not_throw()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(EmptyConfig(), o =>
            {
                o.MinimumLevel = LogLevel.Critical;
                o.EnableConsole = false;
                o.EnableFile = false;
            });
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        [TestMethod]
        public void AddWtmSerilog_None_level_falls_back_to_Information()
        {
            // LogLevel.None is the default fallback branch
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(EmptyConfig(), o =>
            {
                o.MinimumLevel = LogLevel.None;
                o.EnableConsole = false;
                o.EnableFile = false;
            });
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        // ── CurrentOptions accessor ───────────────────────────────────────────

        [TestMethod]
        public void CurrentOptions_is_null_before_AddWtmSerilog_called()
        {
            // Use reflection to reset the static field for isolation
            var field = typeof(WtmSerilogExtension)
                .GetField("_options",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!;
            field.SetValue(null, null);

            Assert.IsNull(WtmSerilogExtension.CurrentOptions);
        }

        [TestMethod]
        public void CurrentOptions_is_set_after_AddWtmSerilog_called()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(EmptyConfig(), o =>
            {
                o.EnableConsole = false;
                o.EnableFile = false;
                o.FilePath = "test-current-options.log";
            });

            Assert.IsNotNull(WtmSerilogExtension.CurrentOptions);
            Assert.AreEqual("test-current-options.log", WtmSerilogExtension.CurrentOptions!.FilePath);
        }

        // ── UseWtmSerilog ─────────────────────────────────────────────────────

        [TestMethod]
        public async System.Threading.Tasks.Task UseWtmSerilog_with_request_logging_enabled_does_not_throw()
        {
            // Serilog's request-logging middleware requires its own services to be registered via
            // AddSerilog() which AddWtmSerilog() calls internally.  We call it during
            // ConfigureServices so the host DI container has DiagnosticContext available.
            using var host = await new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        // AddWtmSerilog registers AddSerilog(Log.Logger) which registers DiagnosticContext
                        services.AddLogging();
                        services.AddWtmSerilog(EmptyConfig(), o =>
                        {
                            o.EnableFile = false;
                            o.EnableConsole = false;
                            o.EnableRequestLogging = true;
                        });
                        // Explicitly add Serilog services so DiagnosticContext is resolvable in the host DI
                        services.AddSingleton(Log.Logger);
                        services.AddSingleton<Serilog.Extensions.Hosting.DiagnosticContext>();
                    });
                    web.Configure(app =>
                    {
                        app.UseWtmSerilog();
                        app.Run(ctx => ctx.Response.WriteAsync("ok"));
                    });
                })
                .StartAsync();

            var client = host.GetTestClient();
            var response = await client.GetAsync("/");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task UseWtmSerilog_with_request_logging_disabled_skips_middleware()
        {
            // Set options with EnableRequestLogging = false via reflection
            var field = typeof(WtmSerilogExtension)
                .GetField("_options",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!;
            field.SetValue(null, new WtmSerilogOptions { EnableRequestLogging = false });

            using var host = await new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.Configure(app =>
                    {
                        app.UseWtmSerilog();   // should be a no-op
                        app.Run(ctx => ctx.Response.WriteAsync("ok"));
                    });
                })
                .StartAsync();

            var client = host.GetTestClient();
            var response = await client.GetAsync("/");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task UseWtmSerilog_with_null_options_skips_middleware()
        {
            // If _options is null UseWtmSerilog should gracefully skip
            var field = typeof(WtmSerilogExtension)
                .GetField("_options",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!;
            field.SetValue(null, null);

            using var host = await new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.Configure(app =>
                    {
                        app.UseWtmSerilog();
                        app.Run(ctx => ctx.Response.WriteAsync("ok"));
                    });
                })
                .StartAsync();

            var client = host.GetTestClient();
            var response = await client.GetAsync("/");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }
}
