# Structured Logging + ProblemDetails Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add opt-in Serilog structured logging and RFC 7807 ProblemDetails to WTM v8.2.0, coexisting with the existing WTMLogger.

**Architecture:** Two new extension method files (`WtmSerilogExtension.cs`, `WtmProblemDetailsExtension.cs`) in `src/WalkingTec.Mvvm.Mvc/Helper/`. Purely additive — no existing files modified except adding the Serilog NuGet package. ProblemDetails middleware intercepts only API routes (`/api/` prefix or `Accept: application/json`), leaving MVC pages to `FrameworkFilter`.

**Tech Stack:** Serilog.AspNetCore 8.0.3, ASP.NET Core 8 built-in ProblemDetails, MSTest, Moq

**Design doc:** `docs/plans/2026-03-06-structured-logging-problemdetails-design.md`

---

### Task 1: Add Serilog NuGet Package

**Files:**
- Modify: `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj` (add one `<PackageReference>` line)

**Step 1: Add the package reference**

In `WalkingTec.Mvvm.Mvc.csproj`, add inside the existing `<ItemGroup>` that has other `PackageReference` entries (after line 101):

```xml
<PackageReference Include="Serilog.AspNetCore" Version="8.0.3" />
```

**Step 2: Restore and verify build**

Run:
```bash
dotnet restore src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj
dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release --no-restore
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj
git commit -m "deps: add Serilog.AspNetCore 8.0.3 to Mvc project"
```

---

### Task 2: Create WtmSerilogOptions + AddWtmSerilog + UseWtmSerilog

**Files:**
- Create: `src/WalkingTec.Mvvm.Mvc/Helper/WtmSerilogExtension.cs`

**Step 1: Write the options class and extension methods**

Create `src/WalkingTec.Mvvm.Mvc/Helper/WtmSerilogExtension.cs`:

```csharp
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

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
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId();

            if (options.EnableConsole)
            {
                loggerConfig.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
            }

            if (options.EnableFile)
            {
                loggerConfig.WriteTo.File(
                    new Serilog.Formatting.Compact.CompactJsonFormatter(),
                    options.FilePath,
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
```

**Important notes for the implementer:**
- `Serilog.Formatting.Compact` is included in `Serilog.AspNetCore` — no extra package needed.
- `Enrich.WithMachineName()` requires `Serilog.Enrichers.Environment` (included in Serilog.AspNetCore).
- `Enrich.WithThreadId()` requires `Serilog.Enrichers.Thread` — check if bundled, if not add as separate package.
- If `Serilog.Enrichers.Environment` or `Serilog.Enrichers.Thread` are NOT bundled in `Serilog.AspNetCore`, add them:
  ```xml
  <PackageReference Include="Serilog.Enrichers.Environment" Version="2.3.0" />
  <PackageReference Include="Serilog.Enrichers.Thread" Version="3.1.0" />
  ```

**Step 2: Verify build**

Run:
```bash
dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release
```
Expected: Build succeeded. If enricher packages are missing, add them to csproj and retry.

**Step 3: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/Helper/WtmSerilogExtension.cs
git add src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj  # if enricher packages added
git commit -m "feat(logging): add WtmSerilogOptions and AddWtmSerilog/UseWtmSerilog extensions"
```

---

### Task 3: Write Serilog Extension Tests

**Files:**
- Create: `test/WalkingTec.Mvvm.Core.Test/SerilogExtensionTests.cs`
- Modify: `test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj` (add Serilog test package)

**Step 1: Add test dependency**

Add to `test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj` inside `<ItemGroup>`:

```xml
<PackageReference Include="Serilog.Sinks.InMemory" Version="0.11.0" />
```

**Step 2: Write the test file**

Create `test/WalkingTec.Mvvm.Core.Test/SerilogExtensionTests.cs`:

```csharp
using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using Serilog.Events;
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

        [TestMethod]
        public void AddWtmSerilog_registers_serilog_provider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(BuildEmptyConfig());

            using var sp = services.BuildServiceProvider();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("TestCategory");

            // Serilog provider is registered — logger should not be null
            Assert.IsNotNull(logger);
            // Verify Serilog's static Log.Logger was configured (not the default silent logger)
            Assert.AreNotEqual(Serilog.Core.Logger.None, Log.Logger);
        }

        [TestMethod]
        public void AddWtmSerilog_default_options_are_sane()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(BuildEmptyConfig());

            var options = WtmSerilogExtension.CurrentOptions;
            Assert.IsNotNull(options);
            Assert.IsTrue(options.EnableConsole);
            Assert.IsTrue(options.EnableFile);
            Assert.AreEqual("logs/wtm-.log", options.FilePath);
            Assert.AreEqual(Serilog.RollingInterval.Day, options.FileRollingInterval);
            Assert.AreEqual(30, options.FileRetainedCount);
            Assert.AreEqual(LogLevel.Information, options.MinimumLevel);
            Assert.IsTrue(options.EnableRequestLogging);
            Assert.IsNull(options.CustomConfig);
        }

        [TestMethod]
        public void AddWtmSerilog_custom_options_applied()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(BuildEmptyConfig(), opt =>
            {
                opt.FilePath = "D:/custom/path-.log";
                opt.FileRetainedCount = 60;
                opt.MinimumLevel = LogLevel.Warning;
                opt.EnableConsole = false;
            });

            var options = WtmSerilogExtension.CurrentOptions;
            Assert.IsNotNull(options);
            Assert.AreEqual("D:/custom/path-.log", options.FilePath);
            Assert.AreEqual(60, options.FileRetainedCount);
            Assert.AreEqual(LogLevel.Warning, options.MinimumLevel);
            Assert.IsFalse(options.EnableConsole);
        }

        [TestMethod]
        public void AddWtmSerilog_coexists_with_WTMLogger()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.AddWTMLogger(); // existing WTMLogger
            });
            services.AddWtmSerilog(BuildEmptyConfig());

            using var sp = services.BuildServiceProvider();
            var providers = sp.GetServices<ILoggerProvider>();
            // Both WTMLoggerProvider and SerilogLoggerProvider should be registered
            var providerTypes = providers.Select(p => p.GetType().Name).ToList();
            Assert.IsTrue(providerTypes.Any(t => t.Contains("Serilog")),
                $"Serilog provider not found. Providers: {string.Join(", ", providerTypes)}");
        }

        [TestMethod]
        public void AddWtmSerilog_custom_config_action_invoked()
        {
            bool customConfigCalled = false;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmSerilog(BuildEmptyConfig(), opt =>
            {
                opt.CustomConfig = cfg =>
                {
                    customConfigCalled = true;
                    cfg.WriteTo.InMemory();
                };
            });

            Assert.IsTrue(customConfigCalled);
        }

        [TestMethod]
        public void not_calling_AddWtmSerilog_preserves_existing_behavior()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            // Deliberately NOT calling AddWtmSerilog

            using var sp = services.BuildServiceProvider();
            var providers = sp.GetServices<ILoggerProvider>();
            var providerTypes = providers.Select(p => p.GetType().Name).ToList();

            Assert.IsFalse(providerTypes.Any(t => t.Contains("Serilog")),
                $"Serilog should not be registered. Providers: {string.Join(", ", providerTypes)}");
        }
    }
}
```

**Step 3: Run tests**

```bash
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj --filter "FullyQualifiedName~SerilogExtensionTests" -c Release -v normal
```
Expected: 6 tests passed.

**Note:** The `coexists_with_WTMLogger` test may need adjustments if `AddWTMLogger()` requires DI services (like `IOptionsMonitor<LoggerFilterOptions>`, `IServiceProvider`, `IHttpContextAccessor`). If it fails due to missing services, register them:
```csharp
services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
services.AddOptions();
```
or simplify the test to only check Serilog provider count.

**Step 4: Commit**

```bash
git add test/WalkingTec.Mvvm.Core.Test/SerilogExtensionTests.cs
git add test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj
git commit -m "test(logging): add SerilogExtension unit tests"
```

---

### Task 4: Create WtmProblemDetailsExtension

**Files:**
- Create: `src/WalkingTec.Mvvm.Mvc/Helper/WtmProblemDetailsExtension.cs`

**Step 1: Write the middleware and extension methods**

Create `src/WalkingTec.Mvvm.Mvc/Helper/WtmProblemDetailsExtension.cs`:

```csharp
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    public static class WtmProblemDetailsExtension
    {
        public static IApplicationBuilder UseWtmProblemDetails(this IApplicationBuilder app)
        {
            app.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    if (ShouldHandleAsApi(context.Request))
                    {
                        await WriteExceptionProblemDetails(context, ex);
                    }
                    else
                    {
                        throw; // re-throw for downstream handlers (FrameworkFilter, UseExceptionHandler)
                    }
                }

                // Handle non-exception error status codes (e.g., 404, 401) for API routes
                if (!context.Response.HasStarted
                    && context.Response.StatusCode >= 400
                    && context.Response.ContentLength == null
                    && string.IsNullOrEmpty(context.Response.ContentType)
                    && ShouldHandleAsApi(context.Request))
                {
                    await WriteStatusProblemDetails(context);
                }
            });

            return app;
        }

        internal static bool ShouldHandleAsApi(HttpRequest request)
        {
            // Rule 1: path starts with /api/
            if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                return true;

            // Rule 2: Accept header contains application/json
            var accept = request.Headers.Accept.ToString();
            if (!string.IsNullOrEmpty(accept)
                && accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static async Task WriteExceptionProblemDetails(HttpContext context, Exception ex)
        {
            var logger = context.RequestServices.GetService<ILogger<ProblemDetails>>();
            logger?.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);

            var isDebug = false;
            try
            {
                var wtm = context.RequestServices.GetService<WTMContext>();
                isDebug = wtm?.ConfigInfo?.IsQuickDebug ?? false;
            }
            catch { }

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                Title = "Internal Server Error",
                Status = 500,
                Detail = isDebug ? ex.ToString() : "An unexpected error occurred."
            };
            problem.Extensions["traceId"] = context.TraceIdentifier;

            if (isDebug)
            {
                problem.Extensions["exception"] = ex.ToString();
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(problem, jsonOptions));
        }

        private static async Task WriteStatusProblemDetails(HttpContext context)
        {
            var statusCode = context.Response.StatusCode;
            var title = statusCode switch
            {
                400 => "Bad Request",
                401 => "Unauthorized",
                403 => "Forbidden",
                404 => "Not Found",
                405 => "Method Not Allowed",
                409 => "Conflict",
                422 => "Unprocessable Entity",
                429 => "Too Many Requests",
                _ => "Error"
            };

            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Type = $"https://tools.ietf.org/html/rfc7231#section-6.5.{statusCode - 399}",
                Title = title,
                Status = statusCode,
                Detail = $"{title}."
            };
            problem.Extensions["traceId"] = context.TraceIdentifier;

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(problem, jsonOptions));
        }
    }
}
```

**Step 2: Verify build**

```bash
dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release
```
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/Helper/WtmProblemDetailsExtension.cs
git commit -m "feat(api): add UseWtmProblemDetails middleware for RFC 7807 error responses"
```

---

### Task 5: Write ProblemDetails Tests

**Files:**
- Create: `test/WalkingTec.Mvvm.Core.Test/ProblemDetailsTests.cs`
- Modify: `test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj` (add TestServer package)

**Step 1: Add test dependencies**

Add to `test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj`:

```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.22" />
```

**Step 2: Write the test file**

Create `test/WalkingTec.Mvvm.Core.Test/ProblemDetailsTests.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class ProblemDetailsTests
    {
        private static IHost BuildTestHost(bool isQuickDebug = false)
        {
            return new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        // Register a minimal Configs so WTMContext can resolve IsQuickDebug
                        var configs = new ConfigOptions.Configs { IsQuickDebug = isQuickDebug };
                        services.AddSingleton(configs);
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseWtmProblemDetails();

                        app.UseRouting();

                        app.Run(async context =>
                        {
                            var path = context.Request.Path.Value ?? "";

                            if (path.StartsWith("/api/throw"))
                                throw new InvalidOperationException("Test exception message");

                            if (path.StartsWith("/api/ok"))
                            {
                                await context.Response.WriteAsync("OK");
                                return;
                            }

                            if (path.StartsWith("/mvc/throw"))
                                throw new InvalidOperationException("MVC exception");

                            await context.Response.WriteAsync("fallback");
                        });
                    });
                })
                .Build();
        }

        [TestMethod]
        public async Task api_route_exception_returns_problemdetails_json()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");

            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual(500, doc.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Internal Server Error", doc.RootElement.GetProperty("title").GetString());
        }

        [TestMethod]
        public async Task api_route_500_hides_exception_in_production()
        {
            using var host = BuildTestHost(isQuickDebug: false);
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.AreEqual("An unexpected error occurred.", doc.RootElement.GetProperty("detail").GetString());
            Assert.IsFalse(doc.RootElement.TryGetProperty("exception", out _),
                "Exception details should not be exposed in production");
        }

        [TestMethod]
        public async Task api_route_500_shows_exception_in_debug()
        {
            using var host = BuildTestHost(isQuickDebug: true);
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var detail = doc.RootElement.GetProperty("detail").GetString();
            Assert.IsTrue(detail?.Contains("Test exception message"),
                $"Detail should contain exception message in debug mode. Got: {detail}");
            Assert.IsTrue(doc.RootElement.TryGetProperty("exception", out _),
                "Exception field should be present in debug mode");
        }

        [TestMethod]
        public async Task mvc_route_exception_falls_through()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            // MVC route should NOT be caught by ProblemDetails — it re-throws
            // TestServer wraps the unhandled exception in a 500 but NOT as ProblemDetails JSON
            try
            {
                var response = await client.GetAsync("/mvc/throw");
                // If no exception, check that it's not problem+json
                if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    Assert.AreNotEqual("application/problem+json",
                        response.Content.Headers.ContentType?.MediaType,
                        "MVC routes should not get ProblemDetails treatment");
                }
            }
            catch (Exception)
            {
                // Re-thrown exception is expected behavior — middleware did not handle it
            }
        }

        [TestMethod]
        public async Task problemdetails_includes_traceId()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/throw");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.IsTrue(doc.RootElement.TryGetProperty("traceId", out var traceId),
                "ProblemDetails must include traceId");
            Assert.IsFalse(string.IsNullOrEmpty(traceId.GetString()),
                "traceId must not be empty");
        }

        [TestMethod]
        public async Task accept_json_header_triggers_problemdetails()
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            var client = host.GetTestClient();

            // Non-/api/ path, but Accept: application/json
            var request = new HttpRequestMessage(HttpMethod.Get, "/mvc/throw");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task api_route_400_includes_validation_errors()
        {
            // For 400 validation errors, the existing MvcOptionExtension.UseWtmApiOptions
            // already handles ModelState → JSON via InvalidModelStateResponseFactory.
            // UseWtmProblemDetails handles bare 400 status codes without body.
            // This test verifies that a bare 400 gets ProblemDetails treatment.

            using var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.Configure(app =>
                    {
                        app.UseWtmProblemDetails();
                        app.Run(async context =>
                        {
                            if (context.Request.Path.StartsWithSegments("/api/validate"))
                            {
                                context.Response.StatusCode = 400;
                                // Don't write body — let ProblemDetails middleware handle it
                                return;
                            }
                            await context.Response.WriteAsync("ok");
                        });
                    });
                })
                .Build();

            await host.StartAsync();
            var client = host.GetTestClient();

            var response = await client.GetAsync("/api/validate");
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.AreEqual(400, doc.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Bad Request", doc.RootElement.GetProperty("title").GetString());
        }
    }
}
```

**Step 3: Run tests**

```bash
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj --filter "FullyQualifiedName~ProblemDetailsTests" -c Release -v normal
```
Expected: 7 tests passed.

**Note:** The `BuildTestHost` helper registers `Configs` directly. If `WtmProblemDetailsExtension` can't resolve `WTMContext` from DI (it uses `GetService<WTMContext>()` which may return null), it falls back to `isDebug = false`. This is the correct production-safe default. If the `api_route_500_shows_exception_in_debug` test fails because `WTMContext` isn't resolvable, adjust the middleware to also accept `Configs` directly:
```csharp
var configs = context.RequestServices.GetService<ConfigOptions.Configs>();
isDebug = configs?.IsQuickDebug ?? false;
```

**Step 4: Commit**

```bash
git add test/WalkingTec.Mvvm.Core.Test/ProblemDetailsTests.cs
git add test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj
git commit -m "test(api): add ProblemDetails middleware unit tests"
```

---

### Task 6: Run Full Test Suite

**Files:** None (verification only)

**Step 1: Run all .NET tests to ensure no regressions**

```bash
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal
```
Expected: All tests pass (existing + 13 new tests).

**Step 2: Run JS tests**

```bash
cd test/WalkingTec.Mvvm.Js.Tests && npm test
```
Expected: All 123 tests pass.

**Step 3: Fix any failures**

If existing tests break, investigate — the change should be purely additive. Likely causes:
- Serilog's `Log.Logger` static leaking between test classes: add `Log.CloseAndFlush()` in `[TestCleanup]`
- TestServer port conflicts: ensure `BuildTestHost()` uses unique in-memory servers

---

### Task 7: Write Developer Documentation

**Files:**
- Create: `docs/structured-logging.md`

**Step 1: Write the usage manual**

Create `docs/structured-logging.md` with the following structure. The document must contain complete, copy-paste-ready code examples.

```markdown
# WTM Structured Logging & ProblemDetails Guide

> Added in v8.2.0. Fully opt-in — your existing app works unchanged without these features.

## Quick Start

Add two lines to your `Startup.cs`:

### ConfigureServices

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Add Serilog structured logging (add this line)
    services.AddWtmSerilog(ConfigRoot);

    // ... your existing AddWtm* calls remain unchanged ...
    services.AddDistributedMemoryCache();
    services.AddWtmSession(3600, ConfigRoot);
    services.AddWtmCrossDomain(ConfigRoot);
    services.AddWtmAuthentication(ConfigRoot);
    services.AddWtmHttpClient(ConfigRoot);
    services.AddWtmSwagger(true);
    services.AddWtmMultiLanguages(ConfigRoot);
    services.AddMvc(options => { options.UseWtmMvcOptions(); })
        .AddJsonOptions(options => { options.UseWtmJsonOptions(); })
        .ConfigureApiBehaviorOptions(options => { options.UseWtmApiOptions(); })
        .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
        .AddWtmDataAnnotationsLocalization(typeof(Program));
    services.AddWtmContext(ConfigRoot, options => { /* ... */ });
}
```

### Configure

```csharp
public void Configure(IApplicationBuilder app, IOptionsMonitor<Configs> configs)
{
    app.UseWtmSerilog();             // Add this (request logging)
    app.UseWtmProblemDetails();      // Add this (API error formatting)
    app.UseExceptionHandler(configs.CurrentValue.ErrorHandler); // Keep existing
    app.UseStaticFiles();
    // ... rest unchanged ...
}
```

### Verify

Start your app. You should see Serilog output in the console:
```
[14:23:01 INF] Now listening on: http://localhost:5000
[14:23:05 INF] HTTP GET /api/Student responded 200 in 45.2 ms
```

And a `logs/` directory with JSON log files.

## Configuration Options

| Property | Default | Description |
|----------|---------|-------------|
| `EnableConsole` | `true` | Write logs to console (recommended for dev) |
| `EnableFile` | `true` | Write logs to rolling JSON files |
| `FilePath` | `"logs/wtm-.log"` | File path template (date appended automatically) |
| `FileRollingInterval` | `Day` | New file per day |
| `FileRetainedCount` | `30` | Keep last 30 log files |
| `MinimumLevel` | `Information` | Minimum log level (Trace/Debug/Information/Warning/Error/Critical) |
| `EnableRequestLogging` | `true` | Log every HTTP request with method, path, status, duration |
| `CustomConfig` | `null` | Advanced: configure Serilog directly (add custom sinks) |

### Custom Configuration Example

```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.FilePath = @"D:\logs\myapp-.log";  // IIS: use absolute path
    options.FileRetainedCount = 60;             // Keep 60 days
    options.MinimumLevel = LogLevel.Warning;    // Only warnings and above
    options.EnableConsole = false;               // No console in production
});
```

## Production Recommendations (IIS)

### File Path

Use an absolute path outside your app directory:

```csharp
options.FilePath = @"D:\logs\wtm-.log";
```

### Disk Space Estimation

| Users | Daily Requests | Est. Log Size/Day |
|-------|---------------|-------------------|
| 50    | ~5,000        | ~5 MB             |
| 100   | ~10,000       | ~10 MB            |
| 500   | ~50,000       | ~50 MB            |

With `FileRetainedCount = 30`, worst case: ~300 MB for 100 users.

### IIS Application Pool

Ensure the app pool identity has write permission to the log directory.

## Querying Logs

### Linux / macOS (jq)

```bash
# All errors today
cat logs/wtm-20260306.log | jq 'select(.Level=="Error")'

# Find by traceId (from API error response)
cat logs/wtm-20260306.log | jq 'select(.Properties.RequestId=="0HN8Q1ABC456")'

# Slow requests (> 1 second)
cat logs/wtm-20260306.log | jq 'select(.Properties.Elapsed > 1000)'
```

### Windows (PowerShell)

```powershell
# All errors today
Get-Content logs\wtm-20260306.log | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.Level -eq "Error" }

# Find by traceId
Get-Content logs\wtm-20260306.log | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.Properties.RequestId -eq "0HN8Q1ABC456" }
```

### TraceId Correlation Flow

1. Frontend receives a 500 error response:
   ```json
   { "status": 500, "traceId": "0HN8Q1ABC456", "detail": "An unexpected error occurred." }
   ```

2. Backend developer searches logs:
   ```bash
   cat logs/wtm-20260306.log | jq 'select(.Properties.RequestId=="0HN8Q1ABC456")'
   ```

3. Full context found:
   ```json
   {
     "Timestamp": "2026-03-06T14:23:01.234+08:00",
     "Level": "Error",
     "Properties": { "RequestId": "0HN8Q1ABC456", "RequestPath": "/api/Student/Edit" },
     "Exception": "System.NullReferenceException: Object reference not set..."
   }
   ```

## ProblemDetails API Error Format

API routes (`/api/*` or `Accept: application/json`) now return RFC 7807 JSON on errors:

### 400 Bad Request

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Bad Request.",
  "traceId": "0HN8Q1ABC123"
}
```

### 401 Unauthorized

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.2",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Unauthorized.",
  "traceId": "0HN8Q1ABC789"
}
```

### 500 Internal Server Error

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "Internal Server Error",
  "status": 500,
  "detail": "An unexpected error occurred.",
  "traceId": "0HN8Q1ABC456"
}
```

> In debug mode (`IsQuickDebug = true`), the `detail` and `exception` fields include the full stack trace.

### Frontend Error Handling Example (JavaScript)

```javascript
async function apiCall(url, options = {}) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const contentType = response.headers.get('content-type');
    if (contentType && contentType.includes('application/problem+json')) {
      const problem = await response.json();
      console.error(`[${problem.status}] ${problem.title}: ${problem.detail} (traceId: ${problem.traceId})`);
      throw new Error(problem.detail);
    }
    throw new Error(`HTTP ${response.status}`);
  }
  return response.json();
}
```

## Advanced: Custom Sinks

### Seq

```csharp
// Add NuGet: Serilog.Sinks.Seq
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.CustomConfig = cfg => cfg.WriteTo.Seq("http://localhost:5341");
});
```

### Elasticsearch

```csharp
// Add NuGet: Serilog.Sinks.Elasticsearch
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.CustomConfig = cfg => cfg.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri("http://localhost:9200"))
    {
        IndexFormat = "wtm-logs-{0:yyyy.MM.dd}"
    });
});
```

## FAQ

**Q: Does enabling Serilog affect the existing ActionLog database logging?**
A: No. `WTMLogger` and Serilog run side-by-side. Both receive log events. ActionLog continues writing to the database exactly as before.

**Q: How do I disable WTMLogger and only use Serilog?**
A: In `appsettings.json`, set the WTM log level to None:
```json
{
  "Logging": {
    "LogLevel": {
      "WTM": "None"
    }
  }
}
```

**Q: My log files are too large. What can I do?**
A: Raise the minimum level and reduce retention:
```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.MinimumLevel = LogLevel.Warning;  // Only warnings and errors
    options.FileRetainedCount = 7;            // Keep only 7 days
});
```

**Q: Does ProblemDetails affect my MVC (Razor) pages?**
A: No. ProblemDetails only activates for API routes (`/api/*`) or requests with `Accept: application/json`. MVC pages continue to show the existing error page.

**Q: Can I use appsettings.json instead of code to configure Serilog?**
A: The `AddWtmSerilog()` extension supports code-based configuration for simplicity. For full `appsettings.json`-driven configuration, use Serilog's native `ReadFrom.Configuration(config)` via `CustomConfig`:
```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.EnableConsole = false;
    options.EnableFile = false;
    options.CustomConfig = cfg => cfg.ReadFrom.Configuration(ConfigRoot);
});
```
Then add to `appsettings.json`:
```json
{
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "logs/wtm-.log", "rollingInterval": "Day" } }
    ]
  }
}
```
```

**Step 2: Commit**

```bash
git add docs/structured-logging.md
git commit -m "docs: add structured logging and ProblemDetails usage manual"
```

---

### Task 8: Final Build, Full Test, and Version Bump

**Files:**
- Modify: `version.props` (bump version to 8.2.0)

**Step 1: Run full solution build**

```bash
dotnet build WalkingTec.Mvvm.sln -c Release
```
Expected: Build succeeded across all projects.

**Step 2: Run full test suite**

```bash
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal
```
Expected: All tests pass (existing + 13 new).

**Step 3: Bump version**

In `version.props`, change `<VersionPrefix>` from `8.1.16` to `8.2.0`.

**Step 4: Update CHANGELOG.md**

Add entry at top:

```markdown
## v8.2.0 — Structured Logging & ProblemDetails

### New Features
- **Structured Logging**: Opt-in Serilog integration via `AddWtmSerilog()` / `UseWtmSerilog()`. Supports Console + JSON file sinks with daily rolling, 30-day retention, request logging, and custom sink extensibility. Coexists with existing WTMLogger/ActionLog.
- **ProblemDetails**: Opt-in RFC 7807 error responses for API routes via `UseWtmProblemDetails()`. Returns structured JSON errors with traceId correlation. MVC pages unaffected.

### Documentation
- New: `docs/structured-logging.md` — complete usage manual with configuration, production tips, log querying, and frontend integration examples.

### Dependencies
- Added: `Serilog.AspNetCore` 8.0.3
```

**Step 5: Commit**

```bash
git add version.props CHANGELOG.md
git commit -m "release: bump version to 8.2.0 — structured logging + ProblemDetails"
```

---

## Task Summary

| Task | Description | Files | Tests |
|------|-------------|-------|-------|
| 1 | Add Serilog NuGet package | 1 modified | 0 |
| 2 | Create WtmSerilogExtension | 1 created | 0 |
| 3 | Write Serilog tests | 1 created, 1 modified | 6 |
| 4 | Create WtmProblemDetailsExtension | 1 created | 0 |
| 5 | Write ProblemDetails tests | 1 created, 1 modified | 7 |
| 6 | Full test suite verification | 0 | 0 (verification) |
| 7 | Write developer documentation | 1 created | 0 |
| 8 | Version bump + CHANGELOG | 2 modified | 0 |

**Total: 5 new files, 4 modified files, 13 new tests, 0 existing files broken.**
