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
}

public static class AppSettingKeys
{
    public const string ViewMode = "viewMode";
    public const string CardScale = "cardScale";
    public const string UiFontScale = "uiFontScale";
    public const string Theme = "theme";
    public const string Language = "language";
    public const string BackupFolder = "backupFolder";
    public const string CustomThemes = "customThemes";
    public const string CustomThemeName = "customThemeName";
    public const string CustomThemeBackground = "customThemeBackground";
    public const string CustomThemeSurface = "customThemeSurface";
    public const string CustomThemePanel = "customThemePanel";
    public const string CustomThemeText = "customThemeText";
    public const string CustomThemeMuted = "customThemeMuted";
    public const string CustomThemePrimary = "customThemePrimary";
    public const string CustomThemeSecondary = "customThemeSecondary";
}

public static class Clock
{
    public static long UnixMillisecondsNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
