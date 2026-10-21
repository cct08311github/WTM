using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>
    /// Writes the readable result of the matrix runs to {output}/permission-matrix: per site and configuration a full matrix (markdown and json) and
    /// a ready-to-paste list of entries for the actions the table does not know yet, plus one compact summary of all runs.
    /// </summary>
    public static class MatrixReport
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

        public static void WriteAll(string directory)
        {
            var runs = MatrixSuite.FinishedRuns.OrderBy(x => x.Setup.Id, StringComparer.Ordinal).ToList();
            if (runs.Count == 0)
            {
                return;
            }
            Directory.CreateDirectory(directory);
            var summary = new StringBuilder();
            summary.AppendLine("# Permission matrix");
            summary.AppendLine();
            summary.AppendLine("Outcome of every action of the inventory for each identity, compared with the checked-in table (`Security/*.permission-matrix.json`). The full matrices are in the artifact `e2e-permission-matrix`.");
            summary.AppendLine();
            summary.AppendLine("| Site / configuration | Actions | Requests (action x method) | Cells | Pass | Known deviations | Resolved | Skipped | Mismatches | Missing entries | Stale entries |");
            summary.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var run in runs)
            {
                var c = run.Cells;
                summary.AppendLine($"| {run.Setup.Id} | {run.Inventory.Count} | {run.Probes.Count} | {c.Count} | {c.Count(x => x.State == CellStatus.Pass)} | {c.Count(x => x.State == CellStatus.KnownDeviation)} | {c.Count(x => x.State == CellStatus.Resolved)} | {c.Count(x => x.State == CellStatus.Skipped)} | {c.Count(x => x.State == CellStatus.Mismatch)} | {run.MissingEntries.Count} | {run.StaleEntries.Count} |");
            }
            summary.AppendLine();
            summary.AppendLine("Outcome types: " + string.Join("; ", Outcome.Catalog.Select(x => $"`{x.Type}` {x.Meaning}")) + ". Any other status is reported by its number.");
            foreach (var run in runs)
            {
                File.WriteAllText(Path.Combine(directory, run.Setup.Id + ".matrix.md"), Markdown(run));
                File.WriteAllText(Path.Combine(directory, run.Setup.Id + ".matrix.json"), JsonSerializer.Serialize(ToJson(run), Json));
                WriteSuggestions(directory, run);
                summary.AppendLine();
                summary.AppendLine($"## {run.Setup.Id}");
                summary.AppendLine();
                AppendHistogram(summary, run);
                AppendProblems(summary, run, limit: 40);
            }
            File.WriteAllText(Path.Combine(directory, "summary.md"), summary.ToString());
        }

        private static void AppendHistogram(StringBuilder sb, MatrixRun run)
        {
            var observed = run.Cells.Where(c => c.Variant == null).ToList();
            var outcomes = observed.Select(c => c.Observed).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            sb.AppendLine("Observed outcomes per identity (requests):");
            sb.AppendLine();
            sb.AppendLine("| outcome | " + string.Join(" | ", run.Identities) + " |");
            sb.AppendLine("|---|" + string.Concat(run.Identities.Select(_ => "---:|")));
            foreach (var o in outcomes)
            {
                sb.AppendLine($"| `{o}` | " + string.Join(" | ", run.Identities.Select(i => observed.Count(c => c.Identity == i && c.Observed == o))) + " |");
            }
            sb.AppendLine();
        }

        private static void AppendProblems(StringBuilder sb, MatrixRun run, int limit)
        {
            void Section(string title, IEnumerable<string> lines)
            {
                var list = lines.ToList();
                if (list.Count == 0)
                {
                    return;
                }
                sb.AppendLine($"**{title}** ({list.Count})");
                sb.AppendLine();
                foreach (var l in list.Take(limit))
                {
                    sb.AppendLine("- " + l);
                }
                if (list.Count > limit)
                {
                    sb.AppendLine($"- ... {list.Count - limit} more in the full report");
                }
                sb.AppendLine();
            }
            Section("Mismatches", run.Cells.Where(c => c.State == CellStatus.Mismatch).Select(Describe));
            Section("Resolved known deviations (remove the record from the table)", run.Cells.Where(c => c.State == CellStatus.Resolved).Select(Describe));
            Section("Actions without a table entry", run.MissingEntries.Select(x => $"`{x}`"));
            Section("Table entries without an action", run.StaleEntries.Select(x => $"`{x}`"));
            Section("Entries that contradict the declared rules", run.RuleViolations);
            // known deviations are many cells of few causes: one line per note
            var deviations = run.Cells.Where(c => c.State == CellStatus.KnownDeviation).ToList();
            Section("Known deviations (tracked in issues, not failing)", deviations.GroupBy(c => c.DeviationNote).Select(g =>
            {
                var keys = g.Select(c => c.EntryKey).Distinct().ToList();
                return $"{g.Key} - {keys.Count} requests, {g.Count()} cells: " + string.Join(", ", keys.Take(6).Select(k => $"`{k}`")) + (keys.Count > 6 ? ", ..." : "");
            }));
        }

        /// <summary>The observed outcome; when the table expects something else, with the expectation.</summary>
        private static string Show(Cell c)
        {
            if (c.State == CellStatus.Skipped)
            {
                return "not requested";
            }
            return Outcome.Matches(c.Expected, c.Observed) ? $"`{c.Observed}`" : $"`{c.Observed}` (expected `{c.Expected}`)";
        }

        private static string Describe(Cell c)
        {
            var key = (c.Variant == null ? "" : c.Variant + ": ") + c.EntryKey;
            return $"`{key}` / {c.Identity}: expected `{c.Expected}`, observed `{c.Observed}`" + (string.IsNullOrEmpty(c.DeviationNote) ? "" : " - " + c.DeviationNote);
        }

        private static string Markdown(MatrixRun run)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Permission matrix: {run.Setup.Id}");
            sb.AppendLine();
            sb.AppendLine($"Generated {DateTime.UtcNow:u}. {run.Inventory.Count} actions, {run.Probes.Count} requests per identity, identities: {string.Join(", ", run.Identities)}.");
            sb.AppendLine();
            sb.AppendLine("## Outcome types");
            sb.AppendLine();
            foreach (var (type, meaning) in Outcome.Catalog)
            {
                sb.AppendLine($"- `{type}`: {meaning}");
            }
            sb.AppendLine("- any other status is shown as its number (for example `400`, `415`, `500`)");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            AppendHistogram(sb, run);
            AppendProblems(sb, run, limit: int.MaxValue);
            sb.AppendLine("## Matrix");
            sb.AppendLine();
            sb.AppendLine("Cells show the observed outcome. `X (expected Y)` marks a difference: a known deviation if the table records it, otherwise a mismatch.");
            sb.AppendLine();
            sb.AppendLine("| action | " + string.Join(" | ", run.Identities) + " |");
            sb.AppendLine("|---|" + string.Concat(run.Identities.Select(_ => "---|")));
            foreach (var probe in run.Probes)
            {
                var cells = run.Identities.Select(i =>
                {
                    var c = run.Cells.FirstOrDefault(x => x.Variant == null && x.EntryKey == probe.EntryKey && x.Identity == i);
                    return c == null ? $"`{run.Observed[probe.EntryKey][i]}` (no entry)" : Show(c);
                });
                sb.AppendLine($"| `{probe.EntryKey}` | " + string.Join(" | ", cells) + " |");
            }
            foreach (var variant in run.Table.Variants)
            {
                sb.AppendLine();
                sb.AppendLine($"## Variant `{variant.Name}` (extra headers: {string.Join(", ", variant.Headers.Keys)})");
                sb.AppendLine();
                sb.AppendLine("| action | " + string.Join(" | ", run.Identities) + " |");
                sb.AppendLine("|---|" + string.Concat(run.Identities.Select(_ => "---|")));
                foreach (var entry in variant.Entries)
                {
                    var cells = run.Identities.Select(i =>
                    {
                        var c = run.Cells.FirstOrDefault(x => x.Variant == variant.Name && x.EntryKey == entry.Key && x.Identity == i);
                        return c == null ? "-" : Show(c);
                    });
                    sb.AppendLine($"| `{entry.Key}` | " + string.Join(" | ", cells) + " |");
                }
            }
            return sb.ToString();
        }

        private static object ToJson(MatrixRun run)
        {
            return new
            {
                site = run.Setup.Site.Name,
                configuration = run.Setup.Configuration,
                generatedAtUtc = DateTime.UtcNow,
                identities = run.Identities,
                actions = run.Inventory.Count,
                requests = run.Probes.Count,
                totals = new
                {
                    cells = run.Cells.Count,
                    pass = run.Cells.Count(x => x.State == CellStatus.Pass),
                    knownDeviations = run.Cells.Count(x => x.State == CellStatus.KnownDeviation),
                    resolved = run.Cells.Count(x => x.State == CellStatus.Resolved),
                    skipped = run.Cells.Count(x => x.State == CellStatus.Skipped),
                    mismatches = run.Cells.Count(x => x.State == CellStatus.Mismatch),
                    missingEntries = run.MissingEntries.Count,
                    staleEntries = run.StaleEntries.Count
                },
                missingEntries = run.MissingEntries,
                staleEntries = run.StaleEntries,
                ruleViolations = run.RuleViolations,
                cells = run.Cells.Select(c => new { variant = c.Variant, key = c.EntryKey, identity = c.Identity, expected = c.Expected, observed = c.Observed, status = c.Status, location = c.Location, state = c.State.ToString(), note = c.DeviationNote }),
                observed = run.Observed
            };
        }

        /// <summary>
        /// One line per request in the format of the table, with the observed outcomes as expectation. For the actions the table does not
        /// know yet this is what to start from; the outcomes must be reviewed against what the action is meant to do before they are copied.
        /// </summary>
        private static void WriteSuggestions(string directory, MatrixRun run)
        {
            var lines = new List<string>();
            foreach (var probe in run.Probes)
            {
                var entry = new MatrixEntry { Key = probe.EntryKey, Expect = run.Observed[probe.EntryKey] };
                lines.Add("    " + MatrixTable.FormatEntry(entry, run.Identities) + ",");
            }
            File.WriteAllLines(Path.Combine(directory, run.Setup.Id + ".observed-entries.txt"), lines);
        }
    }
}
