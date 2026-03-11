#nullable enable
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Test.ViewModels;

internal class EtlTestDataContext : EmptyContext
{
    public EtlTestDataContext(string cs, DBTypeEnum dbtype)
        : base(cs, dbtype) { }

    public DbSet<EtlJobDefinition> EtlJobDefinitions { get; set; } = null!;
    public DbSet<EtlRunLog> EtlRunLogs { get; set; } = null!;
}
