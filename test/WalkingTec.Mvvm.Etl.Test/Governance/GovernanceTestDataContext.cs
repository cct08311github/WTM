#nullable enable
using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Test.Governance;

/// <summary>
/// SQLite-backed test DataContext that registers all ETL governance tables
/// (EtlJobDefinitions, EtlRunLogs, EtlDeadLetterRows, EtlLineageRecords).
/// Used for ETL-004/005/006 integration tests.
/// </summary>
internal class GovernanceTestDataContext : EmptyContext
{
    public GovernanceTestDataContext(string cs, DBTypeEnum dbtype)
        : base(cs, dbtype) { }

    public DbSet<EtlJobDefinition>  EtlJobDefinitions { get; set; } = null!;
    public DbSet<EtlRunLog>         EtlRunLogs        { get; set; } = null!;
    public DbSet<EtlDeadLetterRow>  EtlDeadLetterRows { get; set; } = null!;
    public DbSet<EtlLineageRecord>  EtlLineageRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyEtlModels();

        // Explicitly apply the ITenant global query filter for EtlJobDefinition.
        // Utils.GetAllModels() relies on a static cache that may not include
        // EtlJobDefinition when tests run in isolation; applying it here ensures
        // tenant isolation works correctly regardless of assembly scan order.
        var pe = Expression.Parameter(typeof(EtlJobDefinition));
        var exp = Expression.Equal(
            Expression.Property(pe, "TenantCode"),
            Expression.PropertyOrField(Expression.Constant(this), "TenantCode"));
        var lambda = Expression.Lambda<Func<EtlJobDefinition, bool>>(exp, pe);
        modelBuilder.Entity<EtlJobDefinition>().HasQueryFilter(lambda);
    }
}
