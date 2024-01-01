using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Persistence;

public class ArchiveDbContext : DbContext
{
    public DbSet<Demo> Demos { get; set; }
    public DbSet<Stv> Stvs { get; set; }
    public DbSet<StvChat> StvChats { get; set; }
    public DbSet<StvUser> StvUsers { get; set; }
    
    // The following configures EF to create a Sqlite database file in the
    // special "local" folder for your platform.
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={ArchivePath.Db}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<Stv>().HasKey(stv => stv.DemoId);
        modelBuilder.Entity<StvChat>().HasKey(chat => new { chat.DemoId, chat.Index });
        modelBuilder.Entity<StvUser>().HasKey(user => new { user.DemoId, user.UserId });
        
        // Configure Stv and StvHeader relationship
        modelBuilder.Entity<Stv>().OwnsOne(stv => stv.Header);

        // Configure Stv and StvChat relationship
        modelBuilder.Entity<Stv>()
            .HasMany(stv => stv.Chats)
            .WithOne(chat => chat.Stv)
            .HasForeignKey(chat => chat.DemoId);

        // Configure Stv and StvUser relationship
        modelBuilder.Entity<Stv>()
            .HasMany(stv => stv.Users)
            .WithOne() // Assuming there is no navigation property in StvUser back to Stv
            .HasForeignKey(user => user.DemoId);

        // Configure Stv and Demo relationship
        // This assumes that each Stv is related to exactly one Demo and vice versa.
        modelBuilder.Entity<Stv>()
            .HasOne(stv => stv.Demo)
            .WithOne() // Replace with the navigation property in Demo if there is one
            .HasForeignKey<Stv>(stv => stv.DemoId);
    }
}