using Elsa.Persistence.EntityFramework.Core;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Mvc.Helper
{
    public class WtmElsaContext : ElsaContext
    {
        public WtmElsaContext(DbContextOptions options) : base(options)
        {
        }

        public override string Schema
        {
            get
            {
                // MySQL and Oracle do not support schemas the way SQL Server does.
                // MySQL treats schema = database, so returning a schema prefix like "Elsa"
                // would cause EF Core to look for a different database and fail to create tables.
                if (Database.IsOracle() || Database.ProviderName?.Contains("MySql") == true)
                {
                    return null;
                }
                else
                {
                    return base.Schema;
                }
            }
        }
    }
}
