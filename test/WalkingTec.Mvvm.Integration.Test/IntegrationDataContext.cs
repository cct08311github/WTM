using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Integration.Test.Models;

namespace WalkingTec.Mvvm.Integration.Test;

/// <summary>
/// Lightweight DataContext (extends EmptyContext) for basic CRUD and VM integration tests.
/// Does NOT apply multi-tenant global query filters — use TenantFrameworkContext for those.
/// </summary>
public class IntegrationDataContext : EmptyContext
{
    public IntegrationDataContext(string cs, DBTypeEnum dbtype)
        : base(cs, dbtype)
    {
    }

    public DbSet<TenantSchool> TenantSchools { get; set; } = null!;
}
