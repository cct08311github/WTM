#nullable enable
using Microsoft.EntityFrameworkCore;
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
        // #862: ApplyEtlModels(this) both registers the ETL tables AND applies the ITenant
        // query filter for every ETL entity that implements it -- see that method's remarks in
        // WalkingTec.Mvvm.Etl/ServiceCollectionExtensions.cs for why passing the context
        // instance is required. This used to be done by hand here (a workaround the doc
        // comment attributed to Utils.GetAllModels()'s static cache); the real cause was
        // ApplyEtlModels()'s zero-arg overload never being able to reach a context instance at
        // all, and the hand-applied filter only ever covered EtlJobDefinition.
        modelBuilder.ApplyEtlModels(this);
    }
}
