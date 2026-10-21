using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Describes a web site the E2E tests can run against. A new target is another static instance in
    /// <see cref="SiteTargets"/> plus its route exclusion file; the process start, hit recording and report code is shared.
    /// A site that has no E2E code of its own (the Vue3Demo) gets the route instrumentation through a hosting startup
    /// assembly, which <see cref="ConfigureEnvironment"/> activates by environment variables.
    /// </summary>
    public sealed class SiteTarget
    {
        /// <summary>Short id used in file names of reports and logs.</summary>
        public string Name { get; init; }

        /// <summary>Project directory relative to the repository root.</summary>
        public string ProjectDir { get; init; }

        /// <summary>Assembly name of the site (the entry dll is {AssemblyName}.dll).</summary>
        public string AssemblyName { get; init; }

        public string TargetFramework { get; init; } = "net10.0";

        /// <summary>File name of the route exclusion list, copied next to the test assembly.</summary>
        public string RouteExclusionFile { get; init; }

        /// <summary>Path of the page that must answer 200 before the tests start.</summary>
        public string ReadyPath { get; init; } = "/";

        /// <summary>True when the tests collect Chromium JS coverage of the LayUI framework script (/_js/framework_layui.js) against this site.</summary>
        public bool CollectJsCoverage { get; init; }

        /// <summary>Account the tests sign in with. The password is generated for every run (see <see cref="SiteProcess.AdminPassword"/>).</summary>
        public string AdminAccount { get; init; } = "e2eadmin";

        /// <summary>Adds target specific environment variables for the site process (called before it starts).</summary>
        public Action<SiteProcess, IDictionary<string, string>> ConfigureEnvironment { get; init; }

        /// <summary>Runs once the site answers, before the first test; used to install the generated admin password.</summary>
        public Func<SiteProcess, Task> AfterStartAsync { get; init; }

        public string RepoRoot => AssemblyMetadata("E2E.RepoRoot") ?? FindRepoRoot();

        public string Configuration => AssemblyMetadata("E2E.Configuration") ?? "Debug";

        public string WorkingDirectory => Path.Combine(RepoRoot, ProjectDir);

        public string HostDll => Path.Combine(WorkingDirectory, "bin", Configuration, TargetFramework, AssemblyName + ".dll");

        private static string AssemblyMetadata(string key)
        {
            return typeof(SiteTarget).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(x => x.Key == key).Select(x => x.Value).FirstOrDefault();
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && File.Exists(Path.Combine(dir.FullName, "WalkingTec.Mvvm.sln")) == false)
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("Cannot locate the repository root (WalkingTec.Mvvm.sln).");
        }
    }
}
