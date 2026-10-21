using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Describes a web site the E2E tests can run against. Adding a second target (for example the Vue3Demo)
    /// means adding another static instance in <see cref="SiteTargets"/> plus its route exclusion file; the
    /// process start, hit recording and report code is shared.
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
