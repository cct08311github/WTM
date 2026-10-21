using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WalkingTec.Mvvm.E2E.Coverage;
using WalkingTec.Mvvm.E2E.Infrastructure;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>One site in one configuration, with everything the matrix needs to know about it.</summary>
    public sealed class MatrixSetup
    {
        /// <summary>Configuration name in the table: "default" or a key of <see cref="MatrixTable.Configurations"/>.</summary>
        public string Configuration { get; init; } = "default";

        public SiteTarget Site { get; init; }

        /// <summary>File name of the table (checked in next to this class, copied to the output directory).</summary>
        public string TableFile { get; init; }

        public Func<SiteProcess, Task<List<MatrixIdentity>>> OpenIdentities { get; init; }

        /// <summary>How each identity authenticates (see <see cref="IntentRules"/>).</summary>
        public IReadOnlyDictionary<string, IdentityKind> Kinds { get; init; }

        /// <summary>
        /// Paths a redirect to the sign-in page goes to: the login path of the site and the access-denied path of the cookie scheme
        /// (both are settings of the site).
        /// </summary>
        public IReadOnlyCollection<string> LoginPaths { get; init; }

        /// <summary>
        /// The privileged actions (keys of the inventory) the role of the <c>limited</c> identity was given. Every other privileged action is
        /// refused to it; the table is checked against this list, which comes from the definition of the role.
        /// </summary>
        public IReadOnlyCollection<string> LimitedGrants { get; init; }

        /// <summary>Query strings for actions that would change state of the site when called without arguments (key = table entry key).</summary>
        public IReadOnlyDictionary<string, string> QueryOverrides { get; init; }

        /// <summary>Actions that end a session or change the account; sent last so that they cannot affect the other requests.</summary>
        public IReadOnlyCollection<string> SessionActions { get; init; } = Array.Empty<string>();

        /// <summary>Runs after all requests (restore of site state a probe may have changed).</summary>
        public Func<SiteProcess, MatrixRunner, List<MatrixIdentity>, Task> Cleanup { get; init; }

        public string Id => Site.Name + (Configuration == "default" ? "" : "." + Configuration);
    }

    public enum CellStatus
    {
        /// <summary>Observed outcome equals the intended one.</summary>
        Pass,

        /// <summary>Observed outcome is the one recorded as known deviation.</summary>
        KnownDeviation,

        /// <summary>A known deviation is recorded but the intended outcome is observed: remove the record.</summary>
        Resolved,

        /// <summary>Neither the intended nor the recorded outcome.</summary>
        Mismatch,

        /// <summary>The request is not sent (the entry says why).</summary>
        Skipped
    }

    public sealed class Cell
    {
        public string Variant { get; init; }
        public string EntryKey { get; init; }
        public string Identity { get; init; }
        public string Expected { get; init; }
        public string Observed { get; init; }
        public int Status { get; init; }
        public string Location { get; init; }
        public CellStatus State { get; init; }
        public string DeviationNote { get; init; }
    }

    /// <summary>Everything one execution of the matrix produced.</summary>
    public sealed class MatrixRun
    {
        public MatrixSetup Setup { get; init; }
        public MatrixTable Table { get; init; }
        public Dictionary<string, MatrixEntry> Entries { get; init; }
        public List<InventoryItem> Inventory { get; init; }
        public List<Probe> Probes { get; init; }
        public List<string> Identities { get; init; }
        public List<Cell> Cells { get; init; } = new();

        /// <summary>Actions of the inventory that have no entry in the table (new endpoints must declare what they are meant to do).</summary>
        public List<string> MissingEntries { get; init; } = new();

        /// <summary>Entries without a matching action (stale).</summary>
        public List<string> StaleEntries { get; init; } = new();

        public List<string> RuleViolations { get; init; } = new();

        /// <summary>Observed outcome per probe and identity, kept as suggestion for new entries.</summary>
        public Dictionary<string, Dictionary<string, string>> Observed { get; init; } = new();
    }

    public static class MatrixSuite
    {
        private static readonly ConcurrentDictionary<string, Lazy<Task<MatrixRun>>> Runs = new();

        /// <summary>All finished runs of this test run, for the report written at the end.</summary>
        public static IEnumerable<MatrixRun> FinishedRuns => Runs.Values.Where(x => x.IsValueCreated && x.Value.IsCompletedSuccessfully).Select(x => x.Value.Result);

        /// <summary>Sends every request of the matrix once (the first test that needs it does that) and compares with the table.</summary>
        public static Task<MatrixRun> GetAsync(MatrixSetup setup)
        {
            return Runs.GetOrAdd(setup.Id, _ => new Lazy<Task<MatrixRun>>(() => ExecuteAsync(setup))).Value;
        }

        private static async Task<MatrixRun> ExecuteAsync(MatrixSetup setup)
        {
            var site = await E2ERun.GetSiteAsync(setup.Site);
            var identities = await setup.OpenIdentities(site);
            var table = MatrixTable.Load(Path.Combine(AppContext.BaseDirectory, setup.TableFile));
            var entries = table.Resolve(setup.Configuration);
            var inventory = ProbePlan.LoadInventory(site.InventoryPath);
            var probes = ProbePlan.Build(inventory, setup.QueryOverrides);
            var names = identities.Select(x => x.Name).ToList();
            if (names.SequenceEqual(table.Identities) == false)
            {
                throw new InvalidOperationException($"The identities of the table ({string.Join(", ", table.Identities)}) differ from the ones the test opens ({string.Join(", ", names)}).");
            }

            var run = new MatrixRun { Setup = setup, Table = table, Entries = entries, Inventory = inventory, Probes = probes, Identities = names };
            var texts = RefusalTexts.Load(site.Target.RepoRoot);
            using var runner = new MatrixRunner(site.BaseUri, texts, setup.LoginPaths);

            var skipped = probes.Where(p => entries.TryGetValue(p.EntryKey, out var e) && string.IsNullOrEmpty(e.Skip) == false).Select(p => p.EntryKey).ToHashSet();
            var sendable = probes.Where(p => skipped.Contains(p.EntryKey) == false).ToList();
            var ordered = sendable.Where(p => setup.SessionActions.Contains(p.EntryKey) == false).Concat(sendable.Where(p => setup.SessionActions.Contains(p.EntryKey))).ToList();
            var results = new Dictionary<(string Variant, string Entry, string Identity), ProbeResult>();
            try
            {
                foreach (var probe in ordered)
                {
                    foreach (var identity in identities)
                    {
                        var result = await runner.SendAsync(identity, probe);
                        results[(null, probe.EntryKey, identity.Name)] = result;
                    }
                }
                foreach (var variant in table.Variants)
                {
                    foreach (var entry in variant.Entries)
                    {
                        var probe = probes.FirstOrDefault(p => p.EntryKey == entry.Key);
                        if (probe == null)
                        {
                            continue;
                        }
                        foreach (var identity in identities)
                        {
                            results[(variant.Name, probe.EntryKey, identity.Name)] = await runner.SendAsync(identity, probe, variant.Headers);
                        }
                    }
                }
            }
            finally
            {
                if (setup.Cleanup != null)
                {
                    await setup.Cleanup(site, runner, identities);
                }
            }

            Compare(run, results, table, skipped);
            return run;
        }

        private static void Compare(MatrixRun run, Dictionary<(string Variant, string Entry, string Identity), ProbeResult> results, MatrixTable table, HashSet<string> skipped)
        {
            var probeKeys = run.Probes.Select(x => x.EntryKey).ToHashSet(StringComparer.Ordinal);
            run.MissingEntries.AddRange(probeKeys.Where(k => run.Entries.ContainsKey(k) == false).OrderBy(x => x, StringComparer.Ordinal));
            run.StaleEntries.AddRange(run.Entries.Keys.Where(k => probeKeys.Contains(k) == false).OrderBy(x => x, StringComparer.Ordinal));

            foreach (var probe in run.Probes)
            {
                var wasSkipped = skipped.Contains(probe.EntryKey);
                run.Observed[probe.EntryKey] = run.Identities.ToDictionary(i => i, i => wasSkipped ? "-" : results[(null, probe.EntryKey, i)].Outcome);
                if (run.Entries.TryGetValue(probe.EntryKey, out var entry) == false)
                {
                    continue;
                }
                foreach (var identity in run.Identities)
                {
                    run.Cells.Add(wasSkipped
                        ? new Cell { EntryKey = probe.EntryKey, Identity = identity, Expected = entry.Expect[identity], Observed = "-", State = CellStatus.Skipped }
                        : Judge(null, entry, identity, results[(null, probe.EntryKey, identity)]));
                }
                run.RuleViolations.AddRange(IntentRules.Violations(entry, probe.Item, run.Setup.Kinds, run.Setup.LimitedGrants));
            }
            foreach (var variant in table.Variants)
            {
                foreach (var entry in variant.Entries)
                {
                    foreach (var identity in run.Identities)
                    {
                        if (results.TryGetValue((variant.Name, entry.Key, identity), out var result))
                        {
                            run.Cells.Add(Judge(variant.Name, entry, identity, result));
                        }
                    }
                }
            }
        }

        private static Cell Judge(string variant, MatrixEntry entry, string identity, ProbeResult result)
        {
            var expected = entry.Expect[identity];
            string deviation = null;
            entry.KnownDeviation?.Observed.TryGetValue(identity, out deviation);
            CellStatus state;
            if (Outcome.Matches(expected, result.Outcome))
            {
                state = deviation == null ? CellStatus.Pass : CellStatus.Resolved;
            }
            else if (deviation != null && Outcome.Matches(deviation, result.Outcome))
            {
                state = CellStatus.KnownDeviation;
            }
            else
            {
                state = CellStatus.Mismatch;
            }
            return new Cell
            {
                Variant = variant,
                EntryKey = entry.Key,
                Identity = identity,
                Expected = expected,
                Observed = result.Outcome,
                Status = result.Status,
                Location = result.Location,
                State = state,
                DeviationNote = deviation == null ? null : entry.KnownDeviation.Note
            };
        }
    }
}
