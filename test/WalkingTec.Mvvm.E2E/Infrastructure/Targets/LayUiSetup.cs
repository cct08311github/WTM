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
        /// <summary>Environment variable that sets <c>FileEndpoints:Mode</c> (Legacy, Audit or Enforce) of the LayUI test host for the whole run.</summary>
        public const string FileEndpointsModeVariable = "E2E_FILE_ENDPOINTS_MODE";

        public static void ConfigureEnvironment(SiteProcess site, IDictionary<string, string> env)
        {
            var dir = Path.GetDirectoryName(site.DatabasePath);
            env["Connections__1__Key"] = "other";
            env["Connections__1__Value"] = "Data Source=" + Path.Combine(dir, "e2e-other.db");
            env["Connections__1__DbType"] = "sqlite";
            env["Connections__2__Key"] = "other_1";
            env["Connections__2__Value"] = "Data Source=" + Path.Combine(dir, "e2e-other-replica.db");
            env["Connections__2__DbType"] = "sqlite";
            // a list view model that the generic data endpoints serve without a page reference (only the tests of that setting use it)
            env["GenericEndpoints__ExemptViewModels__0"] = "WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs.ExemptGadgetListVM";
            // the mode of the file endpoints can be chosen for a whole run: E2E_FILE_ENDPOINTS_MODE=Enforce (default of the product: Audit)
            var fileMode = System.Environment.GetEnvironmentVariable(FileEndpointsModeVariable);
            if (string.IsNullOrWhiteSpace(fileMode) == false)
            {
                env["FileEndpoints__Mode"] = fileMode;
            }
        }
    }
}
