using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Integration.Test;

/// <summary>
/// FrameworkContext subclass that skips Utils.GetAllModels() assembly scanning.
/// EF Core auto-discovers entities from DbSet properties on FrameworkContext.
/// Used to verify the framework entity model builds correctly on MSSQL.
/// </summary>
public class TestFrameworkContext : FrameworkContext
{
    public TestFrameworkContext(string cs, DBTypeEnum dbtype)
        : base(cs, dbtype)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Intentionally do NOT call base.OnModelCreating().
        // base calls Utils.GetAllModels() which scans all loaded assemblies,
        // causing duplicate entity registration and column conflicts in test environments.
        // EF Core auto-discovers entities from DbSet<> properties inherited from FrameworkContext.
    }
}
