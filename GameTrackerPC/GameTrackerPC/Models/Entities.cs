using System.ComponentModel.DataAnnotations.Schema;

namespace GameTrackerPC.Models;

public sealed class Game
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public PlatformType PlatformType { get; set; } = PlatformType.PC;
    public string? PcServiceId { get; set; }
    public string? ConsoleFamilyId { get; set; }
    public string? ConsoleModelId { get; set; }
    public string? ImageLocalPath { get; set; }
    public string? ImageArchiveName { get; set; }
    public string? ImageSourceUrl { get; set; }
    public ImageSourceType ImageSourceType { get; set; } = ImageSourceType.NONE;
    public double ImageScale { get; set; } = 1;
    public double ImageOffsetX { get; set; }
    public double ImageOffsetY { get; set; }
    public string? SourcePageUrl { get; set; }
    public string? CustomNotes { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    public ICollection<GameStatusEntry> Statuses { get; set; } = new List<GameStatusEntry>();
    public ICollection<GameImage> ImageGallery { get; set; } = new List<GameImage>();

    [NotMapped]
    public string StatusText => Statuses.Count == 0
        ? GameStatus.PLANNED.ToString()
        : string.Join(", ", Statuses.Select(status => status.Status));
}

public sealed class GameStatusEntry
{
    public int Id { get; set; }
    public string GameId { get; set; } = string.Empty;
    public GameStatus Status { get; set; } = GameStatus.PLANNED;
    public Game? Game { get; set; }
}

public sealed class GameImage
{
    public int Id { get; set; }
    public string GameId { get; set; } = string.Empty;
    public string? LocalPath { get; set; }
    public string? ArchiveName { get; set; }
    public string? SourceUrl { get; set; }
    public ImageSourceType SourceType { get; set; } = ImageSourceType.GALLERY;
    public int SortOrder { get; set; }
    public Game? Game { get; set; }
}

public sealed class PcService
{
    public int Id { get; set; }
    public string StableId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class ConsoleFamily
{
    public int Id { get; set; }
    public string StableId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class ConsoleModel
{
    public int Id { get; set; }
    public string StableId { get; set; } = string.Empty;
    public string FamilyStableId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
