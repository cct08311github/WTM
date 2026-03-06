using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using Serilog.Sinks.InMemory;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class SerilogExtensionTests
    {
        private static IConfiguration BuildEmptyConfig()
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection()
                .Build();
        }

        [TestCleanup]
        public void Cleanup()
        {
            Log.CloseAndFlush();
        }

        [TestMethod]
        public void AddWtmSerilog_registers_serilog_provider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(BuildEmptyConfig());

            using var sp = services.BuildServiceProvider();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("TestCategory");

            Assert.IsNotNull(logger);
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        [TestMethod]
        public void AddWtmSerilog_default_options_work()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(BuildEmptyConfig());

            // Verify Serilog is configured by checking that Log.Logger is not the silent default
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);

            // Verify the extension method returns services for fluent chaining
            using var sp = services.BuildServiceProvider();
            Assert.IsNotNull(sp.GetRequiredService<ILoggerFactory>());
        }

        [TestMethod]
        public void AddWtmSerilog_custom_options_applied()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            bool customConfigCalled = false;
            services.AddWtmSerilog(BuildEmptyConfig(), opt =>
            {
                opt.FilePath = "D:/custom/path-.log";
                opt.FileRetainedCount = 60;
                opt.MinimumLevel = LogLevel.Warning;
                opt.EnableConsole = false;
                opt.CustomConfig = cfg =>
                {
                    customConfigCalled = true;
                    cfg.WriteTo.InMemory();
                };
            });

            Assert.IsTrue(customConfigCalled, "CustomConfig action should have been invoked");
        }

        [TestMethod]
        public void AddWtmSerilog_custom_config_writes_to_inmemory_sink()
        {
            InMemorySink.Instance.Dispose(); // clear previous
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(BuildEmptyConfig(), opt =>
            {
                opt.EnableConsole = false;
                opt.EnableFile = false;
                opt.CustomConfig = cfg => cfg.WriteTo.InMemory();
            });

            using var sp = services.BuildServiceProvider();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");
            logger.LogInformation("Hello from Serilog test");

            Assert.IsTrue(InMemorySink.Instance.LogEvents.Any(),
                "InMemory sink should have captured the log event");
        }

        [TestMethod]
        public void AddWtmSerilog_coexists_with_other_providers()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.AddDebug(); // add another provider
            });
            services.AddWtmSerilog(BuildEmptyConfig());

            using var sp = services.BuildServiceProvider();
            var providers = sp.GetServices<ILoggerProvider>().ToList();

            // Should have at least Debug + Serilog
            Assert.IsTrue(providers.Count >= 2,
                $"Expected at least 2 providers, got {providers.Count}: {string.Join(", ", providers.Select(p => p.GetType().Name))}");
            Assert.IsTrue(providers.Any(p => p.GetType().Name.Contains("Serilog")),
                $"Serilog provider not found. Providers: {string.Join(", ", providers.Select(p => p.GetType().Name))}");
        }

        [TestMethod]
        public void not_calling_AddWtmSerilog_preserves_existing_behavior()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            // Deliberately NOT calling AddWtmSerilog

            using var sp = services.BuildServiceProvider();
            var providers = sp.GetServices<ILoggerProvider>().ToList();

            Assert.IsFalse(providers.Any(p => p.GetType().Name.Contains("Serilog")),
                $"Serilog should not be registered. Providers: {string.Join(", ", providers.Select(p => p.GetType().Name))}");
        }
    }
}
