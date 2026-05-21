using System.IO.Compression;
using System.Text.Json;
using GameTrackerPC.Data;
using GameTrackerPC.Models;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerPC.Services;

public sealed class LibraryTransferService
{
    public const string FormatName = "gamevault-library";
    public const int Version = 1;

    private readonly Func<GameVaultDbContext> _dbFactory;

    public LibraryTransferService(Func<GameVaultDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task ExportJsonAsync(string destinationPath)
    {
        var export = await BuildExportAsync(includeImages: false);
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, export.Document, LibraryJson.Options);
    }

    public async Task ExportZipAsync(string destinationPath)
    {
        var export = await BuildExportAsync(includeImages: true);
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        var libraryEntry = archive.CreateEntry("library.json", CompressionLevel.Optimal);
        await using (var entryStream = libraryEntry.Open())
        {
            await JsonSerializer.SerializeAsync(entryStream, export.Document, LibraryJson.Options);
        }

        foreach (var image in export.Images.Where(image => File.Exists(image.LocalPath)))
        {
            archive.CreateEntryFromFile(image.LocalPath, $"images/{image.ArchiveName}", CompressionLevel.Optimal);
        }
    }

    public async Task<LibraryImportResult> ImportJsonAsync(
        string sourcePath,
        Func<ImportConflictInfo, ImportConflictDecision> resolveConflict)
    {
        await using var stream = File.OpenRead(sourcePath);
        var document = await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, LibraryJson.Options)
            ?? throw new InvalidOperationException("library.json is empty.");

        return await ImportDocumentAsync(document, resolveConflict, importImage: null);
    }

    public async Task<LibraryImportResult> ImportZipAsync(
        string sourcePath,
        Func<ImportConflictInfo, ImportConflictDecision> resolveConflict)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        var libraryEntry = archive.GetEntry("library.json")
            ?? throw new InvalidOperationException("ZIP archive must contain library.json.");

        await using var stream = libraryEntry.Open();
        var document = await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, LibraryJson.Options)
            ?? throw new InvalidOperationException("library.json is empty.");

        async Task<string?> ImportImage(string? archiveName, string? localPath)
        {
            var fileName = FirstNonEmptyFileName(archiveName, localPath);
            if (fileName is null)
            {
                return null;
            }

            var entry = archive.Entries.FirstOrDefault(item =>
                item.FullName.Replace('\\', '/').Equals($"images/{fileName}", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return null;
            }

            Directory.CreateDirectory(AppPaths.ImagesDirectory);
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            var destination = Path.Combine(AppPaths.ImagesDirectory, $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
            await using var entryStream = entry.Open();
            await using var output = File.Create(destination);
            await entryStream.CopyToAsync(output);
            return destination;
        }

        return await ImportDocumentAsync(document, resolveConflict, ImportImage);
    }

    private async Task<LibraryImportResult> ImportDocumentAsync(
        LibraryDocument document,
        Func<ImportConflictInfo, ImportConflictDecision> resolveConflict,
        Func<string?, string?, Task<string?>>? importImage)
    {
        ValidateDocument(document);

        await using var db = _dbFactory();
        UpsertReferences(db, document);

        var result = new LibraryImportResult();
        var replaceAll = false;

        foreach (var incoming in document.Games)
        {
            NormalizeIncoming(incoming);

            var existingById = await LoadGameForMutation(db.Games)
                .FirstOrDefaultAsync(game => game.Id == incoming.Id);
            var existingBySignature = existingById is null
                ? await LoadGameForMutation(db.Games).FirstOrDefaultAsync(game =>
                    game.Title == incoming.Title &&
                    game.Year == incoming.Year &&
                    game.PlatformType == incoming.PlatformType)
                : null;
            var existing = existingById ?? existingBySignature;

            if (existing is not null && !replaceAll)
            {
                var decision = resolveConflict(new ImportConflictInfo
                {
                    Existing = existing,
                    Incoming = incoming,
                    Reason = existingById is not null ? "ID" : "title + year + platform"
                });

                if (decision == ImportConflictDecision.Cancel)
                {
                    result.Cancelled = true;
                    break;
                }

                if (decision == ImportConflictDecision.Skip)
                {
                    result.Skipped++;
                    continue;
                }

                replaceAll = decision == ImportConflictDecision.ReplaceAll;
            }

            var imported = await MapImportedGameAsync(incoming, importImage);

            if (existing is null)
            {
                db.Games.Add(imported);
                result.Added++;
            }
            else
            {
                SetGameId(imported, existing.Id);
                db.Games.Remove(existing);
                await db.SaveChangesAsync();
                db.Games.Add(imported);
                result.Replaced++;
            }
        }

        if (!result.Cancelled)
        {
            await db.SaveChangesAsync();
        }

        return result;
    }

    private static IQueryable<Game> LoadGameForMutation(DbSet<Game> games) =>
        games.Include(game => game.Statuses).Include(game => game.ImageGallery);

    private static void ValidateDocument(LibraryDocument document)
    {
        if (!string.Equals(document.Format, FormatName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported format '{document.Format}'.");
        }

        if (document.Version != Version)
        {
            throw new InvalidOperationException($"Unsupported version '{document.Version}'.");
        }
    }

    private static void NormalizeIncoming(GameDto incoming)
    {
        incoming.Id = string.IsNullOrWhiteSpace(incoming.Id) ? Guid.NewGuid().ToString("N") : incoming.Id.Trim();
        incoming.Title = string.IsNullOrWhiteSpace(incoming.Title) ? "Untitled" : incoming.Title.Trim();
        if (incoming.Statuses.Count == 0)
        {
            incoming.Statuses.Add(incoming.Status ?? GameStatus.PLANNED);
        }

        incoming.Statuses = incoming.Statuses.Distinct().ToList();
        incoming.Status = incoming.Statuses.FirstOrDefault();
        incoming.ImageScale = Clamp(incoming.ImageScale <= 0 ? 1 : incoming.ImageScale, 1, 4);
        incoming.ImageOffsetX = Clamp(incoming.ImageOffsetX, -2, 2);
        incoming.ImageOffsetY = Clamp(incoming.ImageOffsetY, -2, 2);

        var now = Clock.UnixMillisecondsNow();
        if (incoming.CreatedAt <= 0)
        {
            incoming.CreatedAt = now;
        }

        if (incoming.UpdatedAt <= 0)
        {
            incoming.UpdatedAt = incoming.CreatedAt;
        }
    }

    private static async Task<Game> MapImportedGameAsync(
        GameDto incoming,
        Func<string?, string?, Task<string?>>? importImage)
    {
        var localCoverPath = importImage is null
            ? null
            : await importImage(incoming.ImageArchiveName, incoming.ImageLocalPath);

        var game = new Game
        {
            Id = incoming.Id,
            Title = incoming.Title,
            Year = incoming.Year,
            PlatformType = incoming.PlatformType,
            PcServiceId = incoming.PcServiceId,
            ConsoleFamilyId = incoming.ConsoleFamilyId,
            ConsoleModelId = incoming.ConsoleModelId,
            ImageLocalPath = localCoverPath,
            ImageArchiveName = FirstNonEmptyFileName(incoming.ImageArchiveName, incoming.ImageLocalPath),
            ImageSourceUrl = incoming.ImageSourceUrl,
            ImageSourceType = incoming.ImageSourceType,
            ImageScale = incoming.ImageScale,
            ImageOffsetX = incoming.ImageOffsetX,
            ImageOffsetY = incoming.ImageOffsetY,
            SourcePageUrl = incoming.SourcePageUrl,
            CustomNotes = GameNotesSerializer.ToStorageJson(incoming.CustomNotes),
            CreatedAt = incoming.CreatedAt,
            UpdatedAt = incoming.UpdatedAt
        };

        foreach (var status in incoming.Statuses.Distinct())
        {
            game.Statuses.Add(new GameStatusEntry { GameId = game.Id, Status = status });
        }

        var galleryItems = incoming.ImageGallery.Take(20).ToList();
        for (var i = 0; i < galleryItems.Count; i++)
        {
            var image = galleryItems[i];
            var archiveName = FirstNonEmptyFileName(image.LocalPath);
            var localPath = importImage is null ? null : await importImage(archiveName, image.LocalPath);
            game.ImageGallery.Add(new GameImage
            {
                GameId = game.Id,
                LocalPath = localPath,
                ArchiveName = archiveName,
                SourceUrl = image.SourceUrl,
                SourceType = image.SourceType,
                SortOrder = i
            });
        }

        return game;
    }

    private static void SetGameId(Game game, string id)
    {
        game.Id = id;
        foreach (var status in game.Statuses)
        {
            status.GameId = id;
        }

        foreach (var image in game.ImageGallery)
        {
            image.GameId = id;
        }
    }

    private async Task<ExportBuildResult> BuildExportAsync(bool includeImages)
    {
        await using var db = _dbFactory();
        var games = await db.Games
            .Include(game => game.Statuses)
            .Include(game => game.ImageGallery)
            .OrderBy(game => game.Title)
            .ToListAsync();
        var usedArchiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exportImages = new List<ExportImageFile>();

        var document = new LibraryDocument
        {
            CreatedAt = Clock.UnixMillisecondsNow(),
            PcServices = await db.PcServices
                .OrderBy(service => service.Name)
                .Select(service => new PcServiceDto
                {
                    Id = service.StableId,
                    Name = service.Name,
                    IsDefault = service.IsBuiltIn
                })
                .ToListAsync(),
            ConsoleFamilies = await db.ConsoleFamilies
                .OrderBy(family => family.Name)
                .Select(family => new ConsoleFamilyDto
                {
                    Id = family.StableId,
                    Name = family.Name,
                    IsDefault = family.IsBuiltIn
                })
                .ToListAsync(),
            ConsoleModels = await db.ConsoleModels
                .OrderBy(model => model.Name)
                .Select(model => new ConsoleModelDto
                {
                    Id = model.StableId,
                    FamilyId = model.FamilyStableId,
                    Name = model.Name,
                    IsDefault = model.IsBuiltIn
                })
                .ToListAsync()
        };

        foreach (var game in games)
        {
            var coverArchiveName = includeImages
                ? RegisterImage(game.ImageLocalPath, game.ImageArchiveName, usedArchiveNames, exportImages)
                : null;

            var dto = new GameDto
            {
                Id = game.Id,
                Title = game.Title,
                Year = game.Year,
                Statuses = game.Statuses.Select(status => status.Status).DefaultIfEmpty(GameStatus.PLANNED).Distinct().ToList(),
                PlatformType = game.PlatformType,
                PcServiceId = game.PcServiceId,
                ConsoleFamilyId = game.ConsoleFamilyId,
                ConsoleModelId = game.ConsoleModelId,
                ImageLocalPath = game.ImageLocalPath,
                ImageArchiveName = coverArchiveName,
                ImageSourceUrl = game.ImageSourceUrl,
                ImageSourceType = game.ImageSourceType,
                ImageScale = game.ImageScale,
                ImageOffsetX = game.ImageOffsetX,
                ImageOffsetY = game.ImageOffsetY,
                SourcePageUrl = game.SourcePageUrl,
                CustomNotes = GameNotesSerializer.FromStorage(game.CustomNotes),
                CreatedAt = game.CreatedAt,
                UpdatedAt = game.UpdatedAt
            };
            dto.Status = dto.Statuses.FirstOrDefault();

            foreach (var image in game.ImageGallery.OrderBy(image => image.SortOrder).Take(20))
            {
                var archiveName = includeImages
                    ? RegisterImage(image.LocalPath, image.ArchiveName, usedArchiveNames, exportImages)
                    : null;

                dto.ImageGallery.Add(new GameImageDto
                {
                    LocalPath = includeImages ? archiveName : image.LocalPath,
                    SourceUrl = image.SourceUrl,
                    SourceType = image.SourceType
                });
            }

            document.Games.Add(dto);
        }

        return new ExportBuildResult { Document = document, Images = exportImages };
    }

    private static void UpsertReferences(GameVaultDbContext db, LibraryDocument document)
    {
        foreach (var service in document.PcServices.Where(service => !string.IsNullOrWhiteSpace(service.Id)))
        {
            var existing = db.PcServices.FirstOrDefault(item => item.StableId == service.Id);
            if (existing is null)
            {
                db.PcServices.Add(new PcService
                {
                    StableId = service.Id,
                    Name = string.IsNullOrWhiteSpace(service.Name) ? service.Id : service.Name,
                    IsBuiltIn = service.IsDefault,
                    CreatedAt = Clock.UnixMillisecondsNow()
                });
            }
            else
            {
                existing.Name = string.IsNullOrWhiteSpace(service.Name) ? existing.Name : service.Name;
                existing.IsBuiltIn = existing.IsBuiltIn || service.IsDefault;
            }
        }

        foreach (var family in document.ConsoleFamilies.Where(family => !string.IsNullOrWhiteSpace(family.Id)))
        {
            var existing = db.ConsoleFamilies.FirstOrDefault(item => item.StableId == family.Id);
            if (existing is null)
            {
                db.ConsoleFamilies.Add(new ConsoleFamily
                {
                    StableId = family.Id,
                    Name = string.IsNullOrWhiteSpace(family.Name) ? family.Id : family.Name,
                    IsBuiltIn = family.IsDefault,
                    CreatedAt = Clock.UnixMillisecondsNow()
                });
            }
            else
            {
                existing.Name = string.IsNullOrWhiteSpace(family.Name) ? existing.Name : family.Name;
                existing.IsBuiltIn = existing.IsBuiltIn || family.IsDefault;
            }
        }

        foreach (var model in document.ConsoleModels.Where(model => !string.IsNullOrWhiteSpace(model.Id)))
        {
            var existing = db.ConsoleModels.FirstOrDefault(item => item.StableId == model.Id);
            if (existing is null)
            {
                db.ConsoleModels.Add(new ConsoleModel
                {
                    StableId = model.Id,
                    FamilyStableId = model.FamilyId,
                    Name = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
                    IsBuiltIn = model.IsDefault,
                    CreatedAt = Clock.UnixMillisecondsNow()
                });
            }
            else
            {
                existing.FamilyStableId = string.IsNullOrWhiteSpace(model.FamilyId) ? existing.FamilyStableId : model.FamilyId;
                existing.Name = string.IsNullOrWhiteSpace(model.Name) ? existing.Name : model.Name;
                existing.IsBuiltIn = existing.IsBuiltIn || model.IsDefault;
            }
        }
    }

    private static string? RegisterImage(
        string? localPath,
        string? preferredArchiveName,
        HashSet<string> usedArchiveNames,
        List<ExportImageFile> exportImages)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        var baseName = FirstNonEmptyFileName(preferredArchiveName, localPath) ?? $"{Guid.NewGuid():N}.jpg";
        var archiveName = MakeUniqueArchiveName(baseName, usedArchiveNames);
        exportImages.Add(new ExportImageFile { LocalPath = localPath, ArchiveName = archiveName });
        return archiveName;
    }

    private static string MakeUniqueArchiveName(string name, HashSet<string> used)
    {
        var cleaned = CleanArchiveName(name);
        var extension = Path.GetExtension(cleaned);
        var baseName = Path.GetFileNameWithoutExtension(cleaned);
        var candidate = cleaned;
        var index = 1;

        while (!used.Add(candidate))
        {
            candidate = $"{baseName}-{index++}{extension}";
        }

        return candidate;
    }

    private static string CleanArchiveName(string name)
    {
        var fileName = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? $"{Guid.NewGuid():N}.jpg" : fileName;
    }

    private static string? FirstNonEmptyFileName(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var fileName = Path.GetFileName(value);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return null;
    }

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
}
