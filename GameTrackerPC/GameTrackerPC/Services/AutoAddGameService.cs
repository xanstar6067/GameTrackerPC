using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTrackerPC.Models;

namespace GameTrackerPC.Services;

public sealed class AutoAddGameService
{
    private const string SteamServiceId = "steam";
    private const string EpicServiceId = "epic-games-store";
    private const string GogServiceId = "gog";

    private readonly HttpClient _httpClient = CreateHttpClient();

    public IReadOnlyList<AutoAddSource> SupportedSources { get; } =
    [
        AutoAddSource.Steam,
        AutoAddSource.Gog,
        AutoAddSource.Epic
    ];

    public async Task<AutoAddGameDetails> FetchGameDetailsAsync(
        AutoAddRequest request,
        CancellationToken cancellationToken = default)
    {
        return request.Source switch
        {
            AutoAddSource.Steam => await FetchSteamDetailsAsync(
                ExtractSteamAppId(request.AccountReference),
                cancellationToken),
            AutoAddSource.Gog => await FetchGogDetailsAsync(
                ExtractStoreSlug(request.AccountReference, "game", "GOG"),
                cancellationToken),
            AutoAddSource.Epic => await FetchEpicDetailsAsync(
                ExtractStoreSlug(request.AccountReference, "p", "Epic Games Store"),
                cancellationToken),
            _ => throw new InvalidOperationException("Unsupported auto import source.")
        };
    }

    private async Task<AutoAddGameDetails> FetchSteamDetailsAsync(string appId, CancellationToken cancellationToken)
    {
        using var root = await FetchJsonAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=us&l=russian",
            "Steam",
            $"Check AppID {appId}.",
            cancellationToken);

        if (!root.RootElement.TryGetProperty(appId, out var appRoot) ||
            !GetBool(appRoot, "success") ||
            !appRoot.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException($"Steam did not return game data for AppID {appId}.");
        }

        var type = GetString(data, "type");
        if (!string.Equals(type, "game", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This Steam reference does not point to a game.");
        }

        var title = GetString(data, "name").Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException($"Steam did not return a title for AppID {appId}.");
        }

        var imageUrls = new List<string>();
        imageUrls.AddRange(await FetchSteamVerticalImageUrlsAsync(appId, cancellationToken));
        AddIfNotBlank(imageUrls, GetString(data, "header_image"));

        if (data.TryGetProperty("screenshots", out var screenshots) &&
            screenshots.ValueKind == JsonValueKind.Array)
        {
            foreach (var screenshot in screenshots.EnumerateArray())
            {
                AddIfNotBlank(imageUrls, GetString(screenshot, "path_full"));
            }
        }

        var year = default(int?);
        if (data.TryGetProperty("release_date", out var releaseDate) &&
            releaseDate.ValueKind == JsonValueKind.Object)
        {
            year = ExtractYear(GetString(releaseDate, "date"));
        }

        return new AutoAddGameDetails(
            AutoAddSource.Steam,
            title,
            year,
            DistinctUrls(imageUrls),
            $"https://store.steampowered.com/app/{appId}/",
            SteamServiceId);
    }

    private async Task<IReadOnlyList<string>> FetchSteamVerticalImageUrlsAsync(
        string appId,
        CancellationToken cancellationToken)
    {
        var urls = new List<string>();

        try
        {
            var requestJson = JsonSerializer.Serialize(new
            {
                ids = new[] { new { appid = appId } },
                context = new { country_code = "US" },
                data_request = new { include_assets = true }
            });
            using var root = await FetchJsonAsync(
                "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" +
                Uri.EscapeDataString(requestJson),
                "Steam",
                $"Steam did not return library images for AppID {appId}.",
                cancellationToken);

            var assets = TryGetFirstStoreItem(root.RootElement, out var storeItem) &&
                storeItem.TryGetProperty("assets", out var storeAssets)
                ? storeAssets
                : default;

            if (assets.ValueKind == JsonValueKind.Object)
            {
                var assetUrl = SteamStoreAssetUrl(
                    GetString(assets, "asset_url_format"),
                    GetString(assets, "library_capsule_2x"));
                AddIfNotBlank(urls, assetUrl);
            }
        }
        catch
        {
            // The appdetails endpoint is enough to import a game; this call only improves the cover choice.
        }

        urls.Add(SteamVerticalImageUrl(appId, highResolution: true));
        urls.Add(SteamVerticalImageUrl(appId, highResolution: false));
        return DistinctUrls(urls);
    }

    private async Task<AutoAddGameDetails> FetchEpicDetailsAsync(string slug, CancellationToken cancellationToken)
    {
        using var root = await FetchJsonAsync(
            $"https://store-content.ak.epicgames.com/api/en-US/content/products/{slug}",
            "Epic Games Store",
            $"The public Epic content API did not find slug '{slug}'.",
            cancellationToken);

        var page = FindEpicProductPage(root.RootElement);
        var pageData = page?.TryGetProperty("data", out var data) == true ? data : default;
        var about = pageData.ValueKind == JsonValueKind.Object &&
            pageData.TryGetProperty("about", out var aboutData)
            ? aboutData
            : default;
        var hero = pageData.ValueKind == JsonValueKind.Object &&
            pageData.TryGetProperty("hero", out var heroData)
            ? heroData
            : default;

        var title = GetString(root.RootElement, "productName")
            .IfBlank(page is null ? string.Empty : GetString(page.Value, "productName"))
            .IfBlank(GetString(about, "title"))
            .Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException($"Epic Games Store did not return a title for slug '{slug}'.");
        }

        var imageUrls = new List<string>();
        if (about.ValueKind == JsonValueKind.Object &&
            about.TryGetProperty("image", out var aboutImage))
        {
            AddIfNotBlank(imageUrls, GetString(aboutImage, "src"));
        }

        AddIfNotBlank(imageUrls, GetString(hero, "portraitBackgroundImageUrl"));
        if (root.RootElement.TryGetProperty("_images_", out var images) &&
            images.ValueKind == JsonValueKind.Array)
        {
            AddIfNotBlank(imageUrls, FirstString(images));
        }

        return new AutoAddGameDetails(
            AutoAddSource.Epic,
            title,
            null,
            DistinctUrls(imageUrls),
            $"https://store.epicgames.com/en-US/p/{slug}",
            EpicServiceId);
    }

    private async Task<AutoAddGameDetails> FetchGogDetailsAsync(string slug, CancellationToken cancellationToken)
    {
        var query = Regex.Replace(slug, "[-_]+", " ");
        using var root = await FetchJsonAsync(
            "https://catalog.gog.com/v1/catalog?query=like:" +
            Uri.EscapeDataString(query) +
            "&limit=12&productType=in:game,pack&countryCode=US&locale=en-US&currencyCode=USD",
            "GOG",
            $"Check slug '{slug}' or the GOG game page URL.",
            cancellationToken);

        if (!root.RootElement.TryGetProperty("products", out var products) ||
            products.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"GOG did not find a game for slug '{slug}'.");
        }

        var product = default(JsonElement?);
        foreach (var candidate in products.EnumerateArray())
        {
            if (string.Equals(GetString(candidate, "slug"), slug, StringComparison.OrdinalIgnoreCase))
            {
                product = candidate;
                break;
            }
        }

        product ??= products.EnumerateArray().FirstOrDefault();
        if (product is null || product.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"GOG did not find a game for slug '{slug}'.");
        }

        var game = product.Value;
        var title = GetString(game, "title").Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException($"GOG did not return a title for slug '{slug}'.");
        }

        var resolvedSlug = GetString(game, "slug").IfBlank(slug);
        var sourcePageUrl = GetString(game, "storeLink").IfBlank($"https://www.gog.com/en/game/{resolvedSlug}");

        var imageUrls = new List<string>();
        AddIfNotBlank(imageUrls, GetString(game, "coverVertical"));
        AddIfNotBlank(imageUrls, GetString(game, "coverHorizontal"));
        AddIfNotBlank(imageUrls, GetString(game, "galaxyBackgroundImage"));

        return new AutoAddGameDetails(
            AutoAddSource.Gog,
            title,
            ExtractYear(GetString(game, "releaseDate")),
            DistinctUrls(imageUrls),
            sourcePageUrl,
            GogServiceId);
    }

    private async Task<JsonDocument> FetchJsonAsync(
        string url,
        string serviceName,
        string notFoundHint,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var bodyHint = string.IsNullOrWhiteSpace(body) ? string.Empty : $" Response: {body[..Math.Min(160, body.Length)]}";
            throw new InvalidOperationException(
                $"{serviceName} returned HTTP {(int)response.StatusCode}. {notFoundHint}{bodyHint}");
        }

        return JsonDocument.Parse(body);
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

    private static string ExtractSteamAppId(string value)
    {
        var input = value.Trim();
        if (long.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
        {
            return appId.ToString(CultureInfo.InvariantCulture);
        }

        var match = Regex.Match(input, @"store\.steampowered\.com/app/(\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        throw new InvalidOperationException(
            "Enter a Steam URL like https://store.steampowered.com/app/292030/ or an AppID.");
    }

    private static string ExtractStoreSlug(string value, string preferredMarker, string serviceName)
    {
        var input = value.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException($"{serviceName} link or slug is required.");
        }

        var slug = default(string);
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            var markerIndex = segments.FindLastIndex(segment =>
                string.Equals(segment, preferredMarker, StringComparison.OrdinalIgnoreCase));
            slug = markerIndex >= 0 && markerIndex + 1 < segments.Count
                ? segments[markerIndex + 1]
                : segments.LastOrDefault();
        }

        slug ??= input.Split('?', '#')[0].Trim('/');
        if (Regex.IsMatch(slug, @"^[a-zA-Z0-9][a-zA-Z0-9._-]*$"))
        {
            return slug;
        }

        throw new InvalidOperationException($"Enter a valid {serviceName} game page URL or slug.");
    }

    private static int? ExtractYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"\b(19|20)\d{2}\b");
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static string SteamVerticalImageUrl(string appId, bool highResolution)
    {
        var suffix = highResolution ? "_2x" : string.Empty;
        return $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900{suffix}.jpg";
    }

    private static string? SteamStoreAssetUrl(string assetUrlFormat, string filename)
    {
        if (string.IsNullOrWhiteSpace(assetUrlFormat) || string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        var path = assetUrlFormat.Replace("${FILENAME}", filename, StringComparison.Ordinal);
        return $"https://shared.akamai.steamstatic.com/store_item_assets/{path}";
    }

    private static JsonElement? FindEpicProductPage(JsonElement root)
    {
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var firstPage = default(JsonElement?);
        foreach (var page in pages.EnumerateArray())
        {
            firstPage ??= page;
            if (string.Equals(GetString(page, "_templateName"), "productDetail", StringComparison.OrdinalIgnoreCase))
            {
                return page;
            }
        }

        return firstPage;
    }

    private static bool TryGetFirstStoreItem(JsonElement root, out JsonElement storeItem)
    {
        storeItem = default;
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("store_items", out var storeItems) ||
            storeItems.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in storeItems.EnumerateArray())
        {
            storeItem = item;
            return true;
        }

        return false;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static bool GetBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.True;

    private static string? FirstString(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static void AddIfNotBlank(ICollection<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value.Trim());
        }
    }

    private static IReadOnlyList<string> DistinctUrls(IEnumerable<string> urls) =>
        urls.Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

file static class AutoAddStringExtensions
{
    public static string IfBlank(this string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
