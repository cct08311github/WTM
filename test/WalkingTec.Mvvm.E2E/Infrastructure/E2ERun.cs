using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Playwright;
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
        private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> SignedInStates = new();
        private static IPlaywright _playwright;
        private static string _outputDir;

        /// <summary>Collects the JS coverage of the LayUI framework script over all tests (see <see cref="JsCoverageCollector"/>).</summary>
        public static readonly JsCoverageCollector LayUiScript = new JsCoverageCollector("/_js/framework_layui.js");

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

        /// <summary>The shared Playwright driver (API request contexts of tests that do not need a browser).</summary>
        public static async Task<IPlaywright> GetPlaywrightAsync()
        {
            return _playwright ??= await Playwright.CreateAsync();
        }

        public static Task<SiteProcess> GetSiteAsync(SiteTarget target)
        {
            return Sites.GetOrAdd(target.Name, _ => new Lazy<Task<SiteProcess>>(
                () => SiteProcess.StartAsync(target, OutputDir, TimeSpan.FromSeconds(120)))).Value;
        }

        /// <summary>
        /// Storage state (cookies) of the administrator, created by one login request per site and run. Tests that do not exercise
        /// the sign-in itself start from it, so that each of them does not repeat the login.
        /// </summary>
        public static Task<string> GetSignedInStateAsync(SiteProcess site)
        {
            return SignedInStates.GetOrAdd(site.Target.Name, _ => new Lazy<Task<string>>(async () =>
            {
                _playwright ??= await Playwright.CreateAsync();
                var request = await _playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions { BaseURL = site.BaseUri.ToString() });
                try
                {
                    var response = await request.PostAsync("/Login/Login", new APIRequestContextOptions
                    {
                        Form = request.CreateFormData().Set("ITCode", site.AdminAccount).Set("Password", site.AdminPassword),
                        MaxRedirects = 0
                    });
                    if (response.Status != 302 && response.Status != 200)
                    {
                        throw new InvalidOperationException("The test login returned HTTP " + response.Status);
                    }
                    return await request.StorageStateAsync();
                }
                finally
                {
                    await request.DisposeAsync();
                }
            })).Value;
        }

        [AssemblyCleanup]
        public static void Cleanup()
        {
            Security.MatrixReport.WriteAll(Path.Combine(OutputDir, "permission-matrix"));
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
                if (target.CollectJsCoverage && LayUiScript.HasData)
                {
                    WriteJsCoverage(site);
                }
                site.Dispose();
                if (target.ReportRouteCoverage == false)
                {
                    continue;
                }
                var report = RouteCoverageReport.Build(target, site.InventoryPath, site.HitLogPath,
                    Path.Combine(AppContext.BaseDirectory, target.RouteExclusionFile));
                report.WriteTo(Path.Combine(OutputDir, "route-coverage"), target.Name);
                Console.WriteLine(report.ToSummary());
            }
            _playwright?.Dispose();
            _playwright = null;
        }

        private static void WriteJsCoverage(SiteProcess site)
        {
            // the script text as served by the site: the coverage offsets refer to it
            string source = null;
            try
            {
                using var http = new HttpClient { BaseAddress = site.BaseUri, Timeout = TimeSpan.FromSeconds(10) };
                source = http.GetStringAsync("/_js/framework_layui.js").GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                // line numbers are a convenience; the numbers do not depend on them
            }
            var report = LayUiScript.Build("framework_layui.js", source);
            report.WriteTo(Path.Combine(OutputDir, "js-coverage"), "framework_layui");
            Console.WriteLine(report.ToSummary());
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
