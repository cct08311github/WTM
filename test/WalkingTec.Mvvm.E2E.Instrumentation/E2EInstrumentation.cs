using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WalkingTec.Mvvm.E2E.Instrumentation
{
    /// <summary>
    /// Test-only instrumentation for the route coverage report of the E2E tests.
    /// Both outputs are opt-in and driven by configuration (environment variables of the same name work as well):
    ///   E2E:RouteInventoryPath  write the list of every controller action of this site as JSON once the host has started
    ///   E2E:RouteHitLogPath     append one JSON line for every controller action that was executed
    /// Nothing is written when the keys are absent, so a manual run of the host has no side effects.
    /// </summary>
    public static class E2EInstrumentationExtensions
    {
        public const string InventoryPathKey = "E2E:RouteInventoryPath";
        public const string HitLogPathKey = "E2E:RouteHitLogPath";

        public static IServiceCollection AddE2EInstrumentation(this IServiceCollection services, IConfiguration config)
        {
            var hitLog = config[HitLogPathKey];
            if (string.IsNullOrWhiteSpace(hitLog) == false)
            {
                services.AddSingleton(new RouteHitLog(hitLog));
                services.Configure<MvcOptions>(options =>
                {
                    // runs after the framework's filters, so an action that was refused (not logged in, no privilege) is not recorded as hit
                    options.Filters.Add<RouteHitFilter>(order: int.MaxValue);
                });
            }
            return services;
        }

        public static IApplicationBuilder UseE2EInstrumentation(this IApplicationBuilder app)
        {
            var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
            var inventoryPath = config[InventoryPathKey];
            if (string.IsNullOrWhiteSpace(inventoryPath) == false)
            {
                var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
                lifetime.ApplicationStarted.Register(() => RouteInventory.Write(app.ApplicationServices, inventoryPath));
            }
            return app;
        }
    }

    public sealed class RouteHitLog
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        private readonly string _path;
        private readonly object _lock = new object();

        public RouteHitLog(string path)
        {
            _path = path;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir) == false)
            {
                Directory.CreateDirectory(dir);
            }
        }

        public void Append(RouteHit hit)
        {
            var line = JsonSerializer.Serialize(hit, JsonOptions) + Environment.NewLine;
            lock (_lock)
            {
                File.AppendAllText(_path, line);
            }
        }
    }

    public sealed class RouteHit
    {
        public string Key { get; set; }
        public string HttpMethod { get; set; }
        public string Path { get; set; }
        public DateTime TimeUtc { get; set; }
    }

    public sealed class RouteHitFilter : IActionFilter
    {
        private readonly RouteHitLog _log;

        public RouteHitFilter(RouteHitLog log)
        {
            _log = log;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ActionDescriptor is ControllerActionDescriptor cad)
            {
                _log.Append(new RouteHit
                {
                    Key = RouteInventory.KeyOf(cad),
                    HttpMethod = context.HttpContext.Request.Method,
                    Path = context.HttpContext.Request.Path.Value,
                    TimeUtc = DateTime.UtcNow
                });
            }
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }

    public sealed class RouteInventoryItem
    {
        public string Key { get; set; }
        public string Area { get; set; }
        public string Controller { get; set; }
        public string ControllerType { get; set; }
        public string Action { get; set; }
        public List<string> HttpMethods { get; set; }
        public string Template { get; set; }
        public List<string> Attributes { get; set; }
    }

    public static class RouteInventory
    {
        // attributes that explain why an action may not be reachable in a normal run; the exclusion file can match on them
        private static readonly string[] MarkerAttributes = { "DebugOnly", "Public", "AllRights", "AllowAnonymous", "MainTenantOnly", "ApiController" };

        public static string KeyOf(ControllerActionDescriptor cad)
        {
            cad.RouteValues.TryGetValue("area", out var area);
            return string.Join("/", new[] { area, cad.ControllerName, cad.ActionName }.Where(x => string.IsNullOrEmpty(x) == false)).ToLowerInvariant();
        }

        public static List<RouteInventoryItem> Collect(IServiceProvider services)
        {
            var provider = services.GetRequiredService<IActionDescriptorCollectionProvider>();
            var items = new Dictionary<string, RouteInventoryItem>();
            foreach (var cad in provider.ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
            {
                var key = KeyOf(cad);
                cad.RouteValues.TryGetValue("area", out var area);
                var methods = cad.ActionConstraints?.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
                    .SelectMany(x => x.HttpMethods).Distinct().OrderBy(x => x).ToList() ?? new List<string>();
                var attrs = new List<string>();
                foreach (var a in cad.MethodInfo.GetCustomAttributes(true).Concat(cad.ControllerTypeInfo.GetCustomAttributes(true)))
                {
                    var name = a.GetType().Name;
                    if (name.EndsWith("Attribute"))
                    {
                        name = name.Substring(0, name.Length - "Attribute".Length);
                    }
                    if (MarkerAttributes.Contains(name))
                    {
                        attrs.Add(name);
                    }
                }
                if (items.TryGetValue(key, out var existing))
                {
                    // overloads (for example Create GET and Create POST) share one key; merge them
                    existing.HttpMethods = existing.HttpMethods.Concat(methods).Distinct().OrderBy(x => x).ToList();
                    existing.Attributes = existing.Attributes.Concat(attrs).Distinct().OrderBy(x => x).ToList();
                }
                else
                {
                    items[key] = new RouteInventoryItem
                    {
                        Key = key,
                        Area = area,
                        Controller = cad.ControllerName,
                        ControllerType = cad.ControllerTypeInfo.FullName,
                        Action = cad.ActionName,
                        HttpMethods = methods,
                        Template = cad.AttributeRouteInfo?.Template,
                        Attributes = attrs.Distinct().OrderBy(x => x).ToList()
                    };
                }
            }
            return items.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        }

        public static void Write(IServiceProvider services, string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir) == false)
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(Collect(services), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            // write to a temp name first so that a reader never sees a half written file
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
    }
}
