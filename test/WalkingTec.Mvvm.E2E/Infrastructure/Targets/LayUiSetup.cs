using System.Collections.Generic;
using System.IO;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Environment of the LayUI test host. Besides the default connection (index 0, set for every site) the host gets a second
    /// connection <c>other</c> and one read replica <c>other_1</c>, each a SQLite file of its own in the temporary directory of the run,
    /// for the tests of pages with a fixed connection (<c>OtherDbController</c>). The host creates the schema and two synthetic rows
    /// in each file at start-up.
    /// </summary>
    public static class LayUiSetup
    {
        public static void ConfigureEnvironment(SiteProcess site, IDictionary<string, string> env)
        {
            var dir = Path.GetDirectoryName(site.DatabasePath);
            env["Connections__1__Key"] = "other";
            env["Connections__1__Value"] = "Data Source=" + Path.Combine(dir, "e2e-other.db");
            env["Connections__1__DbType"] = "sqlite";
            env["Connections__2__Key"] = "other_1";
            env["Connections__2__Value"] = "Data Source=" + Path.Combine(dir, "e2e-other-replica.db");
            env["Connections__2__DbType"] = "sqlite";
        }
    }
}
