using System.Net.Http;
using GameTrackerPC.Data;
using GameTrackerPC.Models;
using Microsoft.EntityFrameworkCore;

namespace GameTrackerPC.Services;

public sealed class ImageService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp",
        ".gif"
    };

    private readonly HttpClient _httpClient = CreateHttpClient();

    public string CopyLocalImage(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Image file was not found.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension) || !SupportedExtensions.Contains(extension))
        {
            extension = ".jpg";
        }

        Directory.CreateDirectory(AppPaths.ImagesDirectory);
        var destination = Path.Combine(AppPaths.ImagesDirectory, $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        File.Copy(sourcePath, destination, overwrite: false);
        return destination;
    }

    public async Task<string> DownloadImageAsync(
        string url,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Image URL must be absolute.");
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var extension = GuessExtension(response.Content.Headers.ContentType?.MediaType, uri.LocalPath);
        Directory.CreateDirectory(AppPaths.ImagesDirectory);
        var destination = Path.Combine(AppPaths.ImagesDirectory, $"{Guid.NewGuid():N}{extension}");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(destination);
            await CopyToFileWithProgressAsync(
                stream,
                file,
                response.Content.Headers.ContentLength,
                progress,
                cancellationToken);
        }
        catch
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            throw;
        }

        return destination;
    }

    public void ClearImageCache()
    {
        if (!Directory.Exists(AppPaths.ImagesDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(AppPaths.ImagesDirectory))
        {
            File.Delete(file);
        }
    }

    public async Task<ImageCleanupResult> DeleteUnreferencedImagesAsync(
        GameVaultDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(AppPaths.ImagesDirectory))
        {
            return new ImageCleanupResult(0, 0, 0);
        }

        var referenced = await GetReferencedImagePathsAsync(db, cancellationToken);
        var scanned = 0;
        var deleted = 0;
        var failed = 0;

        foreach (var file in Directory.EnumerateFiles(AppPaths.ImagesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            var normalizedFile = NormalizePath(file);

            if (normalizedFile is null || referenced.Contains(normalizedFile))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
                failed++;
            }
            catch (UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return new ImageCleanupResult(scanned, deleted, failed);
    }

    private static string GuessExtension(string? mediaType, string localPath)
    {
        var extension = mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/jpeg" => ".jpg",
            _ => Path.GetExtension(localPath)
        };

        return string.IsNullOrWhiteSpace(extension) || !SupportedExtensions.Contains(extension)
            ? ".jpg"
            : extension.ToLowerInvariant();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameVault/1.0");
        return client;
    }

    private static async Task<HashSet<string>> GetReferencedImagePathsAsync(
        GameVaultDbContext db,
        CancellationToken cancellationToken)
    {
        var paths = await db.Games
            .Select(game => game.ImageLocalPath)
            .Concat(db.GameImages.Select(image => image.LocalPath))
            .Where(path => path != null && path != string.Empty)
            .ToListAsync(cancellationToken);

        return paths
            .Select(NormalizePath)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
        catch (NotSupportedException)
        {
            return path.Trim();
        }
    }

    private static async Task CopyToFileWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var downloaded = 0L;
        progress?.Report(new DownloadProgress(downloaded, totalBytes));

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            progress?.Report(new DownloadProgress(downloaded, totalBytes));
        }
    }
}

public sealed record ImageCleanupResult(int Scanned, int Deleted, int Failed);
