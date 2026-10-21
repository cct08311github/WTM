using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WalkingTec.Mvvm.E2E.Coverage
{
    /// <summary>
    /// A checked-in list of actions that are deliberately not part of the coverage target. Every rule needs an id and a reason.
    /// All match fields of a rule must match (AND); at least one match field is required.
    /// </summary>
    public sealed class RouteExclusions
    {
        public List<ExclusionRule> Rules { get; set; } = new();

        public static RouteExclusions Load(string path)
        {
            var result = JsonSerializer.Deserialize<RouteExclusions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (var rule in result.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Reason))
                {
                    throw new InvalidDataException($"{path}: every exclusion rule needs an id and a reason.");
                }
                if (rule.Match == null || rule.Match.IsEmpty)
                {
                    throw new InvalidDataException($"{path}: rule '{rule.Id}' has no match field.");
                }
            }
            if (result.Rules.Select(x => x.Id).Distinct().Count() != result.Rules.Count)
            {
                throw new InvalidDataException($"{path}: rule ids must be unique.");
            }
            return result;
        }

        public ExclusionRule FindRule(InventoryItem item) => Rules.FirstOrDefault(r => r.Match.Matches(item));
    }

    public sealed class ExclusionRule
    {
        public string Id { get; set; }
        public string Reason { get; set; }
        public ExclusionMatch Match { get; set; }
    }

    public sealed class ExclusionMatch
    {
        /// <summary>Exact action key, for example "_framework/getgithubinfo" (case-insensitive).</summary>
        public string Key { get; set; }

        /// <summary>Controller name without the Controller suffix (case-insensitive).</summary>
        public string Controller { get; set; }

        public string Area { get; set; }

        /// <summary>Marker attribute on the action or controller, for example DebugOnly.</summary>
        public string Attribute { get; set; }

        /// <summary>Full type name prefix of the controller, for example "Elsa.".</summary>
        public string ControllerTypePrefix { get; set; }

        public bool IsEmpty => Key == null && Controller == null && Area == null && Attribute == null && ControllerTypePrefix == null;

        public bool Matches(InventoryItem item)
        {
            return (Key == null || string.Equals(Key, item.Key, StringComparison.OrdinalIgnoreCase))
                && (Controller == null || string.Equals(Controller, item.Controller, StringComparison.OrdinalIgnoreCase))
                && (Area == null || string.Equals(Area, item.Area, StringComparison.OrdinalIgnoreCase))
                && (Attribute == null || item.Attributes.Contains(Attribute, StringComparer.OrdinalIgnoreCase))
                && (ControllerTypePrefix == null || (item.ControllerType ?? "").StartsWith(ControllerTypePrefix, StringComparison.Ordinal));
        }
    }
}
