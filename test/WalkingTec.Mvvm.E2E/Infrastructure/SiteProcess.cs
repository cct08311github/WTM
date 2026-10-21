using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Runs a site under test in its own process on a random loopback port with a throw-away SQLite database.
    /// Everything the run needs (admin password, signing key, database) is generated here and never written to disk
    /// outside the temporary directory of the run.
    /// </summary>
    public sealed class SiteProcess : IDisposable
    {
        private static readonly Regex Listening = new Regex(@"Now listening on:\s*(http://\S+)", RegexOptions.Compiled);

        private readonly Process _process;
        private readonly StreamWriter _log;
        private readonly string _tempDir;

        public SiteTarget Target { get; }
        public Uri BaseUri { get; private set; }
        public string AdminAccount { get; }
        public string AdminPassword { get; }
        public string LogPath { get; }
        public string InventoryPath { get; }
        public string HitLogPath { get; }

        private SiteProcess(SiteTarget target, string outputDir)
        {
            Target = target;
            AdminAccount = "e2eadmin";
            AdminPassword = RandomToken(18);
            _tempDir = Path.Combine(Path.GetTempPath(), "wtm-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var serverDir = Path.Combine(outputDir, "server");
            var coverageDir = Path.Combine(outputDir, "route-coverage");
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(coverageDir);
            LogPath = Path.Combine(serverDir, target.Name + ".log");
            InventoryPath = Path.Combine(coverageDir, target.Name + ".inventory.json");
            HitLogPath = Path.Combine(coverageDir, target.Name + ".hits.jsonl");
            _log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

            if (File.Exists(target.HostDll) == false)
            {
                throw new FileNotFoundException("The site under test has not been built: " + target.HostDll);
            }
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = target.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(target.HostDll);
            psi.ArgumentList.Add("--urls");
            psi.ArgumentList.Add("http://127.0.0.1:0");
            var env = psi.Environment;
            env["ASPNETCORE_ENVIRONMENT"] = "Production";
            env["DOTNET_NOLOGO"] = "1";
            env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            env["Connections__0__Value"] = "Data Source=" + Path.Combine(_tempDir, "e2e.db");
            env["JwtOptions__SecurityKey"] = RandomToken(48);
            env["E2E__AdminPassword"] = AdminPassword;
            env["E2E__RouteInventoryPath"] = InventoryPath;
            env["E2E__RouteHitLogPath"] = HitLogPath;

            _process = new Process { StartInfo = psi };
        }

        public static async Task<SiteProcess> StartAsync(SiteTarget target, string outputDir, TimeSpan timeout)
        {
            var site = new SiteProcess(target, outputDir);
            try
            {
                await site.RunAsync(timeout);
                return site;
            }
            catch
            {
                site.Dispose();
                throw;
            }
        }

        private async Task RunAsync(TimeSpan timeout)
        {
            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            DataReceivedEventHandler onLine = (s, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }
                lock (_log)
                {
                    _log.WriteLine(e.Data);
                }
                var m = Listening.Match(e.Data);
                if (m.Success)
                {
                    listening.TrySetResult(new Uri(m.Groups[1].Value));
                }
            };
            _process.OutputDataReceived += onLine;
            _process.ErrorDataReceived += onLine;
            _process.EnableRaisingEvents = true;
            _process.Exited += (s, e) => listening.TrySetException(new InvalidOperationException("The site process exited early with code " + _process.ExitCode + ", see " + LogPath));
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeout);
            using var reg = cts.Token.Register(() => listening.TrySetException(new TimeoutException("The site did not start within " + timeout + ", see " + LogPath)));
            BaseUri = await listening.Task;

            using var http = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(10) };
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    var res = await http.GetAsync(Target.ReadyPath, cts.Token);
                    if (res.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                }
                await Task.Delay(200, cts.Token);
            }
            // the inventory is written once the host has started; wait for it so the report never races it
            while (File.Exists(InventoryPath) == false)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(100, cts.Token);
            }
        }

        /// <summary>
        /// Reads the route hit log of this run (written by the host's recording filter) and fails when a route is missing from it.
        /// The coverage report is built from this file, so a hit that was executed but not recorded would show up as a gap there.
        /// </summary>
        public async System.Threading.Tasks.Task AssertRouteHitAsync(params string[] keys)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            string[] missing;
            do
            {
                var text = string.Empty;
                using (var stream = new FileStream(HitLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    text = await reader.ReadToEndAsync();
                }
                var recorded = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("key").GetString())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                missing = keys.Where(k => recorded.Contains(k) == false).ToArray();
                if (missing.Length == 0)
                {
                    return;
                }
                await System.Threading.Tasks.Task.Delay(100);
            }
            while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("The route hit log does not contain: " + string.Join(", ", missing));
        }

        public void Dispose()
        {
            try
            {
                if (_process.HasExited == false)
                {
                    _process.Kill(true);
                    _process.WaitForExit(10000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            lock (_log)
            {
                _log.Dispose();
            }
            _process.Dispose();
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch (IOException)
            {
            }
        }

        private static string RandomToken(int bytes)
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', 'a').Replace('/', 'b').TrimEnd('=');
        }
    }
}
