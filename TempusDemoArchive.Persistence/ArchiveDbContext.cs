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
    public DbSet<StvSpawn> StvSpawns { get; set; }
    public DbSet<StvTeamChange> StvTeamChanges { get; set; }
    public DbSet<StvDeath> StvDeaths { get; set; }
    public DbSet<StvPause> StvPauses { get; set; }
    
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
        modelBuilder.Entity<StvSpawn>().HasKey(spawn => new { spawn.DemoId, spawn.Index });
        modelBuilder.Entity<StvTeamChange>().HasKey(change => new { change.DemoId, change.Index });
        modelBuilder.Entity<StvDeath>().HasKey(death => new { death.DemoId, death.Index });
        modelBuilder.Entity<StvPause>().HasKey(pause => new { pause.DemoId, pause.Index });
        
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

        modelBuilder.Entity<Stv>()
            .HasMany(stv => stv.Spawns)
            .WithOne(spawn => spawn.Stv)
            .HasForeignKey(spawn => spawn.DemoId);

        modelBuilder.Entity<Stv>()
            .HasMany(stv => stv.TeamChanges)
            .WithOne(change => change.Stv)
            .HasForeignKey(change => change.DemoId);

        modelBuilder.Entity<Stv>()
            .HasMany(stv => stv.Deaths)
            .WithOne(death => death.Stv)
            .HasForeignKey(death => death.DemoId);

        modelBuilder.Entity<Stv>()
            .HasMany(stv => stv.Pauses)
            .WithOne(pause => pause.Stv)
            .HasForeignKey(pause => pause.DemoId);

        // Configure Stv and Demo relationship
        // This assumes that each Stv is related to exactly one Demo and vice versa.
        modelBuilder.Entity<Stv>()
            .HasOne(stv => stv.Demo)
            .WithOne() // Replace with the navigation property in Demo if there is one
            .HasForeignKey<Stv>(stv => stv.DemoId);
        
        // Configure Demo and Stv relationship
        // This assumes that each Stv is related to exactly one Demo and vice versa.
        modelBuilder.Entity<Demo>()
            .HasOne(demo => demo.Stv)
            .WithOne(stv => stv.Demo)
            .HasForeignKey<Stv>(stv => stv.DemoId);

        modelBuilder.Entity<StvUser>()
            .HasIndex(user => user.SteamId64);
        modelBuilder.Entity<StvChat>()
            .HasIndex(chat => chat.FromUserId);
        modelBuilder.Entity<StvChat>()
            .HasIndex(chat => chat.Tick);
    }
}
