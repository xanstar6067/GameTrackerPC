namespace GameTrackerPC.Models;

public enum AutoAddSource
{
    Steam,
    Gog,
    Epic
}

public sealed record AutoAddRequest(
    AutoAddSource Source,
    string AccountReference);

public sealed record AutoAddResult(
    AutoAddSource Source,
    int ImportedCount,
    string? GameId = null,
    string? Title = null);

public sealed record AutoAddGameDetails(
    AutoAddSource Source,
    string Title,
    int? Year,
    IReadOnlyList<string> ImageUrls,
    string SourcePageUrl,
    string PcServiceId);
