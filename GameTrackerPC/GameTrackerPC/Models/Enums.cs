namespace GameTrackerPC.Models;

public enum GameStatus
{
    COMPLETED,
    IN_PROGRESS,
    POSTPONED,
    DROPPED,
    PLANNED,
    NEVER_PLAY_AGAIN
}

public enum PlatformType
{
    PC,
    CONSOLE,
    MOBILE
}

public enum ImageSourceType
{
    NONE,
    GALLERY,
    DIRECT_IMAGE_URL,
    AUTO_PARSED
}

public enum LibraryViewMode
{
    List,
    Tiles
}

public enum AppThemeMode
{
    Light,
    Dark,
    Oled,
    Cyberpunk,
    HalfLife,
    Custom
}
