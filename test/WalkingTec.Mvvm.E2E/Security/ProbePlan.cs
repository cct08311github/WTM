using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.E2E.Coverage;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>One request of the matrix: an action of the inventory called with one HTTP method.</summary>
    public sealed class Probe
    {
        /// <summary>Table key, "{inventory key} {METHOD}".</summary>
        public string EntryKey { get; init; }
        public InventoryItem Item { get; init; }
        public string Method { get; init; }
        public string Url { get; init; }

        /// <summary>True for API controllers (JSON bodies); MVC controllers get an empty form.</summary>
        public bool IsApi { get; init; }
    }

    /// <summary>
    /// Turns the route inventory of a site into the requests of the matrix. The requests are harmless by construction: ids do not exist,
    /// bodies are empty ({} for APIs, an empty form for MVC actions), and nothing is uploaded.
    /// </summary>
    public static class ProbePlan
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        /// <summary>The id used wherever an action wants one; it does not exist in any table.</summary>
        public const string MissingId = "e2e-missing-id";

        public static List<InventoryItem> LoadInventory(string path)
        {
            return JsonSerializer.Deserialize<List<InventoryItem>>(File.ReadAllText(path), Json);
        }

        /// <param name="queryOverrides">Query strings (without '?') for actions that change state of the site when called without arguments.</param>
        public static List<Probe> Build(IEnumerable<InventoryItem> inventory, IReadOnlyDictionary<string, string> queryOverrides = null)
        {
            var probes = new List<Probe>();
            foreach (var item in inventory)
            {
                var variants = item.Variants.Count > 0 ? item.Variants : new List<RouteVariant> { new RouteVariant { Template = item.Template, HttpMethods = item.HttpMethods } };
                var rows = variants.SelectMany(v => MethodsOf(v).Select(m => (Variant: v, Method: m))).ToList();
                foreach (var byMethod in rows.GroupBy(x => x.Method))
                {
                    // two routes with the same key and method (controllers of the same name in different namespaces) are told apart by their template
                    var distinct = byMethod.GroupBy(x => x.Variant.Template).Select(g => g.First()).ToList();
                    foreach (var row in distinct)
                    {
                        var entryKey = MatrixTable.EntryKey(item.Key, row.Method) + (distinct.Count > 1 ? " [" + row.Variant.Template + "]" : "");
                        string query = null;
                        queryOverrides?.TryGetValue(entryKey, out query);
                        probes.Add(new Probe
                        {
                            EntryKey = entryKey,
                            Item = item,
                            Method = row.Method,
                            Url = UrlOf(item, row.Variant, query),
                            IsApi = item.Attributes.Contains("ApiController", StringComparer.OrdinalIgnoreCase)
                        });
                    }
                }
            }
            return probes.OrderBy(x => x.EntryKey, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The HTTP methods the matrix uses for one route: the methods it declares (HEAD and OPTIONS are not used), or GET when it declares
        /// none. A route without a template is routed by convention and accepts any method, so GET covers it; its POST overload, when there
        /// is one, is a variant of its own.
        /// </summary>
        public static IEnumerable<string> MethodsOf(RouteVariant variant)
        {
            var declared = variant.HttpMethods.Where(x => x != "HEAD" && x != "OPTIONS").ToList();
            return declared.Count == 0 ? new List<string> { "GET" } : declared;
        }

        private static readonly Regex RouteParameter = new Regex(@"\{\*{0,2}(?<name>[A-Za-z0-9_]+)(:[^}?]*)?\??\}", RegexOptions.Compiled);

        public static string UrlOf(InventoryItem item, RouteVariant variant, string query = null)
        {
            string path;
            if (variant.Template != null)
            {
                path = "/" + RouteParameter.Replace(variant.Template, m => ValueFor(m.Groups["name"].Value));
            }
            else
            {
                path = "/" + string.Join("/", new[] { item.Area, item.Controller, item.Action }.Where(x => string.IsNullOrEmpty(x) == false));
                query ??= "id=" + MissingId;
            }
            return string.IsNullOrEmpty(query) ? path : path + "?" + query;
        }

        private static string ValueFor(string name)
        {
            if (name.Equals("apiVersion", StringComparison.OrdinalIgnoreCase) || name.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                return "1";
            }
            if (name.Equals("versionOptions", StringComparison.OrdinalIgnoreCase))
            {
                return "latest";
            }
            if (name.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                return "e2e-missing-token";
            }
            return MissingId;
        }
    }
}
