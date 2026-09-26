using Clean4ork.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Establishment> Establishments => Set<Establishment>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<Violation> Violations => Set<Violation>();
    public DbSet<InspectionEvent> InspectionEvents => Set<InspectionEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");

        modelBuilder.Entity<Establishment>(e =>
        {
            e.HasIndex(x => new { x.Jurisdiction, x.SourceFacilityId }).IsUnique();
            e.HasIndex(x => new { x.Jurisdiction, x.Slug }).IsUnique();
            e.HasIndex(x => x.PostalCode);
            e.HasIndex(x => x.DataTier);
            e.Property(x => x.DataTier).HasConversion<string>();
            e.Property(x => x.Location).HasColumnType("geometry(Point, 4326)");

            e.HasMany(x => x.Inspections)
                .WithOne(x => x.Establishment)
                .HasForeignKey(x => x.EstablishmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Inspection>(e =>
        {
            e.HasIndex(x => new { x.EstablishmentId, x.SourceInspectionId }).IsUnique();
            e.HasIndex(x => x.InspectionDate);

            e.HasMany(x => x.Violations)
                .WithOne(x => x.Inspection)
                .HasForeignKey(x => x.InspectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Violation>(e =>
        {
            e.HasIndex(x => x.Code);
        });

        modelBuilder.Entity<InspectionEvent>(e =>
        {
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.AlertedAt);
        });
    }
}
