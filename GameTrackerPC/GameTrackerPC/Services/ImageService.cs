using System.Net.Http;
using GameTrackerPC.Models;

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

    public async Task<string> DownloadImageAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Image URL must be absolute.");
        }

        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var extension = GuessExtension(response.Content.Headers.ContentType?.MediaType, uri.LocalPath);
        Directory.CreateDirectory(AppPaths.ImagesDirectory);
        var destination = Path.Combine(AppPaths.ImagesDirectory, $"{Guid.NewGuid():N}{extension}");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, cancellationToken);
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
}
