namespace GameTrackerPC.Services;

public sealed record DownloadProgress(long BytesDownloaded, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0
        ? BytesDownloaded * 100d / TotalBytes.Value
        : null;
}
