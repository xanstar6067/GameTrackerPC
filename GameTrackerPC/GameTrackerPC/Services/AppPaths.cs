namespace GameTrackerPC.Services;

public static class AppPaths
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameVault");

    public static string ImagesDirectory { get; } = Path.Combine(RootDirectory, "images");
    public static string GoogleTokenDirectory { get; } = Path.Combine(RootDirectory, "google-token");
    public static string DatabasePath { get; } = Path.Combine(RootDirectory, "gamevault.db");

    public static string DefaultBackupDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GameVault");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(DefaultBackupDirectory);
    }

    public static string? FindGoogleClientSecret()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidateDirectory = Path.Combine(current.FullName, "JsonSecret");
            if (Directory.Exists(candidateDirectory))
            {
                var file = Directory.EnumerateFiles(candidateDirectory, "client_secret_*.json").FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(file))
                {
                    return file;
                }
            }

            current = current.Parent;
        }

        return null;
    }
}

public static class AppSettingKeys
{
    public const string ViewMode = "viewMode";
    public const string Theme = "theme";
    public const string BackupFolder = "backupFolder";
}

public static class Clock
{
    public static long UnixMillisecondsNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
