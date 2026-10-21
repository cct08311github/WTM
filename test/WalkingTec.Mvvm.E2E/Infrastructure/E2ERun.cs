using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Coverage;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Assembly wide state of one test run: the output directory and the sites that were started.
    /// A site is started on first use and shared by all tests; when the run ends every started site is stopped
    /// and its route coverage report is written.
    /// </summary>
    [TestClass]
    public static class E2ERun
    {
        private static readonly ConcurrentDictionary<string, Lazy<Task<SiteProcess>>> Sites = new();
        private static string _outputDir;

        /// <summary>
        /// Output root. Defaults to {repo}/TestResults/e2e; override with the E2E_OUTPUT_DIR environment variable.
        /// </summary>
        public static string OutputDir => _outputDir ??= ResolveOutputDir();

        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            // web-first assertions retry until this timeout; LayUI pages load their grids with a second request
            Microsoft.Playwright.Assertions.SetDefaultExpectTimeout(15000);
            // start from an empty directory so that artifacts of an older run are never mistaken for this one
            if (Directory.Exists(OutputDir))
            {
                Directory.Delete(OutputDir, true);
            }
            Directory.CreateDirectory(OutputDir);
        }

        public static Task<SiteProcess> GetSiteAsync(SiteTarget target)
        {
            return Sites.GetOrAdd(target.Name, _ => new Lazy<Task<SiteProcess>>(
                () => SiteProcess.StartAsync(target, OutputDir, TimeSpan.FromSeconds(120)))).Value;
        }

        [AssemblyCleanup]
        public static void Cleanup()
        {
            foreach (var entry in Sites.Values.Where(x => x.IsValueCreated))
            {
                SiteProcess site;
                try
                {
                    site = entry.Value.GetAwaiter().GetResult();
                }
                catch
                {
                    // the site never started; nothing to report
                    continue;
                }
                var target = site.Target;
                site.Dispose();
                var report = RouteCoverageReport.Build(target, site.InventoryPath, site.HitLogPath,
                    Path.Combine(AppContext.BaseDirectory, target.RouteExclusionFile));
                report.WriteTo(Path.Combine(OutputDir, "route-coverage"), target.Name);
                Console.WriteLine(report.ToSummary());
            }
        }

        private static string ResolveOutputDir()
        {
            var env = Environment.GetEnvironmentVariable("E2E_OUTPUT_DIR");
            if (string.IsNullOrWhiteSpace(env) == false)
            {
                return Path.GetFullPath(env);
            }
            return Path.Combine(SiteTargets.LayUI.RepoRoot, "TestResults", "e2e");
        }
    }
}
