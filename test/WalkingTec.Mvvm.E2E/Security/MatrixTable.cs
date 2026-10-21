using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>
    /// The checked-in expectations of one target: for every action and HTTP method the outcome each identity must get (see <see cref="Outcome"/>).
    /// The table states the intended behaviour. Where the observed behaviour is known to differ, the entry keeps the intended outcome in
    /// <see cref="MatrixEntry.Expect"/> and lists the observed one under <see cref="MatrixEntry.KnownDeviations"/>: the test passes while
    /// the deviation persists, reports it, and tells when it has been resolved.
    /// </summary>
    public sealed class MatrixTable
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Target name (see SiteTarget.Name); informational.</summary>
        public string Target { get; set; }

        /// <summary>The identities of the table, in report order.</summary>
        public List<string> Identities { get; set; } = new();

        /// <summary>Entries for the default configuration.</summary>
        public List<MatrixEntry> Entries { get; set; } = new();

        /// <summary>
        /// The same actions called with extra request headers, for the answers that depend on them (the LayUI grid sends a header and gets JSON
        /// with a code in a 200 answer instead of the page-style refusal). Their keys must exist as entries of the table.
        /// </summary>
        public List<MatrixVariant> Variants { get; set; } = new();

        /// <summary>
        /// Differences of other configurations (for example EnableWorkflow=true) against <see cref="Entries"/>: entries listed here replace or add
        /// the entry of the same key, keys in <see cref="MatrixOverride.Remove"/> do not exist in that configuration.
        /// </summary>
        public Dictionary<string, MatrixOverride> Configurations { get; set; } = new();

        public static MatrixTable Load(string path)
        {
            var table = JsonSerializer.Deserialize<MatrixTable>(File.ReadAllText(path), Json);
            table.Validate(path);
            return table;
        }

        /// <summary>The entries that apply to a configuration ("default" for the base table).</summary>
        public Dictionary<string, MatrixEntry> Resolve(string configuration)
        {
            var result = Entries.ToDictionary(x => x.Key, StringComparer.Ordinal);
            if (configuration != "default")
            {
                if (Configurations.TryGetValue(configuration, out var over) == false)
                {
                    throw new InvalidDataException($"The table of {Target} has no configuration '{configuration}'.");
                }
                foreach (var key in over.Remove)
                {
                    result.Remove(key);
                }
                foreach (var entry in over.Entries)
                {
                    result[entry.Key] = entry;
                }
            }
            return result;
        }

        private void Validate(string path)
        {
            if (Identities.Count == 0)
            {
                throw new InvalidDataException($"{path}: no identities.");
            }
            void Check(IEnumerable<MatrixEntry> entries, string where)
            {
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Key))
                    {
                        throw new InvalidDataException($"{path}: an entry without key in {where}.");
                    }
                    var missing = Identities.Where(i => e.Expect == null || e.Expect.ContainsKey(i) == false).ToList();
                    if (missing.Count > 0)
                    {
                        throw new InvalidDataException($"{path}: entry '{e.Key}' ({where}) has no expectation for: {string.Join(", ", missing)}.");
                    }
                    if (e.KnownDeviation != null)
                    {
                        var d = e.KnownDeviation;
                        if (string.IsNullOrWhiteSpace(d.Note) || d.Observed.Count == 0 || d.Observed.Keys.Any(i => Identities.Contains(i) == false))
                        {
                            throw new InvalidDataException($"{path}: entry '{e.Key}' ({where}) has an incomplete known deviation (a note and the observed outcome of at least one identity of the table are required).");
                        }
                    }
                }
                var dup = entries.GroupBy(x => x.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
                if (dup != null)
                {
                    throw new InvalidDataException($"{path}: duplicate entry '{dup.Key}' in {where}.");
                }
            }
            Check(Entries, "default");
            foreach (var v in Variants)
            {
                Check(v.Entries, "variant " + v.Name);
                var unknown = v.Entries.Select(x => x.Key).Except(Entries.Select(x => x.Key)).ToList();
                if (unknown.Count > 0)
                {
                    throw new InvalidDataException($"{path}: variant '{v.Name}' refers to entries that do not exist: {string.Join(", ", unknown)}.");
                }
            }
            foreach (var c in Configurations)
            {
                Check(c.Value.Entries, c.Key);
            }
        }

        /// <summary>Entry key of an action and HTTP method: "gadget/create GET".</summary>
        public static string EntryKey(string actionKey, string method) => actionKey + " " + method;

        /// <summary>One entry per line, so that a review diff shows exactly the entries that changed.</summary>
        public static string FormatEntry(MatrixEntry e, IEnumerable<string> identities)
        {
            var sb = new StringBuilder();
            sb.Append("{ \"key\": ").Append(JsonSerializer.Serialize(e.Key));
            sb.Append(", \"expect\": { ");
            sb.Append(string.Join(", ", identities.Select(i => JsonSerializer.Serialize(i) + ": " + JsonSerializer.Serialize(e.Expect[i]))));
            sb.Append(" }");
            if (string.IsNullOrEmpty(e.Exempt) == false) sb.Append(", \"exempt\": ").Append(JsonSerializer.Serialize(e.Exempt));
            if (string.IsNullOrEmpty(e.Note) == false) sb.Append(", \"note\": ").Append(JsonSerializer.Serialize(e.Note));
            if (string.IsNullOrEmpty(e.Skip) == false) sb.Append(", \"skip\": ").Append(JsonSerializer.Serialize(e.Skip));
            if (e.KnownDeviation != null)
            {
                sb.Append(", \"knownDeviation\": { \"note\": ").Append(JsonSerializer.Serialize(e.KnownDeviation.Note)).Append(", \"observed\": { ");
                sb.Append(string.Join(", ", e.KnownDeviation.Observed.Select(o => JsonSerializer.Serialize(o.Key) + ": " + JsonSerializer.Serialize(o.Value))));
                sb.Append(" } }");
            }
            sb.Append(" }");
            return sb.ToString();
        }
    }

    public sealed class MatrixVariant
    {
        public string Name { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new();
        public List<MatrixEntry> Entries { get; set; } = new();
    }

    public sealed class MatrixOverride
    {
        public List<string> Remove { get; set; } = new();
        public List<MatrixEntry> Entries { get; set; } = new();
    }

    public sealed class MatrixEntry
    {
        /// <summary>"{inventory key} {HTTP method}", for example "gadget/create GET".</summary>
        public string Key { get; set; }

        /// <summary>Identity name to the outcome type the framework is meant to answer with.</summary>
        public Dictionary<string, string> Expect { get; set; } = new();

        /// <summary>
        /// Set when the rule derived from the action's attributes (see <see cref="IntentRules"/>) does not describe this action; the text says why.
        /// </summary>
        public string Exempt { get; set; }

        public string Note { get; set; }

        /// <summary>
        /// Set when the request must not be sent (it would call an external site); the text says why. The expectations are still checked
        /// against the declared rules.
        /// </summary>
        public string Skip { get; set; }

        /// <summary>Set when the observed behaviour is known to differ from the intended one in <see cref="Expect"/>.</summary>
        public KnownDeviation KnownDeviation { get; set; }
    }

    /// <summary>Observed behaviour that differs from the intended one in <see cref="MatrixEntry.Expect"/>; tracked in an issue.</summary>
    public sealed class KnownDeviation
    {
        /// <summary>Short, neutral description (the table is public).</summary>
        public string Note { get; set; }

        /// <summary>Identity name to the outcome that is observed for it today; the identities that behave as intended are not listed.</summary>
        public Dictionary<string, string> Observed { get; set; } = new();
    }
}
