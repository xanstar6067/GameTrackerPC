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
    public bool IsDefault { get; set; }
}

public sealed class ConsoleFamilyDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public sealed class ConsoleModelDto
{
    public string Id { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public sealed class GameDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public GameStatus? Status { get; set; }
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
    [JsonConverter(typeof(GameNoteListJsonConverter))]
    public List<GameNoteDto> CustomNotes { get; set; } = [];
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class GameImageDto
{
    public string? LocalPath { get; set; }
    public string? SourceUrl { get; set; }
    public ImageSourceType SourceType { get; set; } = ImageSourceType.GALLERY;
}

public sealed class GameNoteDto
{
    public string Category { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
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

internal sealed class GameNoteListJsonConverter : JsonConverter<List<GameNoteDto>>
{
    public override List<GameNoteDto> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            return string.IsNullOrWhiteSpace(text)
                ? []
                : [new GameNoteDto { Category = "Notes", Text = text }];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("customNotes must be an array.");
        }

        var notes = new List<GameNoteDto>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return notes;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            string category = string.Empty;
            string text = string.Empty;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    reader.Skip();
                    continue;
                }

                var propertyName = reader.GetString();
                reader.Read();

                if (string.Equals(propertyName, "category", StringComparison.OrdinalIgnoreCase))
                {
                    category = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;
                }
                else if (string.Equals(propertyName, "text", StringComparison.OrdinalIgnoreCase))
                {
                    text = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;
                }
                else
                {
                    reader.Skip();
                }
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                notes.Add(new GameNoteDto { Category = category, Text = text });
            }
        }

        throw new JsonException("customNotes array was not closed.");
    }

    public override void Write(Utf8JsonWriter writer, List<GameNoteDto> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var note in value.Where(note => !string.IsNullOrWhiteSpace(note.Text)))
        {
            writer.WriteStartObject();
            writer.WriteString("category", note.Category ?? string.Empty);
            writer.WriteString("text", note.Text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

internal static class GameNotesSerializer
{
    public static List<GameNoteDto> FromStorage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            var notes = JsonSerializer.Deserialize<List<GameNoteDto>>(value, LibraryJson.Options);
            if (notes is not null)
            {
                return notes.Where(note => !string.IsNullOrWhiteSpace(note.Text)).ToList();
            }
        }
        catch (JsonException)
        {
        }

        return [new GameNoteDto { Category = "Notes", Text = value }];
    }

    public static string? ToStorageJson(IEnumerable<GameNoteDto> notes)
    {
        var filtered = notes
            .Where(note => !string.IsNullOrWhiteSpace(note.Text))
            .Select(note => new GameNoteDto
            {
                Category = note.Category ?? string.Empty,
                Text = note.Text
            })
            .ToList();

        return filtered.Count == 0 ? null : JsonSerializer.Serialize(filtered, LibraryJson.Options);
    }

    public static string? ToStorageFromPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return ToStorageJson([new GameNoteDto { Category = "Notes", Text = text.Trim() }]);
    }

    public static string ToDisplayText(string? value)
    {
        var notes = FromStorage(value);
        if (notes.Count == 0)
        {
            return string.Empty;
        }

        if (notes.Count == 1 && IsPlainNote(notes[0]))
        {
            return notes[0].Text;
        }

        return string.Join(
            Environment.NewLine,
            notes.Select(note =>
                string.IsNullOrWhiteSpace(note.Category)
                    ? note.Text
                    : $"{note.Category}: {note.Text}"));
    }

    private static bool IsPlainNote(GameNoteDto note) =>
        string.IsNullOrWhiteSpace(note.Category) ||
        string.Equals(note.Category, "Notes", StringComparison.OrdinalIgnoreCase);
}
