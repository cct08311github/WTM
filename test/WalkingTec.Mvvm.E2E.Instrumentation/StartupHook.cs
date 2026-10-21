using System;
using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// Runtime startup hook (DOTNET_STARTUP_HOOKS=full path of this assembly). The site under test does not reference this
/// assembly, so ASP.NET Core's hosting startup loader (ASPNETCORE_HOSTINGSTARTUPASSEMBLIES) could not find it by name.
/// The runtime loads a startup hook from the given path, which makes the assembly available for the by-name load,
/// and the resolving handler below covers the case where it is looked up before that.
/// The type must be named StartupHook, without a namespace, as the runtime requires.
/// </summary>
internal class StartupHook
{
    public static void Initialize()
    {
        var self = typeof(StartupHook).Assembly;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            string.Equals(name.Name, self.GetName().Name, StringComparison.OrdinalIgnoreCase) ? self : null;
    }
}
