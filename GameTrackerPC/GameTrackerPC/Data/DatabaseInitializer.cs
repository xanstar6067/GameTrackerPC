using GameTrackerPC.Models;
using GameTrackerPC.Services;

namespace GameTrackerPC.Data;

public static class DatabaseInitializer
{
    public static void Initialize(GameVaultDbContext db)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DatabasePath)!);
        db.Database.EnsureCreated();

        var now = Clock.UnixMillisecondsNow();

        if (!db.AppSettings.Any())
        {
            db.AppSettings.AddRange(
                new AppSetting { Key = AppSettingKeys.ViewMode, Value = LibraryViewMode.List.ToString() },
                new AppSetting { Key = AppSettingKeys.Theme, Value = AppThemeMode.Light.ToString() },
                new AppSetting { Key = AppSettingKeys.Language, Value = "ru" },
                new AppSetting { Key = AppSettingKeys.BackupFolder, Value = AppPaths.DefaultBackupDirectory });
        }

        if (!db.PcServices.Any())
        {
            db.PcServices.AddRange(new[]
            {
                BuiltInPcService("steam", "Steam", now),
                BuiltInPcService("epic-games-store", "Epic Games Store", now),
                BuiltInPcService("gog", "GOG", now),
                BuiltInPcService("ubisoft-connect", "Ubisoft Connect", now),
                BuiltInPcService("itch-io", "itch.io", now),
                BuiltInPcService("indiegala", "Indiegala", now),
                BuiltInPcService("google-play", "Google Play", now),
                BuiltInPcService("epic-games-mobile", "Epic Games Mobile", now),
                BuiltInPcService("ea-app", "EA App", now),
                BuiltInPcService("microsoft-store", "Microsoft Store", now),
                BuiltInPcService("other", "Other", now)
            });
        }

        if (!db.ConsoleFamilies.Any())
        {
            db.ConsoleFamilies.AddRange(new[]
            {
                BuiltInConsoleFamily("nintendo", "Nintendo", now),
                BuiltInConsoleFamily("sony", "Sony", now),
                BuiltInConsoleFamily("microsoft", "Microsoft", now),
                BuiltInConsoleFamily("sega", "Sega", now)
            });
        }

        if (!db.ConsoleModels.Any())
        {
            db.ConsoleModels.AddRange(new[]
            {
                BuiltInConsoleModel("nes", "nintendo", "NES", now),
                BuiltInConsoleModel("snes", "nintendo", "SNES", now),
                BuiltInConsoleModel("nintendo-64", "nintendo", "Nintendo 64", now),
                BuiltInConsoleModel("gamecube", "nintendo", "GameCube", now),
                BuiltInConsoleModel("wii", "nintendo", "Wii", now),
                BuiltInConsoleModel("wii-u", "nintendo", "Wii U", now),
                BuiltInConsoleModel("nintendo-switch", "nintendo", "Nintendo Switch", now),
                BuiltInConsoleModel("nintendo-switch-2", "nintendo", "Nintendo Switch 2", now),
                BuiltInConsoleModel("playstation", "sony", "PlayStation", now),
                BuiltInConsoleModel("playstation-2", "sony", "PlayStation 2", now),
                BuiltInConsoleModel("playstation-3", "sony", "PlayStation 3", now),
                BuiltInConsoleModel("playstation-4", "sony", "PlayStation 4", now),
                BuiltInConsoleModel("playstation-5", "sony", "PlayStation 5", now),
                BuiltInConsoleModel("psp", "sony", "PSP", now),
                BuiltInConsoleModel("ps-vita", "sony", "PS Vita", now),
                BuiltInConsoleModel("xbox", "microsoft", "Xbox", now),
                BuiltInConsoleModel("xbox-360", "microsoft", "Xbox 360", now),
                BuiltInConsoleModel("xbox-one", "microsoft", "Xbox One", now),
                BuiltInConsoleModel("xbox-series", "microsoft", "Xbox Series X|S", now),
                BuiltInConsoleModel("master-system", "sega", "Master System", now),
                BuiltInConsoleModel("genesis-mega-drive", "sega", "Genesis / Mega Drive", now),
                BuiltInConsoleModel("saturn", "sega", "Saturn", now),
                BuiltInConsoleModel("dreamcast", "sega", "Dreamcast", now)
            });
        }

        db.SaveChanges();
    }

    private static PcService BuiltInPcService(string stableId, string name, long now) =>
        new() { StableId = stableId, Name = name, IsBuiltIn = true, CreatedAt = now };

    private static ConsoleFamily BuiltInConsoleFamily(string stableId, string name, long now) =>
        new() { StableId = stableId, Name = name, IsBuiltIn = true, CreatedAt = now };

    private static ConsoleModel BuiltInConsoleModel(string stableId, string familyStableId, string name, long now) =>
        new() { StableId = stableId, FamilyStableId = familyStableId, Name = name, IsBuiltIn = true, CreatedAt = now };
}
