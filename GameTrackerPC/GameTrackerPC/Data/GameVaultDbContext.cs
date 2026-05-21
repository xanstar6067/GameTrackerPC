using GameTrackerPC.Models;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerPC.Data;

public sealed class GameVaultDbContext : DbContext
{
    private readonly string _databasePath;

    public GameVaultDbContext(string databasePath)
    {
        _databasePath = databasePath;
    }

    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameStatusEntry> GameStatuses => Set<GameStatusEntry>();
    public DbSet<GameImage> GameImages => Set<GameImage>();
    public DbSet<PcService> PcServices => Set<PcService>();
    public DbSet<ConsoleFamily> ConsoleFamilies => Set<ConsoleFamily>();
    public DbSet<ConsoleModel> ConsoleModels => Set<ConsoleModel>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_databasePath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Game>(entity =>
        {
            entity.HasKey(game => game.Id);
            entity.Property(game => game.Title).HasMaxLength(400);
            entity.Property(game => game.PlatformType).HasConversion<string>();
            entity.Property(game => game.ImageSourceType).HasConversion<string>();
            entity.HasIndex(game => new { game.Title, game.Year, game.PlatformType });
            entity.HasMany(game => game.Statuses)
                .WithOne(status => status.Game)
                .HasForeignKey(status => status.GameId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(game => game.ImageGallery)
                .WithOne(image => image.Game)
                .HasForeignKey(image => image.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameStatusEntry>(entity =>
        {
            entity.Property(status => status.Status).HasConversion<string>();
            entity.HasIndex(status => new { status.GameId, status.Status }).IsUnique();
        });

        modelBuilder.Entity<GameImage>(entity =>
        {
            entity.Property(image => image.SourceType).HasConversion<string>();
            entity.HasIndex(image => image.GameId);
        });

        modelBuilder.Entity<PcService>(entity =>
        {
            entity.HasIndex(service => service.StableId).IsUnique();
            entity.Property(service => service.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<ConsoleFamily>(entity =>
        {
            entity.HasIndex(family => family.StableId).IsUnique();
            entity.Property(family => family.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<ConsoleModel>(entity =>
        {
            entity.HasIndex(model => model.StableId).IsUnique();
            entity.HasIndex(model => model.FamilyStableId);
            entity.Property(model => model.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(120);
        });
    }
}
