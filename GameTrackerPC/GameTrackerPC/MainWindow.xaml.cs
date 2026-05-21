using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTrackerPC.Data;
using GameTrackerPC.Models;
using GameTrackerPC.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace GameTrackerPC;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<GameListItem> _gameItems = [];
    private readonly ImageService _imageService = new();
    private readonly AutoAddGameService _autoAddService = new();
    private readonly GoogleDriveBackupService _driveService = new();
    private LibraryTransferService _transferService = null!;
    private string? _currentGameId;
    private string? _loadedCustomNotesStorage;
    private string _loadedNotesDisplay = string.Empty;
    private bool _driveAuthWarningShown;
    private bool _loading;
    private LibraryViewMode _viewMode = LibraryViewMode.List;

    public MainWindow()
    {
        InitializeComponent();
    }

    private GameVaultDbContext CreateDb() => new(AppPaths.DatabasePath);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirectories();
            await using (var db = CreateDb())
            {
                DatabaseInitializer.Initialize(db);
            }

            _transferService = new LibraryTransferService(CreateDb);
            ConfigureControls();
            await LoadSettingsAsync();
            await LoadReferencesAsync();
            await LoadGamesAsync();
            NewGame();
            SetStatus("Ready");
            await TryAutoConnectDriveAsync();
        }
        catch (Exception ex)
        {
            ShowError("Startup failed", ex);
        }
    }

    private void ConfigureControls()
    {
        GamesList.ItemsSource = _gameItems;
        StatusFilterBox.ItemsSource = new[] { "All" }.Concat(Enum.GetNames<GameStatus>()).ToList();
        StatusFilterBox.SelectedIndex = 0;
        SortBox.ItemsSource = new[]
        {
            "Title A-Z",
            "Title Z-A",
            "Status",
            "Year",
            "Created",
            "Updated"
        };
        SortBox.SelectedIndex = 0;
        PlatformBox.ItemsSource = Enum.GetValues<PlatformType>();
        PlatformBox.SelectedItem = PlatformType.PC;
        AutoAddSourceBox.ItemsSource = _autoAddService.SupportedSources;
        AutoAddSourceBox.SelectedItem = AutoAddSource.Steam;
        ImageScaleSlider.Value = 1;
    }

    private async Task LoadSettingsAsync()
    {
        await using var db = CreateDb();
        BackupFolderBox.Text = await GetSettingAsync(db, AppSettingKeys.BackupFolder, AppPaths.DefaultBackupDirectory);

        var viewModeText = await GetSettingAsync(db, AppSettingKeys.ViewMode, LibraryViewMode.List.ToString());
        _viewMode = Enum.TryParse<LibraryViewMode>(viewModeText, out var viewMode) ? viewMode : LibraryViewMode.List;
        ApplyViewMode();

        var themeText = await GetSettingAsync(db, AppSettingKeys.Theme, AppThemeMode.Light.ToString());
        var theme = Enum.TryParse<AppThemeMode>(themeText, out var parsedTheme) ? parsedTheme : AppThemeMode.Light;
        LightThemeButton.IsChecked = theme == AppThemeMode.Light;
        DarkThemeButton.IsChecked = theme == AppThemeMode.Dark;
        ApplyTheme(theme);
    }

    private static async Task<string> GetSettingAsync(GameVaultDbContext db, string key, string fallback)
    {
        var setting = await db.AppSettings.FindAsync(key);
        return string.IsNullOrWhiteSpace(setting?.Value) ? fallback : setting.Value;
    }

    private static async Task SetSettingAsync(GameVaultDbContext db, string key, string value)
    {
        var setting = await db.AppSettings.FindAsync(key);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync();
    }

    private async Task LoadReferencesAsync()
    {
        await using var db = CreateDb();
        var pcServices = await db.PcServices.OrderBy(service => service.Name).ToListAsync();
        var families = await db.ConsoleFamilies.OrderBy(family => family.Name).ToListAsync();
        var models = await db.ConsoleModels.OrderBy(model => model.Name).ToListAsync();

        PcServiceBox.ItemsSource = pcServices;
        PcServicesList.ItemsSource = pcServices;
        ConsoleFamiliesList.ItemsSource = families;
        NewModelFamilyBox.ItemsSource = families;
        ConsoleModelBox.ItemsSource = models;
        ConsoleModelsList.ItemsSource = models;

        if (NewModelFamilyBox.SelectedIndex < 0 && families.Count > 0)
        {
            NewModelFamilyBox.SelectedIndex = 0;
        }
    }

    private async Task LoadGamesAsync(string? selectGameId = null)
    {
        await using var db = CreateDb();
        var games = await db.Games
            .Include(game => game.Statuses)
            .Include(game => game.ImageGallery)
            .ToListAsync();
        var pcServices = await db.PcServices.ToDictionaryAsync(service => service.StableId, service => service.Name);
        var consoleModels = await db.ConsoleModels.ToDictionaryAsync(model => model.StableId, model => model.Name);

        var query = games.AsEnumerable();
        var search = SearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(game => game.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        if (StatusFilterBox.SelectedItem is string statusText &&
            !string.Equals(statusText, "All", StringComparison.Ordinal) &&
            Enum.TryParse<GameStatus>(statusText, out var status))
        {
            query = query.Where(game => game.Statuses.Any(item => item.Status == status));
        }

        query = SortBox.SelectedItem switch
        {
            "Title Z-A" => query.OrderByDescending(game => game.Title),
            "Status" => query.OrderBy(game => game.StatusText).ThenBy(game => game.Title),
            "Year" => query.OrderByDescending(game => game.Year ?? 0).ThenBy(game => game.Title),
            "Created" => query.OrderByDescending(game => game.CreatedAt),
            "Updated" => query.OrderByDescending(game => game.UpdatedAt),
            _ => query.OrderBy(game => game.Title)
        };

        var selectedId = selectGameId ?? (GamesList.SelectedItem as GameListItem)?.Id;
        _gameItems.Clear();

        foreach (var game in query)
        {
            _gameItems.Add(new GameListItem
            {
                Id = game.Id,
                Title = game.Title,
                Subtitle = BuildSubtitle(game, pcServices, consoleModels),
                Statuses = game.StatusText,
                YearText = game.Year?.ToString(CultureInfo.InvariantCulture) ?? "-",
                UpdatedText = $"Updated {FormatTimestamp(game.UpdatedAt)}",
                ImagePath = File.Exists(game.ImageLocalPath) ? game.ImageLocalPath : null
            });
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            GamesList.SelectedItem = _gameItems.FirstOrDefault(item => item.Id == selectedId);
        }
    }

    private static string BuildSubtitle(
        Game game,
        IReadOnlyDictionary<string, string> pcServices,
        IReadOnlyDictionary<string, string> consoleModels)
    {
        var platform = game.PlatformType.ToString();
        var service = game.PlatformType == PlatformType.CONSOLE
            ? Lookup(consoleModels, game.ConsoleModelId)
            : Lookup(pcServices, game.PcServiceId);
        var year = game.Year?.ToString(CultureInfo.InvariantCulture);
        return string.Join(" / ", new[] { platform, service, year }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> values, string? key) =>
        key is not null && values.TryGetValue(key, out var value) ? value : null;

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _transferService is null)
        {
            return;
        }

        await RunUiActionAsync("Unable to filter games.", () => LoadGamesAsync());
    }

    private async void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GamesList.SelectedItem is not GameListItem item)
        {
            return;
        }

        await RunUiActionAsync("Unable to load game.", () => LoadGameIntoEditorAsync(item.Id));
    }

    private async Task LoadGameIntoEditorAsync(string gameId)
    {
        await using var db = CreateDb();
        var game = await db.Games
            .Include(item => item.Statuses)
            .Include(item => item.ImageGallery)
            .FirstAsync(item => item.Id == gameId);

        _loading = true;
        try
        {
            _currentGameId = game.Id;
            IdBox.Text = game.Id;
            TitleBox.Text = game.Title;
            YearBox.Text = game.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            PlatformBox.SelectedItem = game.PlatformType;
            PcServiceBox.SelectedValue = game.PcServiceId;
            ConsoleModelBox.SelectedValue = game.ConsoleModelId;
            CoverUrlBox.Text = game.ImageSourceUrl ?? string.Empty;
            SourcePageUrlBox.Text = game.SourcePageUrl ?? string.Empty;
            _loadedCustomNotesStorage = game.CustomNotes;
            _loadedNotesDisplay = GameNotesSerializer.ToDisplayText(game.CustomNotes);
            NotesBox.Text = _loadedNotesDisplay;
            ImageScaleSlider.Value = game.ImageScale;
            ImageOffsetXSlider.Value = game.ImageOffsetX;
            ImageOffsetYSlider.Value = game.ImageOffsetY;
            SetStatusCheckboxes(game.Statuses.Select(status => status.Status).ToHashSet());
            SetCoverPreview(game.ImageLocalPath);
            GalleryList.ItemsSource = game.ImageGallery.OrderBy(image => image.SortOrder).ToList();
            UpdateCropText();
            ApplyPlatformControls();
        }
        finally
        {
            _loading = false;
        }
    }

    private void NewGameButton_Click(object sender, RoutedEventArgs e) => NewGame();

    private void NewGame()
    {
        _loading = true;
        try
        {
            _currentGameId = null;
            GamesList.SelectedItem = null;
            IdBox.Text = Guid.NewGuid().ToString("N");
            TitleBox.Text = string.Empty;
            YearBox.Text = string.Empty;
            PlatformBox.SelectedItem = PlatformType.PC;
            PcServiceBox.SelectedIndex = PcServiceBox.Items.Count > 0 ? 0 : -1;
            ConsoleModelBox.SelectedIndex = -1;
            CoverUrlBox.Text = string.Empty;
            SourcePageUrlBox.Text = string.Empty;
            NotesBox.Text = string.Empty;
            _loadedCustomNotesStorage = null;
            _loadedNotesDisplay = string.Empty;
            ImageScaleSlider.Value = 1;
            ImageOffsetXSlider.Value = 0;
            ImageOffsetYSlider.Value = 0;
            SetStatusCheckboxes(new HashSet<GameStatus> { GameStatus.PLANNED });
            SetCoverPreview(null);
            GalleryList.ItemsSource = null;
            UpdateCropText();
        }
        finally
        {
            _loading = false;
        }
    }

    private async void SaveGameButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to save game.", async () =>
        {
            var id = await SaveCurrentGameAsync();
            await LoadGamesAsync(id);
            SetStatus("Game saved.");
        });
    }

    private async void AutoAddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to import game from store.", async () =>
        {
            if (AutoAddSourceBox.SelectedItem is not AutoAddSource source)
            {
                throw new InvalidOperationException("Choose a store first.");
            }

            var reference = AutoAddReferenceBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(reference))
            {
                throw new InvalidOperationException("Store link, slug, or AppID is required.");
            }

            SetStatus($"Importing from {AutoAddSourceName(source)}...");
            var details = await _autoAddService.FetchGameDetailsAsync(new AutoAddRequest(source, reference));
            var result = await SaveImportedGameAsync(details);

            await LoadReferencesAsync();
            await LoadGamesAsync(result.GameId);
            if (!string.IsNullOrWhiteSpace(result.GameId))
            {
                await LoadGameIntoEditorAsync(result.GameId);
            }

            AutoAddReferenceBox.Text = string.Empty;
            SetStatus(result.ImportedCount > 0
                ? $"Imported {result.Title} from {AutoAddSourceName(source)}."
                : $"Game already exists; updated imported images for {result.Title}.");
        });
    }

    private async Task<string> SaveCurrentGameAsync()
    {
        var title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Title is required.");
        }

        int? year = null;
        if (!string.IsNullOrWhiteSpace(YearBox.Text))
        {
            if (!int.TryParse(YearBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedYear))
            {
                throw new InvalidOperationException("Year must be a number.");
            }

            year = parsedYear;
        }

        var id = string.IsNullOrWhiteSpace(IdBox.Text) ? Guid.NewGuid().ToString("N") : IdBox.Text.Trim();
        var now = Clock.UnixMillisecondsNow();
        await using var db = CreateDb();
        var game = await db.Games
            .Include(item => item.Statuses)
            .Include(item => item.ImageGallery)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (game is null)
        {
            game = new Game
            {
                Id = id,
                CreatedAt = now
            };
            db.Games.Add(game);
        }

        game.Title = title;
        game.Year = year;
        game.PlatformType = PlatformBox.SelectedItem is PlatformType platform ? platform : PlatformType.PC;
        game.PcServiceId = game.PlatformType == PlatformType.CONSOLE ? null : PcServiceBox.SelectedValue as string;
        game.ConsoleModelId = game.PlatformType == PlatformType.CONSOLE ? ConsoleModelBox.SelectedValue as string : null;
        game.ConsoleFamilyId = await ResolveConsoleFamilyIdAsync(db, game.ConsoleModelId);
        game.ImageSourceUrl = string.IsNullOrWhiteSpace(CoverUrlBox.Text) ? game.ImageSourceUrl : CoverUrlBox.Text.Trim();
        game.SourcePageUrl = EmptyToNull(SourcePageUrlBox.Text);
        game.CustomNotes = string.Equals(NotesBox.Text, _loadedNotesDisplay, StringComparison.Ordinal)
            ? _loadedCustomNotesStorage
            : GameNotesSerializer.ToStorageFromPlainText(NotesBox.Text);
        game.ImageScale = Clamp(ImageScaleSlider.Value, 1, 4);
        game.ImageOffsetX = Clamp(ImageOffsetXSlider.Value, -2, 2);
        game.ImageOffsetY = Clamp(ImageOffsetYSlider.Value, -2, 2);
        game.UpdatedAt = now;

        var oldStatuses = game.Statuses.ToList();
        db.GameStatuses.RemoveRange(oldStatuses);
        game.Statuses.Clear();
        foreach (var status in GetSelectedStatuses())
        {
            game.Statuses.Add(new GameStatusEntry { GameId = game.Id, Status = status });
        }

        await db.SaveChangesAsync();
        _currentGameId = id;
        _loadedCustomNotesStorage = game.CustomNotes;
        _loadedNotesDisplay = GameNotesSerializer.ToDisplayText(game.CustomNotes);
        return id;
    }

    private async Task<AutoAddResult> SaveImportedGameAsync(AutoAddGameDetails details)
    {
        var imageUrls = details.ImageUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var db = CreateDb();
        await EnsurePcServiceAsync(db, details.PcServiceId, AutoAddSourceName(details.Source));

        var existingGame = await FindExistingImportedGameAsync(db, details);
        if (existingGame is not null)
        {
            var changed = false;
            if (string.IsNullOrWhiteSpace(existingGame.SourcePageUrl))
            {
                existingGame.SourcePageUrl = details.SourcePageUrl;
                changed = true;
            }

            changed |= await AddImportedImagesToExistingGameAsync(existingGame, imageUrls);
            if (changed)
            {
                existingGame.UpdatedAt = Clock.UnixMillisecondsNow();
            }

            await db.SaveChangesAsync();
            return new AutoAddResult(details.Source, 0, existingGame.Id, existingGame.Title);
        }

        var storedImages = await DownloadAutoImagesAsync(imageUrls, MaxAutoGalleryImages);
        var firstStoredImage = storedImages.FirstOrDefault();
        var primaryImageUrl = firstStoredImage?.SourceUrl ?? imageUrls.FirstOrDefault();
        var now = Clock.UnixMillisecondsNow();
        var game = new Game
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = details.Title,
            Year = details.Year,
            PlatformType = PlatformType.PC,
            PcServiceId = details.PcServiceId,
            ImageLocalPath = firstStoredImage?.LocalPath,
            ImageArchiveName = firstStoredImage?.ArchiveName,
            ImageSourceUrl = primaryImageUrl,
            ImageSourceType = firstStoredImage is not null
                ? ImageSourceType.AUTO_PARSED
                : primaryImageUrl is not null
                    ? ImageSourceType.AUTO_PARSED
                    : ImageSourceType.NONE,
            SourcePageUrl = details.SourcePageUrl,
            CreatedAt = now,
            UpdatedAt = now
        };
        game.Statuses.Add(new GameStatusEntry { GameId = game.Id, Status = GameStatus.PLANNED });

        for (var index = 0; index < storedImages.Count; index++)
        {
            var image = storedImages[index];
            game.ImageGallery.Add(new GameImage
            {
                GameId = game.Id,
                LocalPath = image.LocalPath,
                ArchiveName = image.ArchiveName,
                SourceUrl = image.SourceUrl,
                SourceType = ImageSourceType.AUTO_PARSED,
                SortOrder = index
            });
        }

        db.Games.Add(game);
        await db.SaveChangesAsync();
        return new AutoAddResult(details.Source, 1, game.Id, game.Title);
    }

    private static async Task<Game?> FindExistingImportedGameAsync(GameVaultDbContext db, AutoAddGameDetails details)
    {
        var candidates = await db.Games
            .Include(game => game.Statuses)
            .Include(game => game.ImageGallery)
            .Where(game => game.SourcePageUrl == details.SourcePageUrl || game.PcServiceId == details.PcServiceId)
            .ToListAsync();

        return candidates.FirstOrDefault(game =>
            string.Equals(game.SourcePageUrl, details.SourcePageUrl, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(game.PcServiceId, details.PcServiceId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(game.Title, details.Title, StringComparison.CurrentCultureIgnoreCase)));
    }

    private async Task<bool> AddImportedImagesToExistingGameAsync(Game game, IReadOnlyList<string> imageUrls)
    {
        var existingSourceUrls = game.ImageGallery
            .Select(image => image.SourceUrl)
            .Append(game.ImageSourceUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableSlots = MaxAutoGalleryImages - game.ImageGallery.Count;
        if (availableSlots <= 0)
        {
            return false;
        }

        var urlsToImport = imageUrls
            .Where(url => !existingSourceUrls.Contains(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(availableSlots)
            .ToList();
        var storedImages = await DownloadAutoImagesAsync(urlsToImport, availableSlots);
        if (storedImages.Count == 0)
        {
            return false;
        }

        var nextSortOrder = game.ImageGallery.Count == 0
            ? 0
            : game.ImageGallery.Max(image => image.SortOrder) + 1;
        foreach (var image in storedImages)
        {
            game.ImageGallery.Add(new GameImage
            {
                GameId = game.Id,
                LocalPath = image.LocalPath,
                ArchiveName = image.ArchiveName,
                SourceUrl = image.SourceUrl,
                SourceType = ImageSourceType.AUTO_PARSED,
                SortOrder = nextSortOrder++
            });
        }

        var firstImage = storedImages[0];
        var shouldUseAsCover = string.IsNullOrWhiteSpace(game.ImageLocalPath) ||
            (!IsSteamLibraryCoverUrl(game.ImageSourceUrl) && IsSteamLibraryCoverUrl(firstImage.SourceUrl));
        if (shouldUseAsCover)
        {
            game.ImageLocalPath = firstImage.LocalPath;
            game.ImageArchiveName = firstImage.ArchiveName;
            game.ImageSourceUrl = firstImage.SourceUrl;
            game.ImageSourceType = ImageSourceType.AUTO_PARSED;
        }

        return true;
    }

    private async Task<List<AutoImportedImage>> DownloadAutoImagesAsync(IEnumerable<string> urls, int maxCount)
    {
        var storedImages = new List<AutoImportedImage>();
        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount))
        {
            try
            {
                var localPath = await _imageService.DownloadImageAsync(url);
                storedImages.Add(new AutoImportedImage(localPath, Path.GetFileName(localPath), url));
            }
            catch
            {
                // Store image URLs can expire or reject hotlinking; keep importing the rest of the game data.
            }
        }

        return storedImages;
    }

    private static async Task EnsurePcServiceAsync(GameVaultDbContext db, string stableId, string name)
    {
        if (await db.PcServices.AnyAsync(service => service.StableId == stableId))
        {
            return;
        }

        db.PcServices.Add(new PcService
        {
            StableId = stableId,
            Name = name,
            IsBuiltIn = true,
            CreatedAt = Clock.UnixMillisecondsNow()
        });
    }

    private static string AutoAddSourceName(AutoAddSource source) => source switch
    {
        AutoAddSource.Steam => "Steam",
        AutoAddSource.Gog => "GOG",
        AutoAddSource.Epic => "Epic Games Store",
        _ => source.ToString()
    };

    private static bool IsSteamLibraryCoverUrl(string? value) =>
        value?.Contains("library_600x900", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string?> ResolveConsoleFamilyIdAsync(GameVaultDbContext db, string? consoleModelId)
    {
        if (string.IsNullOrWhiteSpace(consoleModelId))
        {
            return null;
        }

        var model = await db.ConsoleModels.FirstOrDefaultAsync(item => item.StableId == consoleModelId);
        return model?.FamilyStableId;
    }

    private async void DeleteGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentGameId))
        {
            return;
        }

        if (MessageBox.Show(this, "Delete selected game?", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync("Unable to delete game.", async () =>
        {
            await using var db = CreateDb();
            var game = await db.Games.FindAsync(_currentGameId);
            if (game is not null)
            {
                db.Games.Remove(game);
                await db.SaveChangesAsync();
            }

            await LoadGamesAsync();
            NewGame();
            SetStatus("Game deleted.");
        });
    }

    private async void AddCoverLocalButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickImageFile();
        if (path is null)
        {
            return;
        }

        await RunUiActionAsync("Unable to add cover.", async () =>
        {
            var copied = _imageService.CopyLocalImage(path);
            await UpdateCoverAsync(copied, Path.GetFileName(copied), null, ImageSourceType.GALLERY);
        });
    }

    private async void DownloadCoverButton_Click(object sender, RoutedEventArgs e)
    {
        var url = CoverUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        await RunUiActionAsync("Unable to download cover.", async () =>
        {
            var downloaded = await _imageService.DownloadImageAsync(url);
            await UpdateCoverAsync(downloaded, Path.GetFileName(downloaded), url, ImageSourceType.DIRECT_IMAGE_URL);
        });
    }

    private async void ReloadCoverButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to reload cover.", async () =>
        {
            var id = await EnsureSavedGameAsync();
            await using var db = CreateDb();
            var game = await db.Games.FindAsync(id) ?? throw new InvalidOperationException("Game was not found.");
            if (string.IsNullOrWhiteSpace(game.ImageSourceUrl))
            {
                throw new InvalidOperationException("This cover does not have imageSourceUrl.");
            }

            var downloaded = await _imageService.DownloadImageAsync(game.ImageSourceUrl);
            await UpdateCoverAsync(downloaded, Path.GetFileName(downloaded), game.ImageSourceUrl, ImageSourceType.DIRECT_IMAGE_URL);
        });
    }

    private async Task UpdateCoverAsync(
        string localPath,
        string archiveName,
        string? sourceUrl,
        ImageSourceType sourceType)
    {
        var id = await EnsureSavedGameAsync();
        await using var db = CreateDb();
        var game = await db.Games.FindAsync(id) ?? throw new InvalidOperationException("Game was not found.");
        game.ImageLocalPath = localPath;
        game.ImageArchiveName = archiveName;
        game.ImageSourceUrl = sourceUrl ?? game.ImageSourceUrl;
        game.ImageSourceType = sourceType;
        game.UpdatedAt = Clock.UnixMillisecondsNow();
        await db.SaveChangesAsync();
        await LoadGameIntoEditorAsync(id);
        await LoadGamesAsync(id);
    }

    private async void AddGalleryLocalButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickImageFile();
        if (path is null)
        {
            return;
        }

        await RunUiActionAsync("Unable to add gallery image.", async () =>
        {
            var copied = _imageService.CopyLocalImage(path);
            await AddGalleryImageAsync(copied, Path.GetFileName(copied), null, ImageSourceType.GALLERY);
        });
    }

    private async void AddGalleryUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var url = GalleryUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        await RunUiActionAsync("Unable to download gallery image.", async () =>
        {
            var downloaded = await _imageService.DownloadImageAsync(url);
            await AddGalleryImageAsync(downloaded, Path.GetFileName(downloaded), url, ImageSourceType.DIRECT_IMAGE_URL);
            GalleryUrlBox.Text = string.Empty;
        });
    }

    private async Task AddGalleryImageAsync(string localPath, string archiveName, string? sourceUrl, ImageSourceType sourceType)
    {
        var id = await EnsureSavedGameAsync();
        await using var db = CreateDb();
        var count = await db.GameImages.CountAsync(image => image.GameId == id);
        if (count >= 20)
        {
            throw new InvalidOperationException("Gallery limit is 20 images.");
        }

        db.GameImages.Add(new GameImage
        {
            GameId = id,
            LocalPath = localPath,
            ArchiveName = archiveName,
            SourceUrl = sourceUrl,
            SourceType = sourceType,
            SortOrder = count
        });

        var game = await db.Games.FindAsync(id);
        if (game is not null && string.IsNullOrWhiteSpace(game.ImageLocalPath))
        {
            game.ImageLocalPath = localPath;
            game.ImageArchiveName = archiveName;
            game.ImageSourceUrl = sourceUrl;
            game.ImageSourceType = sourceType;
        }

        if (game is not null)
        {
            game.UpdatedAt = Clock.UnixMillisecondsNow();
        }

        await db.SaveChangesAsync();
        await LoadGameIntoEditorAsync(id);
        await LoadGamesAsync(id);
    }

    private async void SetGalleryCoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not GameImage image || string.IsNullOrWhiteSpace(image.LocalPath))
        {
            return;
        }

        await RunUiActionAsync("Unable to set gallery image as cover.", async () =>
        {
            await UpdateCoverAsync(
                image.LocalPath,
                image.ArchiveName ?? Path.GetFileName(image.LocalPath),
                image.SourceUrl,
                image.SourceType);
        });
    }

    private async void RemoveGalleryImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not GameImage image)
        {
            return;
        }

        await RunUiActionAsync("Unable to remove gallery image.", async () =>
        {
            await using var db = CreateDb();
            var stored = await db.GameImages.FindAsync(image.Id);
            if (stored is not null)
            {
                db.GameImages.Remove(stored);
                await db.SaveChangesAsync();
            }

            if (!string.IsNullOrWhiteSpace(_currentGameId))
            {
                await LoadGameIntoEditorAsync(_currentGameId);
            }
        });
    }

    private async void AddPcServiceButton_Click(object sender, RoutedEventArgs e)
    {
        await AddReferenceAsync(NewPcServiceBox, async (db, stableId, name) =>
        {
            db.PcServices.Add(new PcService { StableId = stableId, Name = name, CreatedAt = Clock.UnixMillisecondsNow() });
            await db.SaveChangesAsync();
        });
    }

    private async void AddConsoleFamilyButton_Click(object sender, RoutedEventArgs e)
    {
        await AddReferenceAsync(NewConsoleFamilyBox, async (db, stableId, name) =>
        {
            db.ConsoleFamilies.Add(new ConsoleFamily { StableId = stableId, Name = name, CreatedAt = Clock.UnixMillisecondsNow() });
            await db.SaveChangesAsync();
        });
    }

    private async void AddConsoleModelButton_Click(object sender, RoutedEventArgs e)
    {
        var familyId = NewModelFamilyBox.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(familyId))
        {
            MessageBox.Show(this, "Choose console family first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await AddReferenceAsync(NewConsoleModelBox, async (db, stableId, name) =>
        {
            db.ConsoleModels.Add(new ConsoleModel
            {
                StableId = stableId,
                FamilyStableId = familyId,
                Name = name,
                CreatedAt = Clock.UnixMillisecondsNow()
            });
            await db.SaveChangesAsync();
        });
    }

    private async Task AddReferenceAsync(System.Windows.Controls.TextBox sourceBox, Func<GameVaultDbContext, string, string, Task> add)
    {
        await RunUiActionAsync("Unable to add reference.", async () =>
        {
            var name = sourceBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Name is required.");
            }

            await using var db = CreateDb();
            await add(db, Slugify(name), name);
            sourceBox.Text = string.Empty;
            await LoadReferencesAsync();
            SetStatus("Reference added.");
        });
    }

    private async void ChooseBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose GameVault backup/export folder",
            InitialDirectory = Directory.Exists(BackupFolderBox.Text) ? BackupFolderBox.Text : AppPaths.DefaultBackupDirectory
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BackupFolderBox.Text = dialog.FolderName;
        await SaveBackupFolderAsync();
    }

    private async void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to export JSON.", async () =>
        {
            var path = BuildLocalBackupPath("json");
            await _transferService.ExportJsonAsync(path);
            SetStatus($"Exported JSON: {path}");
        });
    }

    private async void ExportZipButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to export ZIP.", async () =>
        {
            var path = BuildLocalBackupPath("zip");
            await _transferService.ExportZipAsync(path);
            SetStatus($"Exported ZIP: {path}");
        });
    }

    private async void ImportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickOpenFile("GameVault JSON (*.json)|*.json|All files (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        await ImportFileAsync(path, isZip: false);
    }

    private async void ImportZipButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickOpenFile("GameVault ZIP (*.zip)|*.zip|All files (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        await ImportFileAsync(path, isZip: true);
    }

    private async Task ImportFileAsync(string path, bool isZip)
    {
        await RunUiActionAsync("Unable to import library.", async () =>
        {
            var result = isZip
                ? await _transferService.ImportZipAsync(path, ResolveImportConflict)
                : await _transferService.ImportJsonAsync(path, ResolveImportConflict);
            await LoadReferencesAsync();
            await LoadGamesAsync();
            SetStatus(result.Summary);
        });
    }

    private ImportConflictDecision ResolveImportConflict(ImportConflictInfo conflict)
    {
        var dialog = new ImportConflictDialog(conflict) { Owner = this };
        dialog.ShowDialog();
        return dialog.Decision;
    }

    private async void ClearImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear all files from local image cache?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync("Unable to clear image cache.", async () =>
        {
            _imageService.ClearImageCache();
            await LoadGamesAsync(_currentGameId);
            if (!string.IsNullOrWhiteSpace(_currentGameId))
            {
                await LoadGameIntoEditorAsync(_currentGameId);
            }

            SetStatus("Image cache cleared.");
        });
    }

    private async void ClearLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all local games? References and settings will be kept.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync("Unable to clear library.", async () =>
        {
            await using var db = CreateDb();
            db.Games.RemoveRange(db.Games);
            await db.SaveChangesAsync();
            await LoadGamesAsync();
            NewGame();
            SetStatus("Local library cleared.");
        });
    }

    private async void ConnectDriveButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to connect Google Drive.", async () =>
        {
            SetStatus("Opening browser sign-in...");
            await _driveService.ConnectAsync();
            _driveAuthWarningShown = false;
            await RefreshDriveBackupsAsync();
            SetStatus("Google Drive connected.");
        });
    }

    private void DisconnectDriveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _driveService.Disconnect();
            DriveBackupsGrid.ItemsSource = null;
            SetStatus("Google Drive disconnected.");
        }
        catch (Exception ex)
        {
            ShowError("Unable to disconnect Google Drive.", ex);
        }
    }

    private async void UploadDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to upload backup.", async () =>
        {
            if (!await EnsureDriveConnectedAsync())
            {
                return;
            }

            var path = BuildLocalBackupPath("zip");
            await _transferService.ExportZipAsync(path);
            await _driveService.UploadZipBackupAsync(path);
            await RefreshDriveBackupsAsync();
            SetStatus("ZIP backup uploaded to root /GameVault.");
        });
    }

    private async void AutoExportDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to auto export to Google Drive.", async () =>
        {
            if (!await EnsureDriveConnectedAsync())
            {
                return;
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "GameVault");
            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory, $"gamevault-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            try
            {
                await _transferService.ExportZipAsync(tempPath);
                await _driveService.UploadZipBackupAsync(tempPath);
                await RefreshDriveBackupsAsync();
                SetStatus("Temporary ZIP archive exported and uploaded to root /GameVault.");
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        });
    }

    private async void RefreshDriveBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to refresh backups.", async () =>
        {
            if (await EnsureDriveConnectedAsync())
            {
                await RefreshDriveBackupsAsync();
            }
        });
    }

    private async Task RefreshDriveBackupsAsync()
    {
        var backups = await _driveService.ListBackupsAsync();
        DriveBackupsGrid.ItemsSource = backups;
        SetStatus($"Loaded {backups.Count} Google Drive backup(s) from root /GameVault.");
    }

    private async void RestoreSelectedDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBackupsGrid.SelectedItem is not DriveBackupFile backup)
        {
            return;
        }

        await RestoreDriveBackupAsync(backup);
    }

    private async void RestoreLatestDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Unable to restore latest backup.", async () =>
        {
            if (!await EnsureDriveConnectedAsync())
            {
                return;
            }

            var backups = (await _driveService.ListBackupsAsync()).ToList();
            if (backups.Count == 0)
            {
                throw new InvalidOperationException("No backups found in root /GameVault.");
            }

            await RestoreDriveBackupCoreAsync(backups[0]);
        });
    }

    private async Task RestoreDriveBackupAsync(DriveBackupFile backup)
    {
        await RunUiActionAsync("Unable to restore backup.", async () =>
        {
            if (await EnsureDriveConnectedAsync())
            {
                await RestoreDriveBackupCoreAsync(backup);
            }
        });
    }

    private async Task RestoreDriveBackupCoreAsync(DriveBackupFile backup)
    {
        var localPath = Path.Combine(GetBackupFolder(), $"restore-{DateTime.Now:yyyyMMdd-HHmmss}-{backup.Name}");
        await _driveService.DownloadBackupAsync(backup.Id, localPath);
        var result = await _transferService.ImportZipAsync(localPath, ResolveImportConflict);
        await LoadReferencesAsync();
        await LoadGamesAsync();
        SetStatus(result.Summary);
    }

    private async void TrashDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBackupsGrid.SelectedItem is not DriveBackupFile backup)
        {
            return;
        }

        if (MessageBox.Show(this, $"Move '{backup.Name}' to Google Drive trash?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync("Unable to move backup to trash.", async () =>
        {
            if (!await EnsureDriveConnectedAsync())
            {
                return;
            }

            await _driveService.TrashBackupAsync(backup.Id);
            await RefreshDriveBackupsAsync();
            SetStatus("Backup moved to Google Drive trash.");
        });
    }

    private async Task TryAutoConnectDriveAsync()
    {
        if (await _driveService.TryConnectWithStoredTokenAsync())
        {
            await RefreshDriveBackupsAsync();
            SetStatus("Google Drive connected automatically.");
            return;
        }

        ShowDriveAuthWarningOnce(
            _driveService.HasStoredToken
                ? "Google Drive token is missing, expired, or no longer valid. Use Connect account to sign in again."
                : "Google Drive is not authenticated yet. Use Connect account once, then future starts will connect automatically.");
    }

    private async Task<bool> EnsureDriveConnectedAsync()
    {
        if (_driveService.IsConnected)
        {
            return true;
        }

        if (await _driveService.TryConnectWithStoredTokenAsync())
        {
            return true;
        }

        ShowDriveAuthWarningOnce(
            _driveService.HasStoredToken
                ? "Google Drive token is missing, expired, or no longer valid. Use Connect account to sign in again."
                : "Google Drive is not authenticated yet. Use Connect account once before using Drive actions.");
        return false;
    }

    private void ShowDriveAuthWarningOnce(string message)
    {
        if (_driveAuthWarningShown)
        {
            SetStatus(message);
            return;
        }

        _driveAuthWarningShown = true;
        SetStatus(message);
        MessageBox.Show(this, message, "Google Drive", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ListModeButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMode = LibraryViewMode.List;
        ApplyViewMode();
        await SaveViewModeAsync();
    }

    private async void TilesModeButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMode = LibraryViewMode.Tiles;
        ApplyViewMode();
        await SaveViewModeAsync();
    }

    private void ApplyViewMode()
    {
        GamesList.ItemsPanel = (ItemsPanelTemplate)FindResource(_viewMode == LibraryViewMode.List ? "ListPanelTemplate" : "TilePanelTemplate");
        GamesList.ItemContainerStyle = CreateGameItemStyle(_viewMode);
        ListModeButton.FontWeight = _viewMode == LibraryViewMode.List ? FontWeights.SemiBold : FontWeights.Normal;
        TilesModeButton.FontWeight = _viewMode == LibraryViewMode.Tiles ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static Style CreateGameItemStyle(LibraryViewMode mode)
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        if (mode == LibraryViewMode.Tiles)
        {
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 250d));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 10)));
        }

        return style;
    }

    private async Task SaveViewModeAsync()
    {
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.ViewMode, _viewMode.ToString());
    }

    private async void ThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _transferService is null)
        {
            return;
        }

        var theme = DarkThemeButton.IsChecked == true ? AppThemeMode.Dark : AppThemeMode.Light;
        ApplyTheme(theme);
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.Theme, theme.ToString());
    }

    private void ApplyTheme(AppThemeMode theme)
    {
        Background = theme == AppThemeMode.Dark
            ? new SolidColorBrush(Color.FromRgb(30, 41, 59))
            : new SolidColorBrush(Color.FromRgb(238, 242, 247));
    }

    private void PlatformBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyPlatformControls();
    }

    private void ApplyPlatformControls()
    {
        if (PlatformBox.SelectedItem is not PlatformType platform)
        {
            return;
        }

        PcServiceBox.IsEnabled = platform != PlatformType.CONSOLE;
        ConsoleModelBox.IsEnabled = platform == PlatformType.CONSOLE;
    }

    private void CropSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateCropText();
    }

    private void UpdateCropText()
    {
        if (ImageScaleText is null)
        {
            return;
        }

        ImageScaleText.Text = ImageScaleSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        ImageOffsetXText.Text = ImageOffsetXSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        ImageOffsetYText.Text = ImageOffsetYSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private async Task<string> EnsureSavedGameAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentGameId))
        {
            return await SaveCurrentGameAsync();
        }

        return await SaveCurrentGameAsync();
    }

    private List<GameStatus> GetSelectedStatuses()
    {
        var statuses = new List<GameStatus>();
        AddIfChecked(StatusCompletedBox, GameStatus.COMPLETED);
        AddIfChecked(StatusInProgressBox, GameStatus.IN_PROGRESS);
        AddIfChecked(StatusPostponedBox, GameStatus.POSTPONED);
        AddIfChecked(StatusDroppedBox, GameStatus.DROPPED);
        AddIfChecked(StatusPlannedBox, GameStatus.PLANNED);
        AddIfChecked(StatusNeverPlayAgainBox, GameStatus.NEVER_PLAY_AGAIN);
        if (statuses.Count == 0)
        {
            statuses.Add(GameStatus.PLANNED);
        }

        return statuses;

        void AddIfChecked(CheckBox checkBox, GameStatus status)
        {
            if (checkBox.IsChecked == true)
            {
                statuses.Add(status);
            }
        }
    }

    private void SetStatusCheckboxes(ISet<GameStatus> statuses)
    {
        StatusCompletedBox.IsChecked = statuses.Contains(GameStatus.COMPLETED);
        StatusInProgressBox.IsChecked = statuses.Contains(GameStatus.IN_PROGRESS);
        StatusPostponedBox.IsChecked = statuses.Contains(GameStatus.POSTPONED);
        StatusDroppedBox.IsChecked = statuses.Contains(GameStatus.DROPPED);
        StatusPlannedBox.IsChecked = statuses.Contains(GameStatus.PLANNED) || statuses.Count == 0;
        StatusNeverPlayAgainBox.IsChecked = statuses.Contains(GameStatus.NEVER_PLAY_AGAIN);
    }

    private void SetCoverPreview(string? path)
    {
        CoverPreview.Source = LoadBitmap(path);
    }

    private static BitmapImage? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveBackupFolderAsync()
    {
        Directory.CreateDirectory(GetBackupFolder());
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.BackupFolder, GetBackupFolder());
    }

    private string GetBackupFolder()
    {
        var folder = string.IsNullOrWhiteSpace(BackupFolderBox.Text) ? AppPaths.DefaultBackupDirectory : BackupFolderBox.Text.Trim();
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string BuildLocalBackupPath(string extension)
    {
        var folder = GetBackupFolder();
        return Path.Combine(folder, $"gamevault-library-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
    }

    private string? PickImageFile() => PickOpenFile("Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All files (*.*)|*.*");

    private string? PickOpenFile(string filter)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task RunUiActionAsync(string errorTitle, Func<Task> action)
    {
        try
        {
            await SaveBackupFolderAsync();
            await action();
        }
        catch (Exception ex)
        {
            ShowError(errorTitle, ex);
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void ShowError(string title, Exception ex)
    {
        SetStatus($"{title} {ex.Message}");
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string FormatTimestamp(long timestamp)
    {
        if (timestamp <= 0)
        {
            return "-";
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    private static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? Guid.NewGuid().ToString("N") : slug;
    }

    private sealed record AutoImportedImage(string LocalPath, string ArchiveName, string SourceUrl);

    private const int MaxAutoGalleryImages = 20;
}
