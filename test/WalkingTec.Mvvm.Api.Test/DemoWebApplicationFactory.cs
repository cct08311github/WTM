using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Starts the WTM Demo app in-process for full HTTP pipeline integration tests.
///
/// ContentRoot is set to the demo project source directory so that Views,
/// wwwroot, appsettings.json, and demo.db are all resolved correctly.
/// The demo's SyncDb=true will create demo.db on first run if it doesn't exist.
/// </summary>
public class DemoWebApplicationFactory : WebApplicationFactory<WalkingTec.Mvvm.Demo.Program>
{
    private static readonly string DemoProjectDir = FindDemoProjectDir();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(DemoProjectDir);
        builder.UseEnvironment("Development"); // IsQuickDebug=true lives in appsettings.json
    }

    protected override IHostBuilder CreateHostBuilder()
    {
        return WalkingTec.Mvvm.Demo.Program.CreateWebHostBuilder(Array.Empty<string>());
    }

    private static string FindDemoProjectDir()
    {
        // Walk up from the test binary to the repo root (identified by WalkingTec.Mvvm.sln)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WalkingTec.Mvvm.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
            throw new InvalidOperationException(
                "Cannot locate repo root — WalkingTec.Mvvm.sln not found in any parent directory");

        var demoDir = Path.Combine(dir.FullName, "demo", "WalkingTec.Mvvm.Demo");
        if (!Directory.Exists(demoDir))
            throw new InvalidOperationException($"Demo project directory not found: {demoDir}");

        return demoDir;
    }
}
