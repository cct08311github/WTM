using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Integration.Test.Models;

namespace WalkingTec.Mvvm.Integration.Test;

/// <summary>
/// FrameworkContext subclass for multi-tenant integration tests.
/// Overrides OnModelCreating to register only TenantSchool and apply
/// tenant global query filter, avoiding Utils.GetAllModels() assembly scanning.
/// </summary>
public class TenantFrameworkContext : FrameworkContext
{
    public TenantFrameworkContext(string cs, DBTypeEnum dbtype)
        : base(cs, dbtype)
    {
    }

    public DbSet<TenantSchool> TenantSchools { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Do NOT call base.OnModelCreating — it calls Utils.GetAllModels()
        // which scans all loaded assemblies and causes entity conflicts.
        // Manually apply tenant global query filter matching FrameworkContext's pattern.
        ParameterExpression pe = Expression.Parameter(typeof(TenantSchool));
        var tenantFilter = Expression.Equal(
            Expression.Property(pe, nameof(ITenant.TenantCode)),
            Expression.PropertyOrField(Expression.Constant(this), nameof(TenantCode)));
        modelBuilder.Entity<TenantSchool>()
            .HasQueryFilter(Expression.Lambda<Func<TenantSchool, bool>>(tenantFilter, pe));
    }
}
