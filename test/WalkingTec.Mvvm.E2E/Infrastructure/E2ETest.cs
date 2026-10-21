using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Base class of all browser tests. It points the browser at the site under test, records a Playwright trace for
    /// every test and keeps trace, screenshot and browser console output only for tests that did not pass.
    /// The server log of the whole run is always kept in {output}/server.
    /// </summary>
    public abstract class E2ETest : PageTest
    {
        private readonly List<string> _console = new();
        private ICDPSession _cdp;

        /// <summary>The site the tests of this class run against.</summary>
        protected virtual SiteTarget Target => SiteTargets.LayUI;

        /// <summary>
        /// When true the test starts with the session of the administrator (one login per run, see <see cref="E2ERun.GetSignedInStateAsync"/>).
        /// Tests of the sign-in flow itself leave it false and log in through the page.
        /// </summary>
        protected virtual bool StartSignedIn => false;

        protected SiteProcess Site { get; private set; }

        public override BrowserNewContextOptions ContextOptions()
        {
            // the first test of the run pays for the site start, the others get the running instance
            Site = E2ERun.GetSiteAsync(Target).GetAwaiter().GetResult();
            return new BrowserNewContextOptions
            {
                BaseURL = Site.BaseUri.ToString(),
                AcceptDownloads = true,
                Locale = "en-US",
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                StorageState = StartSignedIn ? E2ERun.GetSignedInStateAsync(Site).GetAwaiter().GetResult() : null
            };
        }

        [TestInitialize]
        public async Task StartTrace()
        {
            Page.Console += (_, m) => _console.Add($"[{m.Type}] {m.Text}");
            Page.PageError += (_, e) => _console.Add("[pageerror] " + e);
            await Context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Sources = false });
            await StartJsCoverageAsync();
        }

        // ---- host switch for UIOptions:EncodeHtmlContent (LayUI host only) ---------------------------------------------

        private bool _encodingChanged;

        /// <summary>
        /// Switches the test host's UIOptions:EncodeHtmlContent for the rest of the test (the host starts with it on); the original value is
        /// restored when the test ends. Needs the session of the signed in administrator.
        /// </summary>
        protected async Task SetHtmlContentEncodingAsync(bool on)
        {
            _encodingChanged = true;
            var response = await Context.APIRequest.GetAsync("/Lab/HtmlEncoding?on=" + (on ? "true" : "false"));
            Assert.AreEqual(on ? "true" : "false", (await response.TextAsync()).Trim());
        }

        private async Task RestoreHtmlContentEncodingAsync()
        {
            if (_encodingChanged)
            {
                await Context.APIRequest.GetAsync("/Lab/HtmlEncoding?on=true");
            }
        }

        // ---- JS coverage (Chromium only): call counts and block ranges of the scripts the page runs -----------------

        private async Task StartJsCoverageAsync()
        {
            if (Target.CollectJsCoverage == false)
            {
                return;
            }
            _cdp = await Context.NewCDPSessionAsync(Page);
            await _cdp.SendAsync("Profiler.enable");
            await _cdp.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object> { ["callCount"] = true, ["detailed"] = true });
        }

        /// <summary>
        /// Hands the coverage gathered so far to the run wide collector. It is called when the test ends; call it before a step that
        /// replaces the document (a full page reload), because the data of the old document is gone afterwards.
        /// </summary>
        protected async Task FlushJsCoverageAsync()
        {
            if (_cdp == null)
            {
                return;
            }
            try
            {
                var result = await _cdp.SendAsync("Profiler.takePreciseCoverage");
                if (result.HasValue)
                {
                    E2ERun.LayUiScript.Add(result.Value);
                }
            }
            catch (PlaywrightException)
            {
                // the page is already gone; nothing to collect
            }
        }

        [TestCleanup]
        public async Task KeepFailureArtifacts()
        {
            await FlushJsCoverageAsync();
            await RestoreHtmlContentEncodingAsync();
            var failed = TestContext.CurrentTestOutcome != UnitTestOutcome.Passed;
            if (failed == false)
            {
                await Context.Tracing.StopAsync();
                return;
            }
            var dir = Path.Combine(E2ERun.OutputDir, "failures", Sanitize(GetType().Name + "." + TestContext.TestName));
            Directory.CreateDirectory(dir);
            await Context.Tracing.StopAsync(new TracingStopOptions { Path = Path.Combine(dir, "trace.zip") });
            try
            {
                await Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, "screenshot.png"), FullPage = true });
            }
            catch (PlaywrightException)
            {
                // the page may already be gone
            }
            File.WriteAllText(Path.Combine(dir, "browser-console.log"), string.Join(Environment.NewLine, _console), Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, "readme.txt"),
                "trace.zip: open with `pwsh bin/Release/net10.0/playwright.ps1 show-trace trace.zip` or https://trace.playwright.dev" + Environment.NewLine +
                "Server log of the run: " + Site.LogPath + Environment.NewLine);
            TestContext.AddResultFile(Path.Combine(dir, "trace.zip"));
        }

        private static string Sanitize(string name)
        {
            return string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        }
    }
}
