using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Fixtures
{
    /// <summary>
    /// Test only. When the run configures the connections <c>other</c> and its read replica <c>other_1</c> (see the E2E project's
    /// LayUiSetup), the host has already created the schema and the synthetic Gadget rows in each database like in the default one;
    /// this gives the rows a prefix that tells the databases apart.
    /// </summary>
    public static class OtherConnection
    {
        public const string BaseKey = "other";
        public const string ReplicaKey = "other_1";

        public static void Prepare(Configs configs)
        {
            foreach (var cs in configs.Connections.Where(x => x.Key == BaseKey || x.Key == ReplicaKey))
            {
                var prefix = cs.Key == BaseKey ? "Base " : "Replica ";
                using var dc = new DataContext(cs);
                dc.Database.EnsureCreated();
                foreach (var gadget in dc.Set<Gadget>().ToList().Where(x => x.Name.StartsWith(prefix) == false))
                {
                    gadget.Name = prefix + gadget.Name;
                }
                dc.SaveChanges();
            }
        }
    }
}
