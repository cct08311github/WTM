using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WalkingTec.Mvvm.E2E.Coverage
{
    /// <summary>
    /// Collects Chromium precise JS coverage (CDP Profiler.takePreciseCoverage, call counts and block ranges) of one script over
    /// all tests of a run. Playwright for .NET has no coverage API, so the tests talk to the Chrome DevTools Protocol directly.
    /// A function counts as covered when at least one test called it. This is a baseline report; it never fails the run.
    /// </summary>
    public sealed class JsCoverageCollector
    {
        private readonly string _urlPart;
        private readonly object _lock = new();
        private readonly Dictionary<(int Start, int End), FunctionState> _functions = new();
        private bool[] _executed = Array.Empty<bool>();
        private int _scriptLength;

        /// <param name="urlPart">The part of the script URL that identifies the script (query strings differ per page load).</param>
        public JsCoverageCollector(string urlPart)
        {
            _urlPart = urlPart;
        }

        public string UrlPart => _urlPart;

        private sealed class FunctionState
        {
            public string Name { get; set; }
            public bool Called { get; set; }
        }

        /// <summary>Adds the result of one Profiler.takePreciseCoverage call.</summary>
        public void Add(JsonElement takeResult)
        {
            if (takeResult.ValueKind != JsonValueKind.Object || takeResult.TryGetProperty("result", out var scripts) == false)
            {
                return;
            }
            foreach (var script in scripts.EnumerateArray())
            {
                var url = script.GetProperty("url").GetString() ?? string.Empty;
                if (url.Contains(_urlPart, StringComparison.OrdinalIgnoreCase) == false)
                {
                    continue;
                }
                AddScript(script);
            }
        }

        private void AddScript(JsonElement script)
        {
            var functions = script.GetProperty("functions").EnumerateArray().ToList();
            if (functions.Count == 0)
            {
                return;
            }
            // the first function is the script itself; its first range spans the whole text
            var length = functions.Max(f => f.GetProperty("ranges")[0].GetProperty("endOffset").GetInt32());
            lock (_lock)
            {
                if (length > _scriptLength)
                {
                    _scriptLength = length;
                    Array.Resize(ref _executed, length);
                }
                // per-character coverage of this load: ranges are nested, the innermost range wins, so apply them outer to inner
                var covered = new bool[length];
                var ranges = new List<(int Start, int End, bool Hit)>();
                foreach (var f in functions)
                {
                    foreach (var r in f.GetProperty("ranges").EnumerateArray())
                    {
                        ranges.Add((r.GetProperty("startOffset").GetInt32(), r.GetProperty("endOffset").GetInt32(), r.GetProperty("count").GetInt32() > 0));
                    }
                }
                foreach (var r in ranges.OrderBy(x => x.Start).ThenByDescending(x => x.End))
                {
                    for (var i = r.Start; i < r.End && i < length; i++)
                    {
                        covered[i] = r.Hit;
                    }
                }
                for (var i = 0; i < length; i++)
                {
                    if (covered[i])
                    {
                        _executed[i] = true;
                    }
                }
                foreach (var f in functions)
                {
                    var first = f.GetProperty("ranges")[0];
                    var key = (first.GetProperty("startOffset").GetInt32(), first.GetProperty("endOffset").GetInt32());
                    if (_functions.TryGetValue(key, out var state) == false)
                    {
                        state = new FunctionState { Name = f.GetProperty("functionName").GetString() };
                        _functions[key] = state;
                    }
                    if (first.GetProperty("count").GetInt32() > 0)
                    {
                        state.Called = true;
                    }
                }
            }
        }

        public bool HasData => _functions.Count > 0;

        /// <param name="source">The script text as served (offsets are positions in this text).</param>
        public JsCoverageReport Build(string scriptName, string source)
        {
            lock (_lock)
            {
                // drop the script's own top level entry (covers the whole text) from the function numbers
                var topLevel = _functions.Where(x => x.Key.Start == 0 && x.Key.End >= _scriptLength).Select(x => x.Key).ToList();
                var items = _functions.Where(x => topLevel.Contains(x.Key) == false).OrderBy(x => x.Key.Start).ToList();
                var report = new JsCoverageReport
                {
                    Script = scriptName,
                    GeneratedAtUtc = DateTime.UtcNow,
                    Functions = new JsCoverageTotals { Total = items.Count, Covered = items.Count(x => x.Value.Called) },
                    UncoveredFunctions = items.Where(x => x.Value.Called == false)
                        .Select(x => new JsFunctionRef { Name = string.IsNullOrEmpty(x.Value.Name) ? "(anonymous)" : x.Value.Name, Line = LineOf(source, x.Key.Start) })
                        .ToList()
                };
                report.Functions.Percent = Percent(report.Functions.Covered, report.Functions.Total);
                var coveredChars = _executed.Count(x => x);
                report.Characters = new JsCoverageTotals { Total = _scriptLength, Covered = coveredChars, Percent = Percent(coveredChars, _scriptLength) };
                // named functions only: a quick view of the framework's public ff.* entry points
                var named = items.Where(x => string.IsNullOrEmpty(x.Value.Name) == false).ToList();
                report.NamedFunctions = new JsCoverageTotals { Total = named.Count, Covered = named.Count(x => x.Value.Called), Percent = Percent(named.Count(x => x.Value.Called), named.Count) };
                return report;
            }
        }

        private static double Percent(int covered, int total) => total == 0 ? 0 : Math.Round(100.0 * covered / total, 1);

        private static int LineOf(string source, int offset)
        {
            if (string.IsNullOrEmpty(source))
            {
                return 0;
            }
            var line = 1;
            for (var i = 0; i < offset && i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                }
            }
            return line;
        }
    }

    public sealed class JsCoverageTotals
    {
        public int Total { get; set; }
        public int Covered { get; set; }
        public double Percent { get; set; }
    }

    public sealed class JsFunctionRef
    {
        public string Name { get; set; }
        public int Line { get; set; }
    }

    public sealed class JsCoverageReport
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

        public string Script { get; set; }
        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>Functions called at least once by any test, including anonymous callbacks.</summary>
        public JsCoverageTotals Functions { get; set; }

        /// <summary>The same for functions that have a name (ff.* entry points and helpers).</summary>
        public JsCoverageTotals NamedFunctions { get; set; }

        /// <summary>Characters of the script text that belong to code that ran, by the innermost block range.</summary>
        public JsCoverageTotals Characters { get; set; }

        public List<JsFunctionRef> UncoveredFunctions { get; set; }

        public void WriteTo(string directory, string name)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".report.json"), JsonSerializer.Serialize(this, Json));
            File.WriteAllText(Path.Combine(directory, name + ".report.md"), ToMarkdown());
        }

        public string ToSummary()
        {
            return $"JS coverage [{Script}]: functions {Functions.Covered}/{Functions.Total} ({Functions.Percent}%), named functions {NamedFunctions.Covered}/{NamedFunctions.Total} ({NamedFunctions.Percent}%), characters {Characters.Percent}%";
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# JS coverage: {Script}");
            sb.AppendLine();
            sb.AppendLine($"Generated {GeneratedAtUtc:u}. Baseline only, the run does not fail on low coverage. Chromium precise coverage (call counts, block ranges), aggregated over all tests.");
            sb.AppendLine();
            sb.AppendLine("| Measure | Covered | Total | Coverage |");
            sb.AppendLine("|---|---:|---:|---:|");
            sb.AppendLine($"| Functions | {Functions.Covered} | {Functions.Total} | {Functions.Percent}% |");
            sb.AppendLine($"| Named functions | {NamedFunctions.Covered} | {NamedFunctions.Total} | {NamedFunctions.Percent}% |");
            sb.AppendLine($"| Characters (block ranges) | {Characters.Covered} | {Characters.Total} | {Characters.Percent}% |");
            sb.AppendLine();
            sb.AppendLine($"## Functions never called ({UncoveredFunctions.Count})");
            sb.AppendLine();
            foreach (var f in UncoveredFunctions)
            {
                sb.AppendLine($"- `{f.Name}` (line {f.Line})");
            }
            return sb.ToString();
        }
    }
}
