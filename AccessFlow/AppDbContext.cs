using Microsoft.EntityFrameworkCore;

namespace AccessFlow;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AccessRequest> Requests => Set<AccessRequest>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLogEntry>().ToTable("AuditLog");
        modelBuilder.Entity<AccessRequest>().ToTable("Requests");
        modelBuilder.Entity<Room>().ToTable("Rooms");
    }
}