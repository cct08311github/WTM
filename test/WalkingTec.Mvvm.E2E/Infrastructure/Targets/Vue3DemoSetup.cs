using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Everything that makes demo/WalkingTec.Mvvm.Vue3Demo drivable without changing it:
    /// <list type="bullet">
    /// <item>configuration only through environment variables (SQLite file, JWT key, no CORS wildcard);</item>
    /// <item>route instrumentation and the built SPA come from the hosting startup assembly
    /// test/WalkingTec.Mvvm.E2E.Instrumentation, loaded with ASPNETCORE_HOSTINGSTARTUPASSEMBLIES. The demo does not
    /// reference that assembly, so DOTNET_STARTUP_HOOKS (full path) makes the runtime load it;</item>
    /// <item>the demo seeds its administrator with a fixed, publicly documented password in DataContext.DataInit.
    /// The test run replaces it with a random one right after the site is up (see <see cref="InstallAdminPasswordAsync"/>).</item>
    /// </list>
    /// </summary>
    internal static class Vue3DemoSetup
    {
        public const string HostingStartupAssembly = "WalkingTec.Mvvm.E2E.Instrumentation";

        /// <summary>
        /// Directory with the SPA build for the tests: ClientApp/dist-e2e, or the E2E_SPA_ROOT environment variable.
        /// It is built with VITE_API_URL empty so that the SPA calls the site it was loaded from instead of the
        /// development address compiled into the regular build (see FORK.md).
        /// </summary>
        public static string SpaRoot(SiteTarget target)
        {
            var env = Environment.GetEnvironmentVariable("E2E_SPA_ROOT");
            return string.IsNullOrWhiteSpace(env) ? Path.Combine(target.WorkingDirectory, "ClientApp", "dist-e2e") : Path.GetFullPath(env);
        }

        public static void ConfigureEnvironment(SiteTarget target, SiteProcess site, IDictionary<string, string> env)
        {
            var spaRoot = SpaRoot(target);
            VerifySpaBuild(spaRoot);
            var startup = Path.Combine(target.RepoRoot, "test", HostingStartupAssembly, "bin", target.Configuration, target.TargetFramework, HostingStartupAssembly + ".dll");
            if (File.Exists(startup) == false)
            {
                throw new FileNotFoundException("The hosting startup assembly has not been built: " + startup);
            }
            env["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = HostingStartupAssembly;
            env["DOTNET_STARTUP_HOOKS"] = startup;
            env["E2E__SpaRoot"] = spaRoot;
            // the SPA and the API are served from the same origin; no cross origin access is needed (and the demo's wildcard setting is not wanted)
            env["CorsOptions__EnableAll"] = "false";
            env["IsQuickDebug"] = "false";
            // the mode of the file endpoints can be chosen for a whole run: E2E_FILE_ENDPOINTS_MODE=Enforce (default of the product: Audit)
            var fileMode = Environment.GetEnvironmentVariable(LayUiSetup.FileEndpointsModeVariable);
            if (string.IsNullOrWhiteSpace(fileMode) == false)
            {
                env["FileEndpoints__Mode"] = fileMode.Trim();
            }
        }

        /// <summary>
        /// Fails early, with the command to run, when the SPA was not built or was built for the development address.
        /// </summary>
        private static void VerifySpaBuild(string spaRoot)
        {
            var index = Path.Combine(spaRoot, "index.html");
            if (File.Exists(index) == false)
            {
                throw new DirectoryNotFoundException("The Vue3Demo SPA is not built: " + index + " is missing. From demo/WalkingTec.Mvvm.Vue3Demo/ClientApp run: npm ci && VITE_API_URL= npm run build -- --outDir dist-e2e");
            }
            var assets = Path.Combine(spaRoot, "assets", "js");
            if (Directory.Exists(assets) && Directory.EnumerateFiles(assets, "*.js").Any(f => File.ReadAllText(f).Contains("localhost:8598")))
            {
                throw new InvalidOperationException("The Vue3Demo SPA in " + spaRoot + " was built with the development API address (http://localhost:8598). Rebuild with: VITE_API_URL= npm run build -- --outDir dist-e2e");
            }
        }

        /// <summary>
        /// Replaces the password of the seeded administrator with the one generated for this run. The demo stores the legacy
        /// format (MD5, upper case hex) when no stronger password algorithm is configured, which is the case here.
        /// A login with the new password in the first test proves that the format matches.
        /// </summary>
        public static async Task InstallAdminPasswordAsync(SiteProcess site)
        {
            var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(site.AdminPassword)));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = site.DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE FrameworkUsers SET Password = $hash WHERE ITCode = $account";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$account", site.AdminAccount);
            var rows = await command.ExecuteNonQueryAsync();
            if (rows != 1)
            {
                throw new InvalidOperationException("The seeded administrator '" + site.AdminAccount + "' was not found in the Vue3Demo database; the seed of DataContext.DataInit may have changed.");
            }
        }
    }
}
