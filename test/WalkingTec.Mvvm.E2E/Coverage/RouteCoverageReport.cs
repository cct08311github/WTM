using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WalkingTec.Mvvm.E2E.Infrastructure;

namespace WalkingTec.Mvvm.E2E.Coverage
{
    public sealed class InventoryItem
    {
        public string Key { get; set; }
        public string Area { get; set; }
        public string Controller { get; set; }
        public string ControllerType { get; set; }
        public string Action { get; set; }
        public List<string> HttpMethods { get; set; } = new();
        public string Template { get; set; }
        public List<string> Attributes { get; set; } = new();
    }

    public sealed class RouteHitRecord
    {
        public string Key { get; set; }
        public string HttpMethod { get; set; }
        public string Path { get; set; }
    }

    public sealed class ReportEntry
    {
        public string Key { get; set; }
        public List<string> HttpMethods { get; set; }
        public int Hits { get; set; }
        public string Reason { get; set; }
        public string RuleId { get; set; }
    }

    public sealed class ReportTotals
    {
        public int Total { get; set; }
        public int Excluded { get; set; }
        public int Eligible { get; set; }
        public int Covered { get; set; }
        public int Uncovered { get; set; }
        public double CoveragePercent { get; set; }
    }

    /// <summary>
    /// Compares the actions a site exposes (inventory) with the actions that were executed during the run (hits)
    /// and the checked-in exclusion list. This is a baseline report: it never fails the run.
    /// </summary>
    public sealed class RouteCoverageReport
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

        public string Target { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public ReportTotals Totals { get; set; }
        public List<ReportEntry> Covered { get; set; }
        public List<ReportEntry> Uncovered { get; set; }
        public List<ReportEntry> Excluded { get; set; }

        /// <summary>Actions that were hit but are not in the inventory (should stay empty).</summary>
        public List<string> HitsNotInInventory { get; set; }

        /// <summary>Exclusion rules that matched nothing (the rule is stale or mistyped).</summary>
        public List<string> UnusedRules { get; set; }

        public static RouteCoverageReport Build(SiteTarget target, string inventoryPath, string hitLogPath, string exclusionPath)
        {
            var inventory = JsonSerializer.Deserialize<List<InventoryItem>>(File.ReadAllText(inventoryPath), Json);
            var hits = new List<RouteHitRecord>();
            if (File.Exists(hitLogPath))
            {
                foreach (var line in File.ReadLines(hitLogPath).Where(x => string.IsNullOrWhiteSpace(x) == false))
                {
                    hits.Add(JsonSerializer.Deserialize<RouteHitRecord>(line, Json));
                }
            }
            var exclusions = RouteExclusions.Load(exclusionPath);
            var hitCount = hits.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            var report = new RouteCoverageReport
            {
                Target = target.Name,
                GeneratedAtUtc = DateTime.UtcNow,
                Covered = new List<ReportEntry>(),
                Uncovered = new List<ReportEntry>(),
                Excluded = new List<ReportEntry>()
            };
            foreach (var item in inventory.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                hitCount.TryGetValue(item.Key, out var n);
                var rule = exclusions.FindRule(item);
                var entry = new ReportEntry { Key = item.Key, HttpMethods = item.HttpMethods, Hits = n };
                if (rule != null)
                {
                    entry.Reason = rule.Reason;
                    entry.RuleId = rule.Id;
                    report.Excluded.Add(entry);
                }
                else if (n > 0)
                {
                    report.Covered.Add(entry);
                }
                else
                {
                    report.Uncovered.Add(entry);
                }
            }
            var known = new HashSet<string>(inventory.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
            report.HitsNotInInventory = hitCount.Keys.Where(x => known.Contains(x) == false).OrderBy(x => x, StringComparer.Ordinal).ToList();
            report.UnusedRules = exclusions.Rules.Where(r => report.Excluded.Any(e => e.RuleId == r.Id) == false).Select(r => r.Id).ToList();
            var eligible = report.Covered.Count + report.Uncovered.Count;
            report.Totals = new ReportTotals
            {
                Total = inventory.Count,
                Excluded = report.Excluded.Count,
                Eligible = eligible,
                Covered = report.Covered.Count,
                Uncovered = report.Uncovered.Count,
                CoveragePercent = eligible == 0 ? 0 : Math.Round(100.0 * report.Covered.Count / eligible, 1)
            };
            return report;
        }

        public void WriteTo(string directory, string name)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".report.json"), JsonSerializer.Serialize(this, Json));
            File.WriteAllText(Path.Combine(directory, name + ".report.md"), ToMarkdown());
        }

        public string ToSummary()
        {
            return $"Route coverage [{Target}]: total {Totals.Total}, excluded {Totals.Excluded}, eligible {Totals.Eligible}, covered {Totals.Covered}, uncovered {Totals.Uncovered} ({Totals.CoveragePercent}%)";
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Route coverage: {Target}");
            sb.AppendLine();
            sb.AppendLine($"Generated {GeneratedAtUtc:u}. Baseline only, the run does not fail on low coverage.");
            sb.AppendLine();
            sb.AppendLine("| Total | Excluded | Eligible | Covered | Uncovered | Coverage |");
            sb.AppendLine("|---:|---:|---:|---:|---:|---:|");
            sb.AppendLine($"| {Totals.Total} | {Totals.Excluded} | {Totals.Eligible} | {Totals.Covered} | {Totals.Uncovered} | {Totals.CoveragePercent}% |");
            sb.AppendLine();
            sb.AppendLine($"## Covered ({Covered.Count})");
            sb.AppendLine();
            foreach (var e in Covered) sb.AppendLine($"- `{e.Key}` ({e.Hits} hits)");
            sb.AppendLine();
            sb.AppendLine($"## Uncovered ({Uncovered.Count})");
            sb.AppendLine();
            foreach (var e in Uncovered) sb.AppendLine($"- `{e.Key}` {string.Join("/", e.HttpMethods ?? new List<string>())}");
            sb.AppendLine();
            sb.AppendLine($"## Excluded ({Excluded.Count})");
            sb.AppendLine();
            foreach (var g in Excluded.GroupBy(x => x.RuleId))
            {
                var first = g.First();
                sb.AppendLine($"- rule `{g.Key}` ({g.Count()} actions): {first.Reason}");
            }
            if (HitsNotInInventory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Hit but not in inventory");
                sb.AppendLine();
                foreach (var k in HitsNotInInventory) sb.AppendLine($"- `{k}`");
            }
            if (UnusedRules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Exclusion rules that matched nothing");
                sb.AppendLine();
                foreach (var k in UnusedRules) sb.AppendLine($"- `{k}`");
            }
            return sb.ToString();
        }
    }
}
