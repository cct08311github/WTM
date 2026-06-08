#nullable enable
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Minimal DbContext that manages the two dashboard EF tables.
/// Hosts call <see cref="DashboardDbContextExtensions.ApplyDashboardModels"/> from their own
/// <c>OnModelCreating</c> override, or register this context directly if they do not have a
/// custom DataContext.
/// </summary>
public class DashboardDbContext : DbContext
{
    public DashboardDbContext(DbContextOptions<DashboardDbContext> options)
        : base(options) { }

    public DbSet<DashboardRecord> DashboardRecords { get; set; } = null!;
    public DbSet<WidgetRecord> WidgetRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyDashboardModels();
    }
}

/// <summary>
/// EF Core ModelBuilder extension — registers Dashboard tables.
/// Call from any <c>OnModelCreating</c> override that should persist dashboards.
/// </summary>
public static class DashboardDbContextExtensions
{
    /// <summary>
    /// Registers DashboardRecords and WidgetRecords tables.
    /// Call this from <c>DbContext.OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder ApplyDashboardModels(this ModelBuilder builder)
    {
        builder.Entity<DashboardRecord>(e =>
        {
            e.ToTable("DashboardRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TenantId).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Owner).HasMaxLength(256).IsRequired();
            e.Property(x => x.SharingMode).HasMaxLength(32).IsRequired();
            e.Property(x => x.SharingRolesJson).HasMaxLength(2048);
            e.Property(x => x.FiltersJson);
            e.Property(x => x.LayoutJson).IsRequired();
            e.Property(x => x.LinksJson);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.Owner);
        });

        builder.Entity<WidgetRecord>(e =>
        {
            e.ToTable("DashboardWidgets");
            e.HasKey(x => new { x.DashboardId, x.WidgetId });
            e.Property(x => x.DashboardId).HasMaxLength(64);
            e.Property(x => x.WidgetId).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(64).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.SourceJson).IsRequired();
            e.Property(x => x.ConfigJson).IsRequired();
            e.Property(x => x.DrillDownJson);
            e.HasOne<DashboardRecord>()
             .WithMany()
             .HasForeignKey(x => x.DashboardId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        return builder;
    }
}
