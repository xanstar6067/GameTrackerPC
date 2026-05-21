using System.Text.Json;
using GameTrackerPC.Models;
using System.Text.Json.Serialization;

namespace GameTrackerPC.Services;

public sealed class LibraryDocument
{
    public string Format { get; set; } = LibraryTransferService.FormatName;
    public int Version { get; set; } = LibraryTransferService.Version;
    public long CreatedAt { get; set; }
    public List<PcServiceDto> PcServices { get; set; } = [];
    public List<ConsoleFamilyDto> ConsoleFamilies { get; set; } = [];
    public List<ConsoleModelDto> ConsoleModels { get; set; } = [];
    public Dictionary<string, JsonElement> Themes { get; set; } = [];
    public List<GameDto> Games { get; set; } = [];
}

public sealed class PcServiceDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class ConsoleFamilyDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class ConsoleModelDto
{
    public string Id { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class GameDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public List<GameStatus> Statuses { get; set; } = [];
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
    public List<GameImageDto> ImageGallery { get; set; } = [];
    public string? SourcePageUrl { get; set; }
    public string? CustomNotes { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class GameImageDto
{
    public string? LocalPath { get; set; }
    public string? ArchiveName { get; set; }
    public string? SourceUrl { get; set; }
    public ImageSourceType SourceType { get; set; } = ImageSourceType.GALLERY;
}

public enum ImportConflictDecision
{
    Replace,
    ReplaceAll,
    Skip,
    Cancel
}

public sealed class ImportConflictInfo
{
    public required Game Existing { get; init; }
    public required GameDto Incoming { get; init; }
    public required string Reason { get; init; }
}

public sealed class LibraryImportResult
{
    public int Added { get; set; }
    public int Replaced { get; set; }
    public int Skipped { get; set; }
    public bool Cancelled { get; set; }

    public string Summary =>
        Cancelled
            ? $"Import cancelled. Added: {Added}, replaced: {Replaced}, skipped: {Skipped}."
            : $"Import complete. Added: {Added}, replaced: {Replaced}, skipped: {Skipped}.";
}

internal sealed class ExportImageFile
{
    public required string LocalPath { get; init; }
    public required string ArchiveName { get; init; }
}

internal sealed class ExportBuildResult
{
    public required LibraryDocument Document { get; init; }
    public required List<ExportImageFile> Images { get; init; }
}

internal static class LibraryJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
