using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using GameTrackerPC.Data;
using GameTrackerPC.Models;
using GameTrackerPC.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using DoubleAnimation = System.Windows.Media.Animation.DoubleAnimation;
using EasingMode = System.Windows.Media.Animation.EasingMode;
using QuadraticEase = System.Windows.Media.Animation.QuadraticEase;

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
    private bool _updatingThemeControls;
    private bool _suppressGameSelectionNavigation;
    private bool _suppressCardScaleSave;
    private bool _suppressUiFontScaleSave;
    private bool _deferUiFontScaleApply;
    private bool _coverEditMode;
    private bool _coverDragActive;
    private Point _coverDragStart;
    private LibraryViewMode _viewMode = LibraryViewMode.List;
    private double _cardScale = DefaultCardScale;
    private double _uiFontScale = DefaultUiFontScale;
    private double _imageScale = 1;
    private double _imageOffsetX;
    private double _imageOffsetY;
    private double _coverDragStartOffsetX;
    private double _coverDragStartOffsetY;
    private double _coverEditStartScale;
    private double _coverEditStartOffsetX;
    private double _coverEditStartOffsetY;
    private string _language = RussianLanguage;
    private string _currentThemeKey = AppThemeMode.Light.ToString();
    private AppScreen _currentScreen = AppScreen.Start;
    private readonly Stack<AppScreen> _navigationStack = new();
    private readonly List<ThemePalette> _customThemes = [];
    private ThemePalette _customTheme = ThemePalette.DefaultCustom;
    private TextBox? _activeColorTextBox;
    private bool _updatingColorPicker;
    private double _pickerHue;
    private double _pickerSaturation;
    private double _pickerValue;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLanguage();
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
                await _imageService.DeleteUnreferencedImagesAsync(db);
            }

            _transferService = new LibraryTransferService(CreateDb);
            ConfigureControls();
            await LoadSettingsAsync();
            await LoadReferencesAsync();
            await LoadGamesAsync();
            NewGame();
            SetStatus(T("Ready"));
            await TryAutoConnectDriveAsync();
        }
        catch (Exception ex)
        {
            ShowError(T("StartupFailed"), ex);
        }
    }

    private void ConfigureControls()
    {
        GamesList.ItemsSource = _gameItems;
        StatusFilterBox.DisplayMemberPath = nameof(UiOption<GameStatus?>.Text);
        StatusFilterBox.SelectedValuePath = nameof(UiOption<GameStatus?>.Value);
        SortBox.DisplayMemberPath = nameof(UiOption<SortMode>.Text);
        SortBox.SelectedValuePath = nameof(UiOption<SortMode>.Value);
        PlatformBox.DisplayMemberPath = nameof(UiOption<PlatformType>.Text);
        PlatformBox.SelectedValuePath = nameof(UiOption<PlatformType>.Value);
        LanguageBox.DisplayMemberPath = nameof(UiOption<string>.Text);
        LanguageBox.SelectedValuePath = nameof(UiOption<string>.Value);
        StartThemeBox.DisplayMemberPath = nameof(UiOption<string>.Text);
        StartThemeBox.SelectedValuePath = nameof(UiOption<string>.Value);
        ThemeBox.DisplayMemberPath = nameof(UiOption<string>.Text);
        ThemeBox.SelectedValuePath = nameof(UiOption<string>.Value);
        ThemeEditorThemeBox.DisplayMemberPath = nameof(UiOption<string>.Text);
        ThemeEditorThemeBox.SelectedValuePath = nameof(UiOption<string>.Value);
        UiFontScaleSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(UiFontScaleSlider_DragCompleted));
        UiFontScaleSlider.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(UiFontScaleSlider_MouseLeftButtonDown), true);
        UiFontScaleSlider.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(UiFontScaleSlider_MouseLeftButtonUp), true);
        RefreshOptionControls();
        RefreshThemeControls();
        AutoAddSourceBox.ItemsSource = _autoAddService.SupportedSources;
        AutoAddSourceBox.SelectedItem = AutoAddSource.Steam;
        SetCoverCrop(1, 0, 0);
        ApplyUiFontScale(_uiFontScale);
        ApplyCardScale(_cardScale);
        ShowScreen(AppScreen.Start, animate: false);
    }

    private async Task LoadSettingsAsync()
    {
        await using var db = CreateDb();
        BackupFolderBox.Text = await GetSettingAsync(db, AppSettingKeys.BackupFolder, AppPaths.DefaultBackupDirectory);

        var language = await GetSettingAsync(db, AppSettingKeys.Language, RussianLanguage);
        SetLanguage(language);

        var viewModeText = await GetSettingAsync(db, AppSettingKeys.ViewMode, LibraryViewMode.List.ToString());
        _viewMode = Enum.TryParse<LibraryViewMode>(viewModeText, out var viewMode) ? viewMode : LibraryViewMode.List;
        var cardScaleText = await GetSettingAsync(db, AppSettingKeys.CardScale, DefaultCardScale.ToString(CultureInfo.InvariantCulture));
        _cardScale = double.TryParse(cardScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var cardScale)
            ? Clamp(cardScale, MinCardScale, MaxCardScale)
            : DefaultCardScale;
        _suppressCardScaleSave = true;
        CardScaleSlider.Value = _cardScale;
        ApplyCardScale(_cardScale);
        _suppressCardScaleSave = false;

        var uiFontScaleText = await GetSettingAsync(db, AppSettingKeys.UiFontScale, DefaultUiFontScale.ToString(CultureInfo.InvariantCulture));
        _uiFontScale = double.TryParse(uiFontScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var uiFontScale)
            ? Clamp(uiFontScale, MinUiFontScale, MaxUiFontScale)
            : DefaultUiFontScale;
        _suppressUiFontScaleSave = true;
        UiFontScaleSlider.Value = _uiFontScale;
        ApplyUiFontScale(_uiFontScale);
        _suppressUiFontScaleSave = false;
        ApplyViewMode();

        await LoadCustomThemeAsync(db);
        RefreshThemeControls();
        var themeText = await GetSettingAsync(db, AppSettingKeys.Theme, AppThemeMode.Light.ToString());
        var themeKey = NormalizeThemeKey(themeText);
        ApplyTheme(themeKey);
        SelectThemeControls(themeKey);
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

        if (StatusFilterBox.SelectedValue is GameStatus status)
        {
            query = query.Where(game => game.Statuses.Any(item => item.Status == status));
        }

        query = SortBox.SelectedValue switch
        {
            SortMode.TitleDescending => query.OrderByDescending(game => game.Title),
            SortMode.Status => query.OrderBy(game => game.StatusText).ThenBy(game => game.Title),
            SortMode.Year => query.OrderByDescending(game => game.Year ?? 0).ThenBy(game => game.Title),
            SortMode.Created => query.OrderByDescending(game => game.CreatedAt),
            SortMode.Updated => query.OrderByDescending(game => game.UpdatedAt),
            _ => query.OrderBy(game => game.Title)
        };

        var selectedId = selectGameId ?? (GamesList.SelectedItem as GameListItem)?.Id;
        _gameItems.Clear();

        foreach (var game in query)
        {
            var imagePath = File.Exists(game.ImageLocalPath) ? game.ImageLocalPath : null;
            _gameItems.Add(new GameListItem
            {
                Id = game.Id,
                Title = game.Title,
                Subtitle = BuildSubtitle(game, pcServices, consoleModels),
                Statuses = FormatStatuses(game.Statuses.Select(item => item.Status)),
                YearText = game.Year?.ToString(CultureInfo.InvariantCulture) ?? "-",
                UpdatedText = string.Format(CultureInfo.CurrentCulture, T("UpdatedAt"), FormatTimestamp(game.UpdatedAt)),
                ImagePath = imagePath,
                CoverPreview = CreateCoverPreview(imagePath, game.ImageScale, game.ImageOffsetX, game.ImageOffsetY),
                ImageScale = game.ImageScale,
                ImageOffsetX = game.ImageOffsetX,
                ImageOffsetY = game.ImageOffsetY
            });
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            _suppressGameSelectionNavigation = true;
            try
            {
                GamesList.SelectedItem = _gameItems.FirstOrDefault(item => item.Id == selectedId);
            }
            finally
            {
                _suppressGameSelectionNavigation = false;
            }
        }
    }

    private string BuildSubtitle(
        Game game,
        IReadOnlyDictionary<string, string> pcServices,
        IReadOnlyDictionary<string, string> consoleModels)
    {
        var platform = PlatformName(game.PlatformType);
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

        await RunUiActionAsync(T("UnableFilterGames"), () => LoadGamesAsync());
    }

    private async void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _suppressGameSelectionNavigation || GamesList.SelectedItem is not GameListItem item)
        {
            return;
        }

        await OpenGameDetailsAsync(item.Id);
    }

    private async void GameItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_loading || sender is not ListBoxItem { IsSelected: true, DataContext: GameListItem item })
        {
            return;
        }

        await OpenGameDetailsAsync(item.Id);
    }

    private async Task OpenGameDetailsAsync(string gameId)
    {
        await RunUiActionAsync(T("UnableLoadGame"), async () =>
        {
            await LoadGameIntoEditorAsync(gameId);
            Navigate(AppScreen.Details);
        });
    }

    private async Task LoadGameIntoEditorAsync(string gameId)
    {
        await using var db = CreateDb();
        var game = await db.Games
            .Include(item => item.Statuses)
            .Include(item => item.ImageGallery)
            .FirstAsync(item => item.Id == gameId);
        var pcServices = await db.PcServices.ToDictionaryAsync(service => service.StableId, service => service.Name);
        var consoleModels = await db.ConsoleModels.ToDictionaryAsync(model => model.StableId, model => model.Name);

        _loading = true;
        try
        {
            _currentGameId = game.Id;
            IdBox.Text = game.Id;
            TitleBox.Text = game.Title;
            YearBox.Text = game.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            PlatformBox.SelectedValue = game.PlatformType;
            PcServiceBox.SelectedValue = game.PcServiceId;
            ConsoleModelBox.SelectedValue = game.ConsoleModelId;
            CoverUrlBox.Text = game.ImageSourceUrl ?? string.Empty;
            SourcePageUrlBox.Text = game.SourcePageUrl ?? string.Empty;
            _loadedCustomNotesStorage = game.CustomNotes;
            _loadedNotesDisplay = GameNotesSerializer.ToDisplayText(game.CustomNotes);
            NotesBox.Text = _loadedNotesDisplay;
            SetCoverCrop(game.ImageScale, game.ImageOffsetX, game.ImageOffsetY);
            SetStatusCheckboxes(game.Statuses.Select(status => status.Status).ToHashSet());
            SetCoverPreview(game.ImageLocalPath);
            var gallery = game.ImageGallery.OrderBy(image => image.SortOrder).ToList();
            GalleryList.ItemsSource = gallery;
            DetailsGalleryList.ItemsSource = gallery;
            DetailTitleText.Text = game.Title;
            DetailSubtitleText.Text = BuildSubtitle(game, pcServices, consoleModels);
            DetailStatusText.Text = FormatStatuses(game.Statuses.Select(status => status.Status));
            DetailYearText.Text = game.Year?.ToString(CultureInfo.InvariantCulture) ?? "-";
            DetailSourceText.Text = string.IsNullOrWhiteSpace(game.SourcePageUrl) ? "-" : game.SourcePageUrl;
            DetailNotesText.Text = string.IsNullOrWhiteSpace(_loadedNotesDisplay) ? "-" : _loadedNotesDisplay;
            ApplyPlatformControls();
        }
        finally
        {
            _loading = false;
        }
    }

    private void NewGameButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.AddMode);

    private void StartButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.Menu);

    private void OpenDatabaseButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.Database);

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.Settings);

    private void OpenImportExportButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.ImportExport);

    private void OpenReferencesButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.References);

    private void OpenThemeEditorButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.ThemeEditor);

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ManualAddButton_Click(object sender, RoutedEventArgs e)
    {
        NewGame();
        Navigate(AppScreen.ManualEdit);
    }

    private void AutoAddModeButton_Click(object sender, RoutedEventArgs e) => Navigate(AppScreen.AutoAdd);

    private void EditCurrentGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentGameId))
        {
            return;
        }

        Navigate(AppScreen.ManualEdit);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => Back();

    private void HomeButton_Click(object sender, RoutedEventArgs e) => Home();

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
            PlatformBox.SelectedValue = PlatformType.PC;
            PcServiceBox.SelectedIndex = PcServiceBox.Items.Count > 0 ? 0 : -1;
            ConsoleModelBox.SelectedIndex = -1;
            CoverUrlBox.Text = string.Empty;
            SourcePageUrlBox.Text = string.Empty;
            NotesBox.Text = string.Empty;
            _loadedCustomNotesStorage = null;
            _loadedNotesDisplay = string.Empty;
            SetCoverCrop(1, 0, 0);
            SetStatusCheckboxes(new HashSet<GameStatus> { GameStatus.PLANNED });
            SetCoverPreview(null);
            GalleryList.ItemsSource = null;
            DetailsGalleryList.ItemsSource = null;
            DetailTitleText.Text = T("New");
            DetailSubtitleText.Text = string.Empty;
            DetailStatusText.Text = StatusName(GameStatus.PLANNED);
            DetailYearText.Text = "-";
            DetailSourceText.Text = "-";
            DetailNotesText.Text = "-";
        }
        finally
        {
            _loading = false;
        }
    }

    private void Navigate(AppScreen screen)
    {
        if (_currentScreen == screen)
        {
            return;
        }

        _navigationStack.Push(_currentScreen);
        ShowScreen(screen);
    }

    private void ReplaceCurrent(AppScreen screen)
    {
        ShowScreen(screen);
    }

    private void Back()
    {
        if (_navigationStack.Count == 0)
        {
            Home();
            return;
        }

        ShowScreen(_navigationStack.Pop());
    }

    private void Home()
    {
        _navigationStack.Clear();
        ShowScreen(AppScreen.Start);
    }

    private void ShowScreen(AppScreen screen, bool animate = true)
    {
        _currentScreen = screen;
        foreach (var element in ScreenElements())
        {
            element.Visibility = Visibility.Collapsed;
        }

        var target = GetScreenElement(screen);
        target.Visibility = Visibility.Visible;
        if (animate)
        {
            target.Opacity = 0;
            target.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            target.Opacity = 1;
        }

        BackButton.Visibility = screen == AppScreen.Start ? Visibility.Collapsed : Visibility.Visible;
        HomeButton.Visibility = screen is AppScreen.Start or AppScreen.Menu ? Visibility.Collapsed : Visibility.Visible;
        ScreenTitleText.Text = ScreenTitle(screen);
        ScreenSubtitleText.Text = ScreenSubtitle(screen);
    }

    private IEnumerable<FrameworkElement> ScreenElements()
    {
        yield return StartScreen;
        yield return MenuScreen;
        yield return DatabaseScreen;
        yield return AddModeScreen;
        yield return ManualEditScreen;
        yield return AutoAddScreen;
        yield return DetailsScreen;
        yield return ImportExportScreen;
        yield return SettingsScreen;
        yield return ThemeEditorScreen;
        yield return ReferencesScreen;
    }

    private FrameworkElement GetScreenElement(AppScreen screen) => screen switch
    {
        AppScreen.Start => StartScreen,
        AppScreen.Menu => MenuScreen,
        AppScreen.Database => DatabaseScreen,
        AppScreen.AddMode => AddModeScreen,
        AppScreen.ManualEdit => ManualEditScreen,
        AppScreen.AutoAdd => AutoAddScreen,
        AppScreen.Details => DetailsScreen,
        AppScreen.ImportExport => ImportExportScreen,
        AppScreen.Settings => SettingsScreen,
        AppScreen.ThemeEditor => ThemeEditorScreen,
        AppScreen.References => ReferencesScreen,
        _ => StartScreen
    };

    private string ScreenTitle(AppScreen screen) => screen switch
    {
        AppScreen.Start => "RELIQUARY",
        AppScreen.Menu => T("Menu"),
        AppScreen.Database => T("Database"),
        AppScreen.AddMode => T("New record"),
        AppScreen.ManualEdit => T("Manual add"),
        AppScreen.AutoAdd => T("Add by link"),
        AppScreen.Details => T("Details"),
        AppScreen.ImportExport => T("Import / Export"),
        AppScreen.Settings => T("Settings"),
        AppScreen.ThemeEditor => T("Theme editor"),
        AppScreen.References => T("References"),
        _ => "RELIQUARY"
    };

    private string ScreenSubtitle(AppScreen screen) => screen switch
    {
        AppScreen.Start => T("Desktop library"),
        AppScreen.Menu => T("Choose action"),
        AppScreen.Database => T("Search, filter, sort, and open game cards"),
        AppScreen.AddMode => T("Choose how to add a game"),
        AppScreen.ManualEdit => T("Create or edit game metadata"),
        AppScreen.AutoAdd => T("Import game metadata from store pages"),
        AppScreen.Details => T("Cover, gallery, statuses, and notes"),
        AppScreen.ImportExport => T("JSON, ZIP, and Google Drive backups"),
        AppScreen.Settings => T("Themes, language, feedback, and maintenance"),
        AppScreen.ThemeEditor => T("Built-in and custom color modes"),
        AppScreen.References => T("Services, console families, and models"),
        _ => string.Empty
    };

    private async void SaveGameButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableSaveGame"), async () =>
        {
            var id = await SaveCurrentGameAsync();
            await LoadGamesAsync(id);
            await LoadGameIntoEditorAsync(id);
            SetStatus(T("GameSaved"));
            ReplaceCurrent(AppScreen.Details);
        });
    }

    private async void AutoAddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableImportStore"), async () =>
        {
            if (AutoAddSourceBox.SelectedItem is not AutoAddSource source)
            {
                throw new InvalidOperationException(T("ChooseStoreFirst"));
            }

            var reference = AutoAddReferenceBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(reference))
            {
                throw new InvalidOperationException(T("StoreReferenceRequired"));
            }

            SetStatus(string.Format(CultureInfo.CurrentCulture, T("ImportingFrom"), AutoAddSourceName(source)));
            var details = await _autoAddService.FetchGameDetailsAsync(new AutoAddRequest(source, reference));
            var result = await SaveImportedGameAsync(details);

            await LoadReferencesAsync();
            await LoadGamesAsync(result.GameId);
            if (!string.IsNullOrWhiteSpace(result.GameId))
            {
                await LoadGameIntoEditorAsync(result.GameId);
                ReplaceCurrent(AppScreen.Details);
            }

            AutoAddReferenceBox.Text = string.Empty;
            SetStatus(result.ImportedCount > 0
                ? string.Format(CultureInfo.CurrentCulture, T("ImportedFrom"), result.Title, AutoAddSourceName(source))
                : string.Format(CultureInfo.CurrentCulture, T("GameExistsImagesUpdated"), result.Title));
        });
    }

    private async Task<string> SaveCurrentGameAsync()
    {
        var title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException(T("TitleRequired"));
        }

        int? year = null;
        if (!string.IsNullOrWhiteSpace(YearBox.Text))
        {
            if (!int.TryParse(YearBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedYear))
            {
                throw new InvalidOperationException(T("YearMustBeNumber"));
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
        game.PlatformType = PlatformBox.SelectedValue is PlatformType platform ? platform : PlatformType.PC;
        game.PcServiceId = game.PlatformType == PlatformType.CONSOLE ? null : PcServiceBox.SelectedValue as string;
        game.ConsoleModelId = game.PlatformType == PlatformType.CONSOLE ? ConsoleModelBox.SelectedValue as string : null;
        game.ConsoleFamilyId = await ResolveConsoleFamilyIdAsync(db, game.ConsoleModelId);
        game.ImageSourceUrl = string.IsNullOrWhiteSpace(CoverUrlBox.Text) ? game.ImageSourceUrl : CoverUrlBox.Text.Trim();
        game.SourcePageUrl = EmptyToNull(SourcePageUrlBox.Text);
        game.CustomNotes = string.Equals(NotesBox.Text, _loadedNotesDisplay, StringComparison.Ordinal)
            ? _loadedCustomNotesStorage
            : GameNotesSerializer.ToStorageFromPlainText(NotesBox.Text);
        game.ImageScale = _imageScale;
        game.ImageOffsetX = _imageOffsetX;
        game.ImageOffsetY = _imageOffsetY;
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

        if (MessageBox.Show(this, T("DeleteSelectedGameQuestion"), T("ConfirmDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync(T("UnableDeleteGame"), async () =>
        {
            await using var db = CreateDb();
            var game = await db.Games.FindAsync(_currentGameId);
            if (game is not null)
            {
                db.Games.Remove(game);
                await db.SaveChangesAsync();
                await _imageService.DeleteUnreferencedImagesAsync(db);
            }

            await LoadGamesAsync();
            NewGame();
            SetStatus(T("GameDeleted"));
            ReplaceCurrent(AppScreen.Database);
        });
    }

    private async void AddCoverLocalButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickImageFile();
        if (path is null)
        {
            return;
        }

        await RunUiActionAsync(T("UnableAddCover"), async () =>
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

        await RunUiActionAsync(T("UnableDownloadCover"), async () =>
        {
            var progress = CreateDownloadProgress(T("DownloadCoverProgress"));
            try
            {
                var downloaded = await _imageService.DownloadImageAsync(url, progress);
                await UpdateCoverAsync(downloaded, Path.GetFileName(downloaded), url, ImageSourceType.DIRECT_IMAGE_URL);
            }
            finally
            {
                ClearDownloadProgress();
            }
        });
    }

    private async void ReloadCoverButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableReloadCover"), async () =>
        {
            var id = await EnsureSavedGameAsync();
            await using var db = CreateDb();
            var game = await db.Games.FindAsync(id) ?? throw new InvalidOperationException(T("GameNotFound"));
            if (string.IsNullOrWhiteSpace(game.ImageSourceUrl))
            {
                throw new InvalidOperationException(T("CoverHasNoSourceUrl"));
            }

            var progress = CreateDownloadProgress(T("DownloadCoverProgress"));
            try
            {
                var downloaded = await _imageService.DownloadImageAsync(game.ImageSourceUrl, progress);
                await UpdateCoverAsync(downloaded, Path.GetFileName(downloaded), game.ImageSourceUrl, ImageSourceType.DIRECT_IMAGE_URL);
            }
            finally
            {
                ClearDownloadProgress();
            }
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
        var game = await db.Games.FindAsync(id) ?? throw new InvalidOperationException(T("GameNotFound"));
        game.ImageLocalPath = localPath;
        game.ImageArchiveName = archiveName;
        game.ImageSourceUrl = sourceUrl ?? game.ImageSourceUrl;
        game.ImageSourceType = sourceType;
        game.ImageScale = 1;
        game.ImageOffsetX = 0;
        game.ImageOffsetY = 0;
        game.UpdatedAt = Clock.UnixMillisecondsNow();
        await db.SaveChangesAsync();
        await _imageService.DeleteUnreferencedImagesAsync(db);
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

        await RunUiActionAsync(T("UnableAddGalleryImage"), async () =>
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

        await RunUiActionAsync(T("UnableDownloadGalleryImage"), async () =>
        {
            var progress = CreateDownloadProgress(T("DownloadGalleryProgress"));
            try
            {
                var downloaded = await _imageService.DownloadImageAsync(url, progress);
                await AddGalleryImageAsync(downloaded, Path.GetFileName(downloaded), url, ImageSourceType.DIRECT_IMAGE_URL);
                GalleryUrlBox.Text = string.Empty;
            }
            finally
            {
                ClearDownloadProgress();
            }
        });
    }

    private async Task AddGalleryImageAsync(string localPath, string archiveName, string? sourceUrl, ImageSourceType sourceType)
    {
        var id = await EnsureSavedGameAsync();
        await using var db = CreateDb();
        var count = await db.GameImages.CountAsync(image => image.GameId == id);
        if (count >= 20)
        {
            throw new InvalidOperationException(T("GalleryLimit"));
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

        await RunUiActionAsync(T("UnableSetGalleryCover"), async () =>
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

        await RunUiActionAsync(T("UnableRemoveGalleryImage"), async () =>
        {
            await using var db = CreateDb();
            var stored = await db.GameImages.FindAsync(image.Id);
            if (stored is not null)
            {
                db.GameImages.Remove(stored);
                await db.SaveChangesAsync();
                await _imageService.DeleteUnreferencedImagesAsync(db);
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
            MessageBox.Show(this, T("ChooseConsoleFamilyFirst"), T("Validation"), MessageBoxButton.OK, MessageBoxImage.Information);
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
        await RunUiActionAsync(T("UnableAddReference"), async () =>
        {
            var name = sourceBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(T("NameRequired"));
            }

            await using var db = CreateDb();
            await add(db, Slugify(name), name);
            sourceBox.Text = string.Empty;
            await LoadReferencesAsync();
            SetStatus(T("ReferenceAdded"));
        });
    }

    private async void ChooseBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = T("ChooseBackupFolderTitle"),
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
        await RunUiActionAsync(T("UnableExportJson"), async () =>
        {
            var path = BuildLocalBackupPath("json");
            await _transferService.ExportJsonAsync(path);
            SetStatus(string.Format(CultureInfo.CurrentCulture, T("ExportedJson"), path));
        });
    }

    private async void ExportZipButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableExportZip"), async () =>
        {
            var path = BuildLocalBackupPath("zip");
            await _transferService.ExportZipAsync(path);
            SetStatus(string.Format(CultureInfo.CurrentCulture, T("ExportedZip"), path));
        });
    }

    private async void ImportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickOpenFile(T("JsonFileFilter"));
        if (path is null)
        {
            return;
        }

        await ImportFileAsync(path, isZip: false);
    }

    private async void ImportZipButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickOpenFile(T("ZipFileFilter"));
        if (path is null)
        {
            return;
        }

        await ImportFileAsync(path, isZip: true);
    }

    private async Task ImportFileAsync(string path, bool isZip)
    {
        await RunUiActionAsync(T("UnableImportLibrary"), async () =>
        {
            var result = isZip
                ? await _transferService.ImportZipAsync(path, ResolveImportConflict)
                : await _transferService.ImportJsonAsync(path, ResolveImportConflict);
            await LoadReferencesAsync();
            await LoadGamesAsync();
            await CleanupUnreferencedImagesAsync();
            SetStatus(FormatImportSummary(result));
        });
    }

    private ImportConflictDecision ResolveImportConflict(ImportConflictInfo conflict)
    {
        var dialog = new ImportConflictDialog(conflict, _language) { Owner = this };
        dialog.ShowDialog();
        return dialog.Decision;
    }

    private async void ClearImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, T("ClearImageCacheQuestion"), T("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync(T("UnableClearImageCache"), async () =>
        {
            var cleanup = await CleanupUnreferencedImagesAsync();
            await LoadGamesAsync(_currentGameId);
            if (!string.IsNullOrWhiteSpace(_currentGameId))
            {
                await LoadGameIntoEditorAsync(_currentGameId);
            }

            SetStatus(FormatImageCleanupSummary(cleanup));
        });
    }

    private async void ClearLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, T("ClearLibraryQuestion"), T("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync(T("UnableClearLibrary"), async () =>
        {
            await using var db = CreateDb();
            db.Games.RemoveRange(db.Games);
            await db.SaveChangesAsync();
            await _imageService.DeleteUnreferencedImagesAsync(db);
            await LoadGamesAsync();
            NewGame();
            SetStatus(T("LocalLibraryCleared"));
        });
    }

    private async void ConnectDriveButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableConnectDrive"), async () =>
        {
            SetStatus(T("OpeningBrowserSignIn"));
            await _driveService.ConnectAsync();
            _driveAuthWarningShown = false;
            await RefreshDriveBackupsAsync();
            SetStatus(T("DriveConnected"));
        });
    }

    private void DisconnectDriveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _driveService.Disconnect();
            DriveBackupsGrid.ItemsSource = null;
            SetStatus(T("DriveDisconnected"));
        }
        catch (Exception ex)
        {
            ShowError(T("UnableDisconnectDrive"), ex);
        }
    }

    private async void UploadDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableUploadBackup"), async () =>
        {
            if (!await EnsureDriveConnectedAsync())
            {
                return;
            }

            var path = BuildLocalBackupPath("zip");
            await _transferService.ExportZipAsync(path);
            await _driveService.UploadZipBackupAsync(path);
            await RefreshDriveBackupsAsync();
            SetStatus(T("ZipUploadedDrive"));
        });
    }

    private async void AutoExportDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableAutoExportDrive"), async () =>
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
                SetStatus(T("TempZipUploadedDrive"));
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
        await RunUiActionAsync(T("UnableRefreshBackups"), async () =>
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
        SetStatus(string.Format(CultureInfo.CurrentCulture, T("LoadedDriveBackups"), backups.Count));
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
        await RunUiActionAsync(T("UnableRestoreLatestBackup"), async () =>
        {
            if (!await EnsureDriveConnectedAsync())
            {
                return;
            }

            var backups = (await _driveService.ListBackupsAsync()).ToList();
            if (backups.Count == 0)
            {
                throw new InvalidOperationException(T("NoDriveBackups"));
            }

            await RestoreDriveBackupCoreAsync(backups[0]);
        });
    }

    private async Task RestoreDriveBackupAsync(DriveBackupFile backup)
    {
        await RunUiActionAsync(T("UnableRestoreBackup"), async () =>
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
        var progress = CreateDownloadProgress(T("DownloadBackupProgress"));
        try
        {
            await _driveService.DownloadBackupAsync(backup.Id, localPath, backup.Size, progress);
        }
        finally
        {
            ClearDownloadProgress();
        }

        SetStatus(T("ImportingBackup"));
        var result = await _transferService.ImportZipAsync(localPath, ResolveImportConflict);
        await LoadReferencesAsync();
        await LoadGamesAsync();
        await CleanupUnreferencedImagesAsync();
        SetStatus(FormatImportSummary(result));
    }

    private async void TrashDriveBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBackupsGrid.SelectedItem is not DriveBackupFile backup)
        {
            return;
        }

        if (MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, T("MoveBackupToTrashQuestion"), backup.Name), T("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync(T("UnableMoveBackupTrash"), async () =>
        {
            if (!await EnsureDriveConnectedAsync())
            {
                return;
            }

            await _driveService.TrashBackupAsync(backup.Id);
            await RefreshDriveBackupsAsync();
            SetStatus(T("BackupMovedTrash"));
        });
    }

    private async Task TryAutoConnectDriveAsync()
    {
        if (await _driveService.TryConnectWithStoredTokenAsync())
        {
            await RefreshDriveBackupsAsync();
            SetStatus(T("DriveConnectedAutomatically"));
            return;
        }

        if (_driveService.HasStoredToken)
        {
            ShowDriveAuthWarningOnce(T("DriveTokenInvalid"));
        }
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
                ? T("DriveTokenInvalid")
                : T("DriveNotAuthenticatedAction"));
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

    private void ApplyCardScale(double scale)
    {
        _cardScale = Clamp(scale, MinCardScale, MaxCardScale);
        Resources["GameCardPadding"] = new Thickness(10 * _cardScale);
        Resources["GameCardMargin"] = new Thickness(0, 0, 0, 10 * _cardScale);
        Resources["GameCardContentMargin"] = new Thickness(12 * _cardScale, 0, 0, 0);
        Resources["GameCardSubtitleMargin"] = new Thickness(0, 4 * _cardScale, 0, 0);
        Resources["GameCardChipMargin"] = new Thickness(0, 8 * _cardScale, 0, 0);
        Resources["GameCardUpdatedMargin"] = new Thickness(0, 8 * _cardScale, 0, 0);
        var coverWidth = 68 * _cardScale;
        Resources["GameCardCoverWidth"] = coverWidth;
        Resources["GameCardCoverHeight"] = coverWidth / DefaultCoverAspectRatio;
        Resources["GameCardHeight"] = 132 * _cardScale;
        Resources["GameCardTitleFontSize"] = 15 * _cardScale;
        Resources["GameCardSmallFontSize"] = 11 * _cardScale;
        if (CardScaleValueText is not null)
        {
            CardScaleValueText.Text = $"{Math.Round(_cardScale * 100, MidpointRounding.AwayFromZero):0}%";
        }

        if (GamesList is not null)
        {
            GamesList.ItemContainerStyle = CreateGameItemStyle(_viewMode);
        }
    }

    private async void CardScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CardScaleValueText is null)
        {
            return;
        }

        ApplyCardScale(e.NewValue);
        if (_suppressCardScaleSave)
        {
            return;
        }

        await SaveCardScaleAsync();
    }

    private void DecreaseCardScaleButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustCardScaleByPercent(-1);
    }

    private void IncreaseCardScaleButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustCardScaleByPercent(1);
    }

    private void AdjustCardScaleByPercent(int percentDelta)
    {
        var currentPercent = (int)Math.Round(_cardScale * 100, MidpointRounding.AwayFromZero);
        var nextScale = Clamp((currentPercent + percentDelta) / 100d, MinCardScale, MaxCardScale);
        CardScaleSlider.Value = nextScale;
    }

    private Style CreateGameItemStyle(LibraryViewMode mode)
    {
        var baseStyle = TryFindResource(typeof(ListBoxItem)) as Style;
        var style = baseStyle is null
            ? new Style(typeof(ListBoxItem))
            : new Style(typeof(ListBoxItem), baseStyle);
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new EventSetter(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(GameItem_PreviewMouseLeftButtonDown)));
        if (mode == LibraryViewMode.Tiles)
        {
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 250d * _cardScale));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 10 * _cardScale, 10 * _cardScale)));
        }

        return style;
    }

    private async Task SaveViewModeAsync()
    {
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.ViewMode, _viewMode.ToString());
    }

    private async Task SaveCardScaleAsync()
    {
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.CardScale, _cardScale.ToString(CultureInfo.InvariantCulture));
    }

    private void ApplyUiFontScale(double scale)
    {
        _uiFontScale = Clamp(scale, MinUiFontScale, MaxUiFontScale);
        Resources["AppBodyFontSize"] = 14 * _uiFontScale;
        Resources["AppSmallFontSize"] = 11 * _uiFontScale;
        Resources["AppLabelFontSize"] = 12 * _uiFontScale;
        Resources["AppMutedFontSize"] = 13 * _uiFontScale;
        Resources["AppLargeFontSize"] = 18 * _uiFontScale;
        Resources["AppScreenTitleFontSize"] = 20 * _uiFontScale;
        Resources["AppSectionTitleFontSize"] = 22 * _uiFontScale;
        Resources["AppMenuButtonFontSize"] = 24 * _uiFontScale;
        Resources["AppDetailTitleFontSize"] = 34 * _uiFontScale;
        Resources["AppHeroFontSize"] = 68 * _uiFontScale;
        Resources["AppControlMinHeight"] = 38 * _uiFontScale;
        Resources["AppHeaderControlHeight"] = 36 * _uiFontScale;
        Resources["AppStartButtonHeight"] = 48 * _uiFontScale;
        Resources["AppColorSwatchWidth"] = 38 * _uiFontScale;
        Resources["AppScaleStepButtonWidth"] = 42 * _uiFontScale;
        Resources["AppScaleValueColumnWidth"] = 76 * _uiFontScale;
        Resources["AppFontScaleGridWidth"] = Math.Min(1040d, 620d + 210d * (_uiFontScale - DefaultUiFontScale));
        Resources["AppCardScaleControlFontSize"] = 13 * Math.Min(_uiFontScale, MaxCardScale);
        Resources["AppButtonPadding"] = new Thickness(16 * _uiFontScale, 9 * _uiFontScale, 16 * _uiFontScale, 9 * _uiFontScale);
        Resources["AppHeaderButtonPadding"] = new Thickness(14 * _uiFontScale, 0, 14 * _uiFontScale, 0);
        Resources["AppInputPadding"] = new Thickness(10 * _uiFontScale, 6 * _uiFontScale, 10 * _uiFontScale, 6 * _uiFontScale);
        Resources["AppComboBoxPadding"] = new Thickness(8 * _uiFontScale, 5 * _uiFontScale, 8 * _uiFontScale, 5 * _uiFontScale);
        Resources["AppComboBoxItemPadding"] = new Thickness(10 * _uiFontScale, 7 * _uiFontScale, 10 * _uiFontScale, 7 * _uiFontScale);
        SetUiFontScaleValueText(_uiFontScale);
    }

    private async void UiFontScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (UiFontScaleValueText is null)
        {
            return;
        }

        if (_deferUiFontScaleApply)
        {
            SetUiFontScaleValueText(e.NewValue);
            return;
        }

        ApplyUiFontScale(e.NewValue);
        if (_suppressUiFontScaleSave)
        {
            return;
        }

        await SaveUiFontScaleAsync();
    }

    private void UiFontScaleSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _deferUiFontScaleApply = true;
    }

    private async void UiFontScaleSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await ApplyDeferredUiFontScaleAsync();
    }

    private async void UiFontScaleSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        await ApplyDeferredUiFontScaleAsync();
    }

    private async Task ApplyDeferredUiFontScaleAsync()
    {
        if (!_deferUiFontScaleApply)
        {
            return;
        }

        _deferUiFontScaleApply = false;
        ApplyUiFontScale(UiFontScaleSlider.Value);
        if (!_suppressUiFontScaleSave)
        {
            await SaveUiFontScaleAsync();
        }
    }

    private void DecreaseUiFontScaleButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustUiFontScaleByPercent(-1);
    }

    private void IncreaseUiFontScaleButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustUiFontScaleByPercent(1);
    }

    private void ResetUiFontScaleButton_Click(object sender, RoutedEventArgs e)
    {
        UiFontScaleSlider.Value = DefaultUiFontScale;
    }

    private void AdjustUiFontScaleByPercent(int percentDelta)
    {
        var currentPercent = (int)Math.Round(_uiFontScale * 100, MidpointRounding.AwayFromZero);
        var nextScale = Clamp((currentPercent + percentDelta) / 100d, MinUiFontScale, MaxUiFontScale);
        UiFontScaleSlider.Value = nextScale;
    }

    private async Task SaveUiFontScaleAsync()
    {
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.UiFontScale, _uiFontScale.ToString(CultureInfo.InvariantCulture));
    }

    private void SetUiFontScaleValueText(double scale)
    {
        if (UiFontScaleValueText is not null)
        {
            UiFontScaleValueText.Text = $"{Math.Round(Clamp(scale, MinUiFontScale, MaxUiFontScale) * 100, MidpointRounding.AwayFromZero):0}%";
        }
    }

    private async void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _updatingThemeControls || _transferService is null)
        {
            return;
        }

        var themeKey = sender switch
        {
            ComboBox box when box.SelectedValue is string selectedThemeKey => selectedThemeKey,
            _ => _currentThemeKey
        };

        await ApplyAndSaveThemeAsync(themeKey);
    }

    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedValue is not string language || string.Equals(language, _language, StringComparison.Ordinal))
        {
            return;
        }

        SetLanguage(language);
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.Language, _language);
        if (_transferService is not null)
        {
            await LoadGamesAsync(_currentGameId);
        }

        SetStatus(T("LanguageChanged"));
    }

    private async Task LoadCustomThemeAsync(GameVaultDbContext db)
    {
        _customThemes.Clear();
        var customThemesSetting = await db.AppSettings.FindAsync(AppSettingKeys.CustomThemes);
        if (!string.IsNullOrWhiteSpace(customThemesSetting?.Value))
        {
            try
            {
                var themes = JsonSerializer.Deserialize<List<ThemePalette>>(customThemesSetting.Value, ThemeJsonOptions);
                if (themes is not null)
                {
                    _customThemes.AddRange(themes.Where(IsCompleteTheme));
                }
            }
            catch
            {
                _customThemes.Clear();
            }
        }
        else if (customThemesSetting is null)
        {
            _customThemes.Add(new ThemePalette(
                CreateThemeId(await GetSettingAsync(db, AppSettingKeys.CustomThemeName, "Custom")),
                await GetSettingAsync(db, AppSettingKeys.CustomThemeName, "Custom"),
                await GetSettingAsync(db, AppSettingKeys.CustomThemeBackground, "#10131F"),
                await GetSettingAsync(db, AppSettingKeys.CustomThemeSurface, "#171A29"),
                await GetSettingAsync(db, AppSettingKeys.CustomThemePanel, "#0D101A"),
                await GetSettingAsync(db, AppSettingKeys.CustomThemeText, "#F7F7FF"),
                await GetSettingAsync(db, AppSettingKeys.CustomThemeMuted, "#A9B1C7"),
                await GetSettingAsync(db, AppSettingKeys.CustomThemePrimary, "#7CF7C7"),
                await GetSettingAsync(db, AppSettingKeys.CustomThemeSecondary, "#F7D56E")));
        }

        _customTheme = _customThemes.FirstOrDefault() ?? ThemePalette.DefaultCustom with { Id = CreateThemeId("Custom") };
        UpdateCustomThemeFields();
    }

    private async Task ApplyAndSaveThemeAsync(string themeKey)
    {
        ApplyTheme(themeKey);
        SelectThemeControls(themeKey);
        await using var db = CreateDb();
        await SetSettingAsync(db, AppSettingKeys.Theme, _currentThemeKey);
    }

    private void ApplyTheme(string themeKey)
    {
        _currentThemeKey = NormalizeThemeKey(themeKey);
        var palette = GetThemePalette(_currentThemeKey);
        SetBrush("AppBackgroundBrush", palette.Background);
        SetBrush("SurfaceBrush", palette.Surface);
        SetBrush("SurfaceAltBrush", Shade(palette.Surface, 1.12));
        SetBrush("PanelBrush", palette.Panel);
        SetBrush("TextBrush", palette.Text);
        SetBrush("MutedTextBrush", palette.Muted);
        SetBrush("BorderBrushSoft", Blend(palette.Primary, palette.Surface, 0.55));
        SetBrush("PrimaryBrush", palette.Primary);
        SetBrush("PrimaryHoverBrush", Shade(palette.Primary, 1.18));
        SetBrush("PrimaryPressedBrush", Shade(palette.Primary, 0.72));
        SetBrush("SecondaryBrush", palette.Secondary);
        SetBrush("ButtonTextBrush", palette.ButtonText);
        SetBrush("DangerBrush", palette.Danger);
        SetBrush("InputBrush", Shade(palette.Panel, 1.08));
        SetBrush("InputTextBrush", palette.Text);
        SetBrush("ChipBrush", Blend(palette.Primary, palette.Panel, 0.22));
        var gameCardBackground = Blend(palette.Text, palette.Surface, 0.08);
        SetBrush("GameCardBrush", gameCardBackground);
        SetBrush("GameCardHoverBrush", Blend(palette.Primary, gameCardBackground, 0.18));
        Background = (Brush)Resources["AppBackgroundBrush"];
    }

    private ThemePalette GetThemePalette(string themeKey)
    {
        var custom = GetCustomTheme(themeKey);
        if (custom is not null)
        {
            return custom;
        }

        return themeKey switch
        {
            nameof(AppThemeMode.Dark) => new(nameof(AppThemeMode.Dark), "Dark", "#07111F", "#111E31", "#0A1424", "#F8FAFC", "#94A3B8", "#38F2C2", "#FACC15"),
            nameof(AppThemeMode.Oled) => new(nameof(AppThemeMode.Oled), "OLED", "#000000", "#070707", "#000000", "#FFFFFF", "#9CA3AF", "#00F5D4", "#E879F9"),
            nameof(AppThemeMode.Cyberpunk) => new(nameof(AppThemeMode.Cyberpunk), "Cyberpunk", "#10091A", "#1B1029", "#0C0714", "#FDF4FF", "#D8B4FE", "#F8E71C", "#00E5FF", "#FF2D95"),
            nameof(AppThemeMode.HalfLife) => new(nameof(AppThemeMode.HalfLife), "Half-Life", "#130E08", "#21170D", "#0C0905", "#FFF7ED", "#D6B28C", "#FF7A18", "#80C342"),
            _ => new(nameof(AppThemeMode.Light), "Light", "#EEF2F7", "#FFFFFF", "#F8FAFC", "#0F172A", "#475569", "#0F766E", "#B45309", "#DC2626", "#FFFFFF")
        };
    }

    private void SetBrush(string key, string color)
    {
        Resources[key] = new SolidColorBrush(ParseColor(color));
    }

    private static Color ParseColor(string color)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(color);
        }
        catch
        {
            return Colors.Magenta;
        }
    }

    private static string Shade(string color, double factor)
    {
        var parsed = ParseColor(color);
        return ColorToHex(Color.FromRgb(
            (byte)Math.Clamp(parsed.R * factor, 0, 255),
            (byte)Math.Clamp(parsed.G * factor, 0, 255),
            (byte)Math.Clamp(parsed.B * factor, 0, 255)));
    }

    private static string Blend(string foreground, string background, double amount)
    {
        var fg = ParseColor(foreground);
        var bg = ParseColor(background);
        return ColorToHex(Color.FromRgb(
            (byte)(bg.R + (fg.R - bg.R) * amount),
            (byte)(bg.G + (fg.G - bg.G) * amount),
            (byte)(bg.B + (fg.B - bg.B) * amount)));
    }

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void RefreshThemeControls()
    {
        if (!ThemeKeyExists(_currentThemeKey))
        {
            _currentThemeKey = nameof(AppThemeMode.Light);
        }

        var themes = new List<UiOption<string>>
        {
            new(nameof(AppThemeMode.Light), T("Light")),
            new(nameof(AppThemeMode.Dark), T("Dark")),
            new(nameof(AppThemeMode.Oled), "OLED"),
            new(nameof(AppThemeMode.Cyberpunk), "Cyberpunk"),
            new(nameof(AppThemeMode.HalfLife), "Half-Life")
        };
        themes.AddRange(_customThemes.Select(theme => new UiOption<string>(GetCustomThemeKey(theme.Id), theme.Name)));

        _updatingThemeControls = true;
        try
        {
            StartThemeBox.ItemsSource = themes;
            ThemeBox.ItemsSource = themes;
            ThemeEditorThemeBox.ItemsSource = themes;
            SelectThemeControls(_currentThemeKey);
        }
        finally
        {
            _updatingThemeControls = false;
        }
    }

    private void SelectThemeControls(string themeKey)
    {
        _updatingThemeControls = true;
        try
        {
            var normalizedThemeKey = NormalizeThemeKey(themeKey);
            StartThemeBox.SelectedValue = normalizedThemeKey;
            ThemeBox.SelectedValue = normalizedThemeKey;
            ThemeEditorThemeBox.SelectedValue = normalizedThemeKey;
        }
        finally
        {
            _updatingThemeControls = false;
        }
    }

    private void UpdateCustomThemeFields()
    {
        if (CustomThemeNameBox is null)
        {
            return;
        }

        CustomThemeNameBox.Text = _customTheme.Name;
        CustomThemeBackgroundBox.Text = _customTheme.Background;
        CustomThemeSurfaceBox.Text = _customTheme.Surface;
        CustomThemePanelBox.Text = _customTheme.Panel;
        CustomThemeTextBox.Text = _customTheme.Text;
        CustomThemeMutedBox.Text = _customTheme.Muted;
        CustomThemePrimaryBox.Text = _customTheme.Primary;
        CustomThemeSecondaryBox.Text = _customTheme.Secondary;
        UpdateColorSwatches();
    }

    private async void ApplyThemeFromEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeEditorThemeBox.SelectedValue is string themeKey)
        {
            await ApplyAndSaveThemeAsync(themeKey);
        }
    }

    private void ThemeEditorThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingThemeControls || ThemeEditorThemeBox.SelectedValue is not string themeKey)
        {
            return;
        }

        var sourceTheme = GetThemePalette(themeKey);
        _customTheme = IsCustomThemeKey(themeKey)
            ? sourceTheme
            : sourceTheme with { Id = CreateThemeId(sourceTheme.Name), Name = CreateCopyThemeName(sourceTheme.Name) };
        UpdateCustomThemeFields();
    }

    private void NewCustomThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _customTheme = ThemePalette.DefaultCustom with
        {
            Id = CreateThemeId("Custom"),
            Name = CreateCopyThemeName("Custom")
        };
        _updatingThemeControls = true;
        try
        {
            ThemeEditorThemeBox.SelectedIndex = -1;
        }
        finally
        {
            _updatingThemeControls = false;
        }

        UpdateCustomThemeFields();
    }

    private async void SaveCustomThemeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableSaveTheme"), async () =>
        {
            var selectedThemeKey = ThemeEditorThemeBox.SelectedValue as string;
            var themeId = selectedThemeKey is not null && IsCustomThemeKey(selectedThemeKey) && GetCustomTheme(selectedThemeKey) is not null
                ? selectedThemeKey[CustomThemeKeyPrefix.Length..]
                : CreateThemeId(CustomThemeNameBox.Text);
            _customTheme = new ThemePalette(
                themeId,
                string.IsNullOrWhiteSpace(CustomThemeNameBox.Text) ? "Custom" : CustomThemeNameBox.Text.Trim(),
                NormalizeColor(CustomThemeBackgroundBox.Text),
                NormalizeColor(CustomThemeSurfaceBox.Text),
                NormalizeColor(CustomThemePanelBox.Text),
                NormalizeColor(CustomThemeTextBox.Text),
                NormalizeColor(CustomThemeMutedBox.Text),
                NormalizeColor(CustomThemePrimaryBox.Text),
                NormalizeColor(CustomThemeSecondaryBox.Text));

            var existingIndex = _customThemes.FindIndex(theme => string.Equals(theme.Id, _customTheme.Id, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                _customThemes[existingIndex] = _customTheme;
            }
            else
            {
                _customThemes.Add(_customTheme);
            }

            await using var db = CreateDb();
            await SaveCustomThemeSettingsAsync(db);
            RefreshThemeControls();
            var customThemeKey = GetCustomThemeKey(_customTheme.Id);
            ApplyTheme(customThemeKey);
            SelectThemeControls(customThemeKey);
            await SetSettingAsync(db, AppSettingKeys.Theme, customThemeKey);
            SetStatus(T("CustomThemeSaved"));
        });
    }

    private async void DeleteCustomThemeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(T("UnableDeleteTheme"), async () =>
        {
            if (ThemeEditorThemeBox.SelectedValue is not string selectedThemeKey || !IsCustomThemeKey(selectedThemeKey))
            {
                return;
            }

            _customThemes.RemoveAll(theme => string.Equals(GetCustomThemeKey(theme.Id), selectedThemeKey, StringComparison.Ordinal));
            var deletedActiveTheme = string.Equals(_currentThemeKey, selectedThemeKey, StringComparison.Ordinal);
            _customTheme = _customThemes.FirstOrDefault() ?? ThemePalette.DefaultCustom with { Id = CreateThemeId("Custom") };
            await using var db = CreateDb();
            await SaveCustomThemeSettingsAsync(db);
            RefreshThemeControls();
            var nextThemeKey = deletedActiveTheme ? nameof(AppThemeMode.Light) : _currentThemeKey;
            ApplyTheme(nextThemeKey);
            SelectThemeControls(nextThemeKey);
            UpdateCustomThemeFields();
            await SetSettingAsync(db, AppSettingKeys.Theme, _currentThemeKey);
            SetStatus(T("CustomThemeDeleted"));
        });
    }

    private async Task SaveCustomThemeSettingsAsync(GameVaultDbContext db)
    {
        await SetSettingAsync(db, AppSettingKeys.CustomThemes, JsonSerializer.Serialize(_customThemes, ThemeJsonOptions));
        await SetSettingAsync(db, AppSettingKeys.CustomThemeName, _customTheme.Name);
        await SetSettingAsync(db, AppSettingKeys.CustomThemeBackground, _customTheme.Background);
        await SetSettingAsync(db, AppSettingKeys.CustomThemeSurface, _customTheme.Surface);
        await SetSettingAsync(db, AppSettingKeys.CustomThemePanel, _customTheme.Panel);
        await SetSettingAsync(db, AppSettingKeys.CustomThemeText, _customTheme.Text);
        await SetSettingAsync(db, AppSettingKeys.CustomThemeMuted, _customTheme.Muted);
        await SetSettingAsync(db, AppSettingKeys.CustomThemePrimary, _customTheme.Primary);
        await SetSettingAsync(db, AppSettingKeys.CustomThemeSecondary, _customTheme.Secondary);
    }

    private static string NormalizeColor(string value)
    {
        var color = ParseColor(value.Trim());
        return ColorToHex(color);
    }

    private string NormalizeThemeKey(string? themeKey)
    {
        if (string.IsNullOrWhiteSpace(themeKey))
        {
            return nameof(AppThemeMode.Light);
        }

        if (string.Equals(themeKey, nameof(AppThemeMode.Custom), StringComparison.OrdinalIgnoreCase))
        {
            return _customThemes.Count > 0 ? GetCustomThemeKey(_customThemes[0].Id) : nameof(AppThemeMode.Light);
        }

        if (IsCustomThemeKey(themeKey))
        {
            return GetCustomTheme(themeKey) is not null ? themeKey : nameof(AppThemeMode.Light);
        }

        return Enum.TryParse<AppThemeMode>(themeKey, ignoreCase: true, out var theme) && theme != AppThemeMode.Custom
            ? theme.ToString()
            : nameof(AppThemeMode.Light);
    }

    private bool ThemeKeyExists(string themeKey) =>
        IsCustomThemeKey(themeKey)
            ? GetCustomTheme(themeKey) is not null
            : Enum.TryParse<AppThemeMode>(themeKey, out var theme) && theme != AppThemeMode.Custom;

    private ThemePalette? GetCustomTheme(string? themeKey)
    {
        if (!IsCustomThemeKey(themeKey))
        {
            return null;
        }

        var id = themeKey![CustomThemeKeyPrefix.Length..];
        return _customThemes.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.Ordinal));
    }

    private static bool IsCustomThemeKey(string? themeKey) =>
        themeKey?.StartsWith(CustomThemeKeyPrefix, StringComparison.Ordinal) == true;

    private static string GetCustomThemeKey(string id) => $"{CustomThemeKeyPrefix}{id}";

    private static bool IsCompleteTheme(ThemePalette theme) =>
        !string.IsNullOrWhiteSpace(theme.Id)
        && !string.IsNullOrWhiteSpace(theme.Name)
        && !string.IsNullOrWhiteSpace(theme.Background)
        && !string.IsNullOrWhiteSpace(theme.Surface)
        && !string.IsNullOrWhiteSpace(theme.Panel)
        && !string.IsNullOrWhiteSpace(theme.Text)
        && !string.IsNullOrWhiteSpace(theme.Muted)
        && !string.IsNullOrWhiteSpace(theme.Primary)
        && !string.IsNullOrWhiteSpace(theme.Secondary);

    private string CreateCopyThemeName(string baseName)
    {
        var root = string.IsNullOrWhiteSpace(baseName) ? "Custom" : baseName.Trim();
        var candidate = root;
        var index = 2;
        while (_customThemes.Any(theme => string.Equals(theme.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{root} {index}";
            index++;
        }

        return candidate;
    }

    private static string CreateThemeId(string? name) =>
        $"{Slugify(string.IsNullOrWhiteSpace(name) ? "custom" : name)}-{Guid.NewGuid():N}";

    private void CustomThemeColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingColorPicker)
        {
            return;
        }

        UpdateColorSwatches();
        if (sender is TextBox textBox && ReferenceEquals(textBox, _activeColorTextBox) && ColorPickerPopup.IsOpen)
        {
            SetPickerFromColor(ParseColor(textBox.Text), updateTextBox: false);
        }
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not TextBox textBox)
        {
            return;
        }

        _activeColorTextBox = textBox;
        ColorPickerPopup.PlacementTarget = button;
        SetPickerFromColor(ParseColor(textBox.Text), updateTextBox: false);
        ColorPickerPopup.IsOpen = true;
    }

    private void SaturationValueImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SaturationValueImage.CaptureMouse();
        UpdateSaturationValueFromPoint(e.GetPosition(SaturationValueImage));
    }

    private void SaturationValueImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSaturationValueFromPoint(e.GetPosition(SaturationValueImage));
        }
    }

    private void HueImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueImage.CaptureMouse();
        UpdateHueFromPoint(e.GetPosition(HueImage));
    }

    private void HueImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateHueFromPoint(e.GetPosition(HueImage));
        }
    }

    private void ColorPickerImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        SaturationValueImage.ReleaseMouseCapture();
        HueImage.ReleaseMouseCapture();
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingColorPicker || RedSlider is null || GreenSlider is null || BlueSlider is null)
        {
            return;
        }

        var color = Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        SetPickerFromColor(color, updateTextBox: true);
    }

    private void UpdateSaturationValueFromPoint(Point point)
    {
        _pickerSaturation = Clamp(point.X / Math.Max(1, SaturationValueImage.ActualWidth), 0, 1);
        _pickerValue = 1 - Clamp(point.Y / Math.Max(1, SaturationValueImage.ActualHeight), 0, 1);
        SetActivePickerColor(HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue));
    }

    private void UpdateHueFromPoint(Point point)
    {
        _pickerHue = Clamp(point.Y / Math.Max(1, HueImage.ActualHeight), 0, 1) * 360;
        RenderSaturationValueImage();
        SetActivePickerColor(HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue));
    }

    private void SetPickerFromColor(Color color, bool updateTextBox)
    {
        RgbToHsv(color, out _pickerHue, out _pickerSaturation, out _pickerValue);
        RenderColorPickerImages();
        SetActivePickerColor(color, updateTextBox);
    }

    private void SetActivePickerColor(Color color, bool updateTextBox = true)
    {
        _updatingColorPicker = true;
        try
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RedValueText.Text = color.R.ToString(CultureInfo.InvariantCulture);
            GreenValueText.Text = color.G.ToString(CultureInfo.InvariantCulture);
            BlueValueText.Text = color.B.ToString(CultureInfo.InvariantCulture);
            PickerPreview.Background = new SolidColorBrush(color);
            UpdatePickerMarkers();

            if (updateTextBox && _activeColorTextBox is not null)
            {
                _activeColorTextBox.Text = ColorToHex(color);
                UpdateColorSwatches();
            }
        }
        finally
        {
            _updatingColorPicker = false;
        }
    }

    private void RenderColorPickerImages()
    {
        RenderSaturationValueImage();
        if (HueImage.Source is null)
        {
            HueImage.Source = CreateHueBitmap();
        }
    }

    private void RenderSaturationValueImage()
    {
        SaturationValueImage.Source = CreateSaturationValueBitmap(_pickerHue);
    }

    private void UpdatePickerMarkers()
    {
        Canvas.SetLeft(SaturationValueMarker, _pickerSaturation * SaturationValueImage.Width - SaturationValueMarker.Width / 2);
        Canvas.SetTop(SaturationValueMarker, (1 - _pickerValue) * SaturationValueImage.Height - SaturationValueMarker.Height / 2);
        Canvas.SetLeft(HueMarker, -2);
        Canvas.SetTop(HueMarker, _pickerHue / 360 * HueImage.Height - HueMarker.Height / 2);
    }

    private void UpdateColorSwatches()
    {
        SetSwatch(CustomThemeBackgroundSwatch, CustomThemeBackgroundBox);
        SetSwatch(CustomThemeSurfaceSwatch, CustomThemeSurfaceBox);
        SetSwatch(CustomThemePanelSwatch, CustomThemePanelBox);
        SetSwatch(CustomThemeTextSwatch, CustomThemeTextBox);
        SetSwatch(CustomThemeMutedSwatch, CustomThemeMutedBox);
        SetSwatch(CustomThemePrimarySwatch, CustomThemePrimaryBox);
        SetSwatch(CustomThemeSecondarySwatch, CustomThemeSecondaryBox);
    }

    private static void SetSwatch(Button button, TextBox textBox)
    {
        button.Background = new SolidColorBrush(ParseColor(textBox.Text));
    }

    private static WriteableBitmap CreateSaturationValueBitmap(double hue)
    {
        const int width = 276;
        const int height = 234;
        const int bytesPerPixel = 4;
        var pixels = new byte[width * height * bytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            var value = 1 - y / (double)(height - 1);
            for (var x = 0; x < width; x++)
            {
                var saturation = x / (double)(width - 1);
                var color = HsvToRgb(hue, saturation, value);
                var offset = (y * width + x) * bytesPerPixel;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * bytesPerPixel, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static WriteableBitmap CreateHueBitmap()
    {
        const int width = 34;
        const int height = 234;
        const int bytesPerPixel = 4;
        var pixels = new byte[width * height * bytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            var hue = y / (double)(height - 1) * 360;
            var color = HsvToRgb(hue, 1, 1);
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * bytesPerPixel;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * bytesPerPixel, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Clamp(saturation, 0, 1);
        value = Clamp(value, 0, 1);

        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var match = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + match) * 255),
            (byte)Math.Round((g + match) * 255),
            (byte)Math.Round((b + match) * 255));
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        hue = delta == 0
            ? 0
            : max == r
                ? 60 * (((g - b) / delta) % 6)
                : max == g
                    ? 60 * ((b - r) / delta + 2)
                    : 60 * ((r - g) / delta + 4);
        if (hue < 0)
        {
            hue += 360;
        }

        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }

    private void PlatformBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyPlatformControls();
    }

    private void ApplyPlatformControls()
    {
        if (PlatformBox.SelectedValue is not PlatformType platform)
        {
            return;
        }

        PcServiceBox.IsEnabled = platform != PlatformType.CONSOLE;
        ConsoleModelBox.IsEnabled = platform == PlatformType.CONSOLE;
    }

    private void CoverFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyCoverCrop();
    }

    private void ApplyCoverCrop()
    {
        if (CoverPreview is null || DetailsCoverPreview is null)
        {
            return;
        }

        ApplyCoverTransform(CoverPreview, CoverFrame, _imageScale, _imageOffsetX, _imageOffsetY);
        ApplyCoverTransform(DetailsCoverPreview, DetailsCoverFrame, _imageScale, _imageOffsetX, _imageOffsetY);
    }

    private void SetCoverCrop(double scale, double offsetX, double offsetY)
    {
        _imageScale = Clamp(scale, 1, 4);
        var (maxPanX, maxPanY) = CoverPreview is null || CoverFrame is null
            ? (1d, 1d)
            : GetCoverPanBounds(CoverPreview, CoverFrame, _imageScale);
        _imageOffsetX = maxPanX <= 0 ? 0 : Clamp(offsetX, -2, 2);
        _imageOffsetY = maxPanY <= 0 ? 0 : Clamp(offsetY, -2, 2);
        ApplyCoverCrop();
    }

    private void CoverFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideCoverEditAction(source))
        {
            return;
        }

        if (!_coverEditMode)
        {
            EnterCoverEditMode();
        }

        _coverDragActive = true;
        _coverDragStart = e.GetPosition(CoverFrame);
        _coverDragStartOffsetX = _imageOffsetX;
        _coverDragStartOffsetY = _imageOffsetY;
        CoverFrame.CaptureMouse();
        e.Handled = true;
    }

    private void CoverFrame_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_coverEditMode || !_coverDragActive || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(CoverFrame);
        var (maxPanX, maxPanY) = GetCoverPanBounds(CoverPreview, CoverFrame, _imageScale);
        var startPixelX = OffsetToPixels(_coverDragStartOffsetX, maxPanX);
        var startPixelY = OffsetToPixels(_coverDragStartOffsetY, maxPanY);
        var nextOffsetX = PixelsToOffset(startPixelX + position.X - _coverDragStart.X, maxPanX);
        var nextOffsetY = PixelsToOffset(startPixelY + position.Y - _coverDragStart.Y, maxPanY);
        SetCoverCrop(_imageScale, nextOffsetX, nextOffsetY);
        e.Handled = true;
    }

    private void CoverFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _coverDragActive = false;
        CoverFrame.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CoverFrame_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_coverEditMode)
        {
            return;
        }

        var step = e.Delta > 0 ? 0.1 : -0.1;
        SetCoverCrop(_imageScale + step, _imageOffsetX, _imageOffsetY);
        e.Handled = true;
    }

    private void ApplyCoverEditButton_Click(object sender, RoutedEventArgs e)
    {
        ExitCoverEditMode(keepChanges: true);
        e.Handled = true;
    }

    private void CancelCoverEditButton_Click(object sender, RoutedEventArgs e)
    {
        SetCoverCrop(_coverEditStartScale, _coverEditStartOffsetX, _coverEditStartOffsetY);
        ExitCoverEditMode(keepChanges: false);
        e.Handled = true;
    }

    private void EnterCoverEditMode()
    {
        _coverEditMode = true;
        _coverEditStartScale = _imageScale;
        _coverEditStartOffsetX = _imageOffsetX;
        _coverEditStartOffsetY = _imageOffsetY;
        CoverEditActions.Visibility = Visibility.Visible;
        CoverEditHint.Visibility = Visibility.Collapsed;
        CoverFrame.Cursor = Cursors.SizeAll;
    }

    private void ExitCoverEditMode(bool keepChanges)
    {
        _coverEditMode = false;
        _coverDragActive = false;
        CoverFrame.ReleaseMouseCapture();
        CoverEditActions.Visibility = Visibility.Collapsed;
        CoverEditHint.Visibility = Visibility.Visible;
        CoverFrame.Cursor = Cursors.Hand;
    }

    private static bool IsInsideCoverEditAction(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static void ApplyCoverTransform(Image image, FrameworkElement frame, double scale, double offsetX, double offsetY)
    {
        UpdateCoverImageLayout(image, frame);
        var (maxPanX, maxPanY) = GetCoverPanBounds(image, frame, scale);
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(OffsetToPixels(offsetX, maxPanX), OffsetToPixels(offsetY, maxPanY)));
        image.RenderTransform = transform;
    }

    private static void UpdateCoverImageLayout(Image image, FrameworkElement frame)
    {
        if (image.Source is not BitmapSource bitmap || frame.ActualWidth <= 0 || frame.ActualHeight <= 0)
        {
            image.Width = double.NaN;
            image.Height = double.NaN;
            return;
        }

        var frameWidth = frame.ActualWidth;
        var frameHeight = frame.ActualHeight;
        var imageAspect = bitmap.PixelWidth / (double)bitmap.PixelHeight;
        var frameAspect = frameWidth / frameHeight;
        if (imageAspect > frameAspect)
        {
            image.Width = frameWidth;
            image.Height = frameWidth / imageAspect;
        }
        else
        {
            image.Height = frameHeight;
            image.Width = frameHeight * imageAspect;
        }
    }

    private static (double MaxX, double MaxY) GetCoverPanBounds(Image image, FrameworkElement frame, double scale)
    {
        UpdateCoverImageLayout(image, frame);
        var frameWidth = frame.ActualWidth <= 0 ? 1 : frame.ActualWidth;
        var frameHeight = frame.ActualHeight <= 0 ? 1 : frame.ActualHeight;
        var imageWidth = double.IsNaN(image.Width) || image.Width <= 0 ? frameWidth : image.Width;
        var imageHeight = double.IsNaN(image.Height) || image.Height <= 0 ? frameHeight : image.Height;
        return (
            Math.Max(0, (imageWidth * scale - frameWidth) / 2),
            Math.Max(0, (imageHeight * scale - frameHeight) / 2));
    }

    private static double OffsetToPixels(double offset, double maxPan) =>
        maxPan <= 0 ? 0 : Clamp(offset, -2, 2) * maxPan / 2;

    private static double PixelsToOffset(double pixels, double maxPan) =>
        maxPan <= 0 ? 0 : Clamp(pixels, -maxPan, maxPan) / maxPan * 2;

    private static ImageSource? CreateCoverPreview(string? path, double scale, double offsetX, double offsetY)
    {
        var bitmap = LoadBitmap(path);
        if (bitmap is null)
        {
            return null;
        }

        const int targetWidth = 300;
        const int targetHeight = 400;
        var normalizedScale = Clamp(scale, 1, 4);
        var imageAspect = bitmap.PixelWidth / (double)bitmap.PixelHeight;
        var frameAspect = targetWidth / (double)targetHeight;
        double baseWidth;
        double baseHeight;
        if (imageAspect > frameAspect)
        {
            baseWidth = targetWidth;
            baseHeight = targetWidth / imageAspect;
        }
        else
        {
            baseHeight = targetHeight;
            baseWidth = targetHeight * imageAspect;
        }

        var renderedWidth = baseWidth * normalizedScale;
        var renderedHeight = baseHeight * normalizedScale;
        var maxPanX = Math.Max(0, (renderedWidth - targetWidth) / 2);
        var maxPanY = Math.Max(0, (renderedHeight - targetHeight) / 2);
        var translateX = OffsetToPixels(offsetX, maxPanX);
        var translateY = OffsetToPixels(offsetY, maxPanY);
        var rect = new Rect(
            (targetWidth - renderedWidth) / 2 + translateX,
            (targetHeight - renderedHeight) / 2 + translateY,
            renderedWidth,
            renderedHeight);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(bitmap, rect);
        }

        var preview = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        preview.Render(visual);
        preview.Freeze();
        return preview;
    }

    private void SetLanguage(string language)
    {
        _language = string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase)
            ? EnglishLanguage
            : RussianLanguage;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_language == RussianLanguage ? "ru-RU" : "en-US");
        RefreshOptionControls();
        RefreshThemeControls();
        ApplyLanguage();
    }

    private void RefreshOptionControls()
    {
        var status = StatusFilterBox.SelectedValue as GameStatus?;
        var sort = SortBox.SelectedValue is SortMode sortMode ? sortMode : SortMode.TitleAscending;
        var platform = PlatformBox.SelectedValue is PlatformType platformType ? platformType : PlatformType.PC;
        var language = string.IsNullOrWhiteSpace(_language) ? RussianLanguage : _language;

        StatusFilterBox.ItemsSource = new UiOption<GameStatus?>[]
        {
            new(null, T("FilterAll")),
            new(GameStatus.COMPLETED, StatusName(GameStatus.COMPLETED)),
            new(GameStatus.IN_PROGRESS, StatusName(GameStatus.IN_PROGRESS)),
            new(GameStatus.POSTPONED, StatusName(GameStatus.POSTPONED)),
            new(GameStatus.DROPPED, StatusName(GameStatus.DROPPED)),
            new(GameStatus.PLANNED, StatusName(GameStatus.PLANNED)),
            new(GameStatus.NEVER_PLAY_AGAIN, StatusName(GameStatus.NEVER_PLAY_AGAIN))
        };
        StatusFilterBox.SelectedValue = status;
        if (StatusFilterBox.SelectedIndex < 0)
        {
            StatusFilterBox.SelectedIndex = 0;
        }

        SortBox.ItemsSource = new UiOption<SortMode>[]
        {
            new(SortMode.TitleAscending, T("SortTitleAscending")),
            new(SortMode.TitleDescending, T("SortTitleDescending")),
            new(SortMode.Status, T("SortStatus")),
            new(SortMode.Year, T("SortYear")),
            new(SortMode.Created, T("SortCreated")),
            new(SortMode.Updated, T("SortUpdated"))
        };
        SortBox.SelectedValue = sort;

        PlatformBox.ItemsSource = new UiOption<PlatformType>[]
        {
            new(PlatformType.PC, PlatformName(PlatformType.PC)),
            new(PlatformType.CONSOLE, PlatformName(PlatformType.CONSOLE)),
            new(PlatformType.MOBILE, PlatformName(PlatformType.MOBILE))
        };
        PlatformBox.SelectedValue = platform;

        LanguageBox.ItemsSource = new UiOption<string>[]
        {
            new(RussianLanguage, "Русский"),
            new(EnglishLanguage, "English")
        };
        LanguageBox.SelectedValue = language;
    }

    private void ApplyLanguage()
    {
        Title = T("WindowTitle");
        TranslateStaticText(this);
        foreach (var column in DriveBackupsGrid.Columns)
        {
            if (column.Header is string text)
            {
                column.Header = TranslateLiteral(text);
            }
        }
    }

    private void TranslateStaticText(DependencyObject root)
    {
        switch (root)
        {
            case Button button when button.Content is string text:
                button.Content = TranslateLiteral(text);
                break;
            case CheckBox checkBox when checkBox.Content is string text:
                checkBox.Content = TranslateLiteral(text);
                break;
            case RadioButton radioButton when radioButton.Content is string text:
                radioButton.Content = TranslateLiteral(text);
                break;
            case GroupBox groupBox when groupBox.Header is string text:
                groupBox.Header = TranslateLiteral(text);
                break;
            case TabItem tabItem when tabItem.Header is string text:
                tabItem.Header = TranslateLiteral(text);
                break;
            case TextBlock textBlock when BindingOperations.GetBindingExpression(textBlock, TextBlock.TextProperty) is null:
                textBlock.Text = TranslateLiteral(textBlock.Text);
                break;
            case TextBox textBox when textBox.ToolTip is string text:
                textBox.ToolTip = TranslateLiteral(text);
                break;
        }

        if (root is FrameworkElement { ToolTip: string toolTipText } element)
        {
            element.ToolTip = TranslateLiteral(toolTipText);
        }

        if (root is Visual or Visual3D)
        {
            var childrenCount = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < childrenCount; index++)
            {
                TranslateStaticText(VisualTreeHelper.GetChild(root, index));
            }
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            TranslateStaticText(child);
        }
    }

    private string TranslateLiteral(string text)
    {
        if (StaticTextTranslations.TryGetValue(text, out var key))
        {
            return T(key);
        }

        return text;
    }

    private string FormatStatuses(IEnumerable<GameStatus> statuses)
    {
        var values = statuses.Distinct().ToList();
        if (values.Count == 0)
        {
            values.Add(GameStatus.PLANNED);
        }

        return string.Join(", ", values.Select(StatusName));
    }

    private string FormatImportSummary(LibraryImportResult result)
    {
        var key = result.Cancelled ? "ImportCancelledSummary" : "ImportCompleteSummary";
        return string.Format(CultureInfo.CurrentCulture, T(key), result.Added, result.Replaced, result.Skipped);
    }

    private async Task<ImageCleanupResult> CleanupUnreferencedImagesAsync()
    {
        await using var db = CreateDb();
        return await _imageService.DeleteUnreferencedImagesAsync(db);
    }

    private string FormatImageCleanupSummary(ImageCleanupResult result)
    {
        var key = result.Failed == 0 ? "ImageCleanupSummary" : "ImageCleanupPartialSummary";
        return string.Format(CultureInfo.CurrentCulture, T(key), result.Deleted, result.Scanned, result.Failed);
    }

    private string StatusName(GameStatus status) => status switch
    {
        GameStatus.COMPLETED => T("StatusCompleted"),
        GameStatus.IN_PROGRESS => T("StatusInProgress"),
        GameStatus.POSTPONED => T("StatusPostponed"),
        GameStatus.DROPPED => T("StatusDropped"),
        GameStatus.PLANNED => T("StatusPlanned"),
        GameStatus.NEVER_PLAY_AGAIN => T("StatusNeverPlayAgain"),
        _ => status.ToString()
    };

    private string PlatformName(PlatformType platform) => platform switch
    {
        PlatformType.PC => T("PlatformPc"),
        PlatformType.CONSOLE => T("PlatformConsole"),
        PlatformType.MOBILE => T("PlatformMobile"),
        _ => platform.ToString()
    };

    private string T(string key)
    {
        var table = _language == EnglishLanguage ? EnglishText : RussianText;
        return table.TryGetValue(key, out var value) ? value : key;
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
        var bitmap = LoadBitmap(path);
        CoverPreview.Source = bitmap;
        DetailsCoverPreview.Source = bitmap;
        SetCoverCrop(_imageScale, _imageOffsetX, _imageOffsetY);
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

    private string? PickImageFile() => PickOpenFile(T("ImageFileFilter"));

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

    private IProgress<DownloadProgress> CreateDownloadProgress(string label)
    {
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        SetStatus(label);
        return new Progress<DownloadProgress>(progress => SetDownloadProgress(label, progress));
    }

    private void SetDownloadProgress(string label, DownloadProgress progress)
    {
        if (progress.Percent is double percent)
        {
            DownloadProgressBar.Visibility = Visibility.Visible;
            DownloadProgressBar.Value = Math.Clamp(percent, 0, 100);
            SetStatus(string.Format(
                CultureInfo.CurrentCulture,
                T("DownloadProgressKnown"),
                label,
                percent,
                FormatByteSize(progress.BytesDownloaded),
                FormatByteSize(progress.TotalBytes!.Value)));
            return;
        }

        DownloadProgressBar.Visibility = Visibility.Collapsed;
        SetStatus(string.Format(
            CultureInfo.CurrentCulture,
            T("DownloadProgressUnknown"),
            label,
            FormatByteSize(progress.BytesDownloaded)));
    }

    private void ClearDownloadProgress()
    {
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Collapsed;
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

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024d;
        if (kb < 1024)
        {
            return $"{kb:0.0} KB";
        }

        var mb = kb / 1024d;
        return mb < 1024
            ? $"{mb:0.0} MB"
            : $"{mb / 1024d:0.0} GB";
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

    private sealed record UiOption<T>(T Value, string Text);
    private sealed record AutoImportedImage(string LocalPath, string ArchiveName, string SourceUrl);
    private sealed record ThemePalette(
        string Id,
        string Name,
        string Background,
        string Surface,
        string Panel,
        string Text,
        string Muted,
        string Primary,
        string Secondary,
        string Danger = "#FF4D6D",
        string ButtonText = "#061019")
    {
        public static ThemePalette DefaultCustom { get; } =
            new("custom", "Custom", "#10131F", "#171A29", "#0D101A", "#F7F7FF", "#A9B1C7", "#7CF7C7", "#F7D56E");
    }

    private enum SortMode
    {
        TitleAscending,
        TitleDescending,
        Status,
        Year,
        Created,
        Updated
    }

    private enum AppScreen
    {
        Start,
        Menu,
        Database,
        AddMode,
        ManualEdit,
        AutoAdd,
        Details,
        ImportExport,
        Settings,
        ThemeEditor,
        References
    }

    private const int MaxAutoGalleryImages = 20;
    private const double MinCardScale = 0.75;
    private const double MaxCardScale = 1.5;
    private const double DefaultCardScale = 1.0;
    private const double MinUiFontScale = 0.8;
    private const double MaxUiFontScale = 3;
    private const double DefaultUiFontScale = 1.0;
    private const double DefaultCoverAspectRatio = 3d / 4d;
    private const string CustomThemeKeyPrefix = "custom:";
    private const string RussianLanguage = "ru";
    private const string EnglishLanguage = "en";
    private static readonly JsonSerializerOptions ThemeJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string> RussianText = new Dictionary<string, string>
    {
        ["WindowTitle"] = "Reliquary",
        ["Ready"] = "Готово",
        ["StartupFailed"] = "Ошибка запуска",
        ["FilterAll"] = "Все",
        ["SortTitleAscending"] = "Название А-Я",
        ["SortTitleDescending"] = "Название Я-А",
        ["SortStatus"] = "Статус",
        ["SortYear"] = "Год",
        ["SortCreated"] = "Дата создания",
        ["SortUpdated"] = "Дата изменения",
        ["UpdatedAt"] = "Обновлено {0}",
        ["UnableFilterGames"] = "Не удалось отфильтровать игры.",
        ["UnableLoadGame"] = "Не удалось загрузить игру.",
        ["UnableSaveGame"] = "Не удалось сохранить игру.",
        ["GameSaved"] = "Игра сохранена.",
        ["UnableImportStore"] = "Не удалось импортировать игру из магазина.",
        ["ChooseStoreFirst"] = "Сначала выберите магазин.",
        ["StoreReferenceRequired"] = "Укажите ссылку магазина, slug или AppID.",
        ["ImportingFrom"] = "Импорт из {0}...",
        ["ImportedFrom"] = "{0} импортирована из {1}.",
        ["GameExistsImagesUpdated"] = "Игра уже существует; импортированные изображения обновлены для {0}.",
        ["TitleRequired"] = "Название обязательно.",
        ["YearMustBeNumber"] = "Год должен быть числом.",
        ["DeleteSelectedGameQuestion"] = "Удалить выбранную игру?",
        ["ConfirmDelete"] = "Подтверждение удаления",
        ["Confirm"] = "Подтверждение",
        ["UnableDeleteGame"] = "Не удалось удалить игру.",
        ["GameDeleted"] = "Игра удалена.",
        ["UnableAddCover"] = "Не удалось добавить обложку.",
        ["UnableDownloadCover"] = "Не удалось скачать обложку.",
        ["UnableReloadCover"] = "Не удалось перезагрузить обложку.",
        ["GameNotFound"] = "Игра не найдена.",
        ["CoverHasNoSourceUrl"] = "У этой обложки нет imageSourceUrl.",
        ["UnableAddGalleryImage"] = "Не удалось добавить изображение в галерею.",
        ["UnableDownloadGalleryImage"] = "Не удалось скачать изображение для галереи.",
        ["GalleryLimit"] = "Лимит галереи: 20 изображений.",
        ["UnableSetGalleryCover"] = "Не удалось сделать изображение галереи обложкой.",
        ["UnableRemoveGalleryImage"] = "Не удалось удалить изображение из галереи.",
        ["ChooseConsoleFamilyFirst"] = "Сначала выберите семейство консолей.",
        ["Validation"] = "Проверка",
        ["UnableAddReference"] = "Не удалось добавить справочник.",
        ["NameRequired"] = "Название обязательно.",
        ["ReferenceAdded"] = "Справочник добавлен.",
        ["ChooseBackupFolderTitle"] = "Выберите папку резервных копий/экспорта GameVault",
        ["UnableExportJson"] = "Не удалось экспортировать JSON.",
        ["UnableExportZip"] = "Не удалось экспортировать ZIP.",
        ["ExportedJson"] = "JSON экспортирован: {0}",
        ["ExportedZip"] = "ZIP экспортирован: {0}",
        ["JsonFileFilter"] = "GameVault JSON (*.json)|*.json|Все файлы (*.*)|*.*",
        ["ZipFileFilter"] = "GameVault ZIP (*.zip)|*.zip|Все файлы (*.*)|*.*",
        ["ImageFileFilter"] = "Изображения|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|Все файлы (*.*)|*.*",
        ["UnableImportLibrary"] = "Не удалось импортировать библиотеку.",
        ["ImportCancelledSummary"] = "Импорт отменён. Добавлено: {0}, заменено: {1}, пропущено: {2}.",
        ["ImportCompleteSummary"] = "Импорт завершён. Добавлено: {0}, заменено: {1}, пропущено: {2}.",
        ["ClearImageCacheQuestion"] = "Удалить файлы изображений, не связанные ни с одной игрой?",
        ["UnableClearImageCache"] = "Не удалось очистить кэш изображений.",
        ["ImageCacheCleared"] = "Кэш изображений очищен.",
        ["ImageCleanupSummary"] = "Очистка изображений: удалено {0} из {1} файлов.",
        ["ImageCleanupPartialSummary"] = "Очистка изображений: удалено {0} из {1} файлов, не удалось удалить {2}.",
        ["ClearLibraryQuestion"] = "Удалить все локальные игры? Справочники и настройки будут сохранены.",
        ["UnableClearLibrary"] = "Не удалось очистить библиотеку.",
        ["LocalLibraryCleared"] = "Локальная библиотека очищена.",
        ["UnableConnectDrive"] = "Не удалось подключить Google Диск.",
        ["OpeningBrowserSignIn"] = "Открывается вход через браузер...",
        ["DriveConnected"] = "Google Диск подключён.",
        ["DriveDisconnected"] = "Google Диск отключён.",
        ["UnableDisconnectDrive"] = "Не удалось отключить Google Диск.",
        ["UnableUploadBackup"] = "Не удалось загрузить резервную копию.",
        ["ZipUploadedDrive"] = "ZIP-копия загружена в корневую папку /GameVault.",
        ["UnableAutoExportDrive"] = "Не удалось выполнить автоэкспорт в Google Диск.",
        ["TempZipUploadedDrive"] = "Временный ZIP-архив экспортирован и загружен в корневую папку /GameVault.",
        ["UnableRefreshBackups"] = "Не удалось обновить список резервных копий.",
        ["LoadedDriveBackups"] = "Загружено резервных копий Google Диска из корневой папки /GameVault: {0}.",
        ["UnableRestoreLatestBackup"] = "Не удалось восстановить последнюю резервную копию.",
        ["NoDriveBackups"] = "В корневой папке /GameVault резервные копии не найдены.",
        ["UnableRestoreBackup"] = "Не удалось восстановить резервную копию.",
        ["MoveBackupToTrashQuestion"] = "Переместить «{0}» в корзину Google Диска?",
        ["UnableMoveBackupTrash"] = "Не удалось переместить резервную копию в корзину.",
        ["BackupMovedTrash"] = "Резервная копия перемещена в корзину Google Диска.",
        ["DownloadCoverProgress"] = "Скачивание обложки",
        ["DownloadGalleryProgress"] = "Скачивание изображения",
        ["DownloadBackupProgress"] = "Скачивание резервной копии",
        ["DownloadProgressKnown"] = "{0}: {1:0}% ({2} из {3})",
        ["DownloadProgressUnknown"] = "{0}: скачано {1}",
        ["ImportingBackup"] = "Импорт резервной копии...",
        ["DriveConnectedAutomatically"] = "Google Диск подключён автоматически.",
        ["DriveTokenInvalid"] = "Токен Google Диска отсутствует, истёк или больше недействителен. Нажмите «Подключить аккаунт», чтобы войти снова.",
        ["DriveNotAuthenticatedStartup"] = "Google Диск ещё не авторизован. Один раз нажмите «Подключить аккаунт», после этого будущие запуски будут подключаться автоматически.",
        ["DriveNotAuthenticatedAction"] = "Google Диск ещё не авторизован. Сначала нажмите «Подключить аккаунт», чтобы использовать действия с Диском.",
        ["LanguageChanged"] = "Язык интерфейса изменён.",
        ["StatusCompleted"] = "Пройдено",
        ["StatusInProgress"] = "В процессе",
        ["StatusPostponed"] = "Отложено",
        ["StatusDropped"] = "Брошено",
        ["StatusPlanned"] = "Запланировано",
        ["StatusNeverPlayAgain"] = "Больше не играть",
        ["PlatformPc"] = "ПК",
        ["PlatformConsole"] = "Консоль",
        ["PlatformMobile"] = "Мобильная",
        ["Search by title"] = "Поиск по названию",
        ["List"] = "Список",
        ["Tiles"] = "Плитка",
        ["New"] = "Новая",
        ["Game card"] = "Карточка игры",
        ["Add cover"] = "Добавить обложку",
        ["Reload URL"] = "Обновить по URL",
        ["Image URL"] = "URL изображения",
        ["Download cover URL"] = "Скачать обложку по URL",
        ["Store import"] = "Импорт из магазина",
        ["Steam AppID, store URL, or GOG/Epic slug"] = "Steam AppID, URL магазина или slug GOG/Epic",
        ["Import"] = "Импорт",
        ["Title"] = "Название",
        ["Year"] = "Год",
        ["Stable ID"] = "Стабильный ID",
        ["Platform"] = "Платформа",
        ["PC service"] = "ПК-сервис",
        ["Console model"] = "Модель консоли",
        ["Statuses"] = "Статусы",
        ["COMPLETED"] = "ПРОЙДЕНО",
        ["IN_PROGRESS"] = "В ПРОЦЕССЕ",
        ["POSTPONED"] = "ОТЛОЖЕНО",
        ["DROPPED"] = "БРОШЕНО",
        ["PLANNED"] = "ЗАПЛАНИРОВАНО",
        ["NEVER_PLAY_AGAIN"] = "БОЛЬШЕ НЕ ИГРАТЬ",
        ["Cover crop"] = "Кадрирование обложки",
        ["Scale"] = "Масштаб",
        ["Offset X"] = "Сдвиг X",
        ["Offset Y"] = "Сдвиг Y",
        ["Cover edit hint"] = "Клик по обложке: перетаскивание - сдвиг, колесо - масштаб.",
        ["Source page URL"] = "URL страницы источника",
        ["Notes"] = "Заметки",
        ["Save"] = "Сохранить",
        ["Delete"] = "Удалить",
        ["Images"] = "Изображения",
        ["Add local image"] = "Добавить локальное изображение",
        ["Gallery image URL"] = "URL изображения галереи",
        ["Download URL"] = "Скачать по URL",
        ["Set as cover"] = "Сделать обложкой",
        ["Remove"] = "Удалить",
        ["References"] = "Справочники",
        ["PC services"] = "ПК-сервисы",
        ["Add service"] = "Добавить сервис",
        ["Console families"] = "Семейства консолей",
        ["Add family"] = "Добавить семейство",
        ["Console models"] = "Модели консолей",
        ["Add model"] = "Добавить модель",
        ["Import / Export"] = "Импорт / экспорт",
        ["Local backup/export folder"] = "Локальная папка резервных копий/экспорта",
        ["Choose folder"] = "Выбрать папку",
        ["Export"] = "Экспорт",
        ["Export JSON"] = "Экспорт JSON",
        ["Export ZIP with images"] = "Экспорт ZIP с изображениями",
        ["Import JSON"] = "Импорт JSON",
        ["Import ZIP"] = "Импорт ZIP",
        ["Maintenance"] = "Обслуживание",
        ["Clear image cache"] = "Очистить лишние изображения",
        ["Clear library"] = "Очистить библиотеку",
        ["Google Drive"] = "Google Диск",
        ["Connect account"] = "Подключить аккаунт",
        ["Disconnect"] = "Отключить",
        ["Upload ZIP backup"] = "Загрузить ZIP-копию",
        ["Auto export to Drive"] = "Автоэкспорт на Диск",
        ["Refresh list"] = "Обновить список",
        ["Restore selected"] = "Восстановить выбранную",
        ["Restore latest"] = "Восстановить последнюю",
        ["Move to trash"] = "В корзину",
        ["Name"] = "Имя",
        ["Created"] = "Создано",
        ["Size"] = "Размер",
        ["Only the root /GameVault folder is used."] = "Используется только корневая папка /GameVault.",
        ["Settings"] = "Настройки",
        ["Theme"] = "Тема",
        ["Light"] = "Светлая",
        ["Dark"] = "Тёмная",
        ["Language"] = "Язык",
        ["Font size"] = "Размер шрифта",
        ["Reset"] = "Сбросить",
        ["Back"] = "Назад",
        ["Home"] = "Домой",
        ["Start"] = "Старт",
        ["Desktop collection control"] = "Управление игровой коллекцией на ПК",
        ["Use the same visual modes as the mobile GameVault shell."] = "Используйте те же визуальные режимы, что и в мобильной оболочке GameVault.",
        ["Menu"] = "Меню",
        ["New record"] = "Новая запись",
        ["Database"] = "База данных",
        ["Exit"] = "Выход",
        ["Sort"] = "Сортировка",
        ["Manual add"] = "Вручную",
        ["Add by link"] = "По ссылке",
        ["Main"] = "Основное",
        ["Platform / Distribution"] = "Платформа / дистрибуция",
        ["Details"] = "Детали",
        ["Edit"] = "Редактировать",
        ["Game"] = "Игра",
        ["Feedback"] = "Отклик",
        ["Button sound"] = "Звук кнопок",
        ["Visual click feedback"] = "Визуальный отклик на нажатие",
        ["Theme editor"] = "Редактор тем",
        ["Built-in themes"] = "Встроенные темы",
        ["Apply theme"] = "Применить тему",
        ["New custom theme"] = "Новая кастомная тема",
        ["Choose color"] = "Выбрать цвет",
        ["Custom theme"] = "Кастомная тема",
        ["Background"] = "Фон",
        ["Surface"] = "Поверхность",
        ["Panel"] = "Панель",
        ["Text"] = "Текст",
        ["Muted text"] = "Приглушённый текст",
        ["Primary"] = "Основной",
        ["Secondary"] = "Вторичный",
        ["Save custom theme"] = "Сохранить кастомную тему",
        ["Delete custom theme"] = "Удалить кастомную тему",
        ["Desktop library"] = "Библиотека ПК",
        ["Choose action"] = "Выберите действие",
        ["Search, filter, sort, and open game cards"] = "Поиск, фильтры, сортировка и карточки игр",
        ["Choose how to add a game"] = "Выберите способ добавления игры",
        ["Create or edit game metadata"] = "Создание или редактирование данных игры",
        ["Import game metadata from store pages"] = "Импорт данных игры со страниц магазинов",
        ["Cover, gallery, statuses, and notes"] = "Обложка, галерея, статусы и заметки",
        ["JSON, ZIP, and Google Drive backups"] = "JSON, ZIP и резервные копии Google Диска",
        ["Themes, language, feedback, and maintenance"] = "Темы, язык, отклик и обслуживание",
        ["Built-in and custom color modes"] = "Встроенные и кастомные цветовые режимы",
        ["Services, console families, and models"] = "Сервисы, семейства консолей и модели",
        ["UnableSaveTheme"] = "Не удалось сохранить тему.",
        ["UnableDeleteTheme"] = "Не удалось удалить тему.",
        ["CustomThemeSaved"] = "Кастомная тема сохранена.",
        ["CustomThemeDeleted"] = "Кастомная тема сброшена."
    };

    private static readonly IReadOnlyDictionary<string, string> EnglishText = new Dictionary<string, string>
    {
        ["WindowTitle"] = "Reliquary",
        ["Ready"] = "Ready",
        ["StartupFailed"] = "Startup failed",
        ["FilterAll"] = "All",
        ["SortTitleAscending"] = "Title A-Z",
        ["SortTitleDescending"] = "Title Z-A",
        ["SortStatus"] = "Status",
        ["SortYear"] = "Year",
        ["SortCreated"] = "Created",
        ["SortUpdated"] = "Updated",
        ["UpdatedAt"] = "Updated {0}",
        ["UnableFilterGames"] = "Unable to filter games.",
        ["UnableLoadGame"] = "Unable to load game.",
        ["UnableSaveGame"] = "Unable to save game.",
        ["GameSaved"] = "Game saved.",
        ["UnableImportStore"] = "Unable to import game from store.",
        ["ChooseStoreFirst"] = "Choose a store first.",
        ["StoreReferenceRequired"] = "Store link, slug, or AppID is required.",
        ["ImportingFrom"] = "Importing from {0}...",
        ["ImportedFrom"] = "Imported {0} from {1}.",
        ["GameExistsImagesUpdated"] = "Game already exists; updated imported images for {0}.",
        ["TitleRequired"] = "Title is required.",
        ["YearMustBeNumber"] = "Year must be a number.",
        ["DeleteSelectedGameQuestion"] = "Delete selected game?",
        ["ConfirmDelete"] = "Confirm delete",
        ["Confirm"] = "Confirm",
        ["UnableDeleteGame"] = "Unable to delete game.",
        ["GameDeleted"] = "Game deleted.",
        ["UnableAddCover"] = "Unable to add cover.",
        ["UnableDownloadCover"] = "Unable to download cover.",
        ["UnableReloadCover"] = "Unable to reload cover.",
        ["GameNotFound"] = "Game was not found.",
        ["CoverHasNoSourceUrl"] = "This cover does not have imageSourceUrl.",
        ["UnableAddGalleryImage"] = "Unable to add gallery image.",
        ["UnableDownloadGalleryImage"] = "Unable to download gallery image.",
        ["GalleryLimit"] = "Gallery limit is 20 images.",
        ["UnableSetGalleryCover"] = "Unable to set gallery image as cover.",
        ["UnableRemoveGalleryImage"] = "Unable to remove gallery image.",
        ["ChooseConsoleFamilyFirst"] = "Choose console family first.",
        ["Validation"] = "Validation",
        ["UnableAddReference"] = "Unable to add reference.",
        ["NameRequired"] = "Name is required.",
        ["ReferenceAdded"] = "Reference added.",
        ["ChooseBackupFolderTitle"] = "Choose GameVault backup/export folder",
        ["UnableExportJson"] = "Unable to export JSON.",
        ["UnableExportZip"] = "Unable to export ZIP.",
        ["ExportedJson"] = "Exported JSON: {0}",
        ["ExportedZip"] = "Exported ZIP: {0}",
        ["JsonFileFilter"] = "GameVault JSON (*.json)|*.json|All files (*.*)|*.*",
        ["ZipFileFilter"] = "GameVault ZIP (*.zip)|*.zip|All files (*.*)|*.*",
        ["ImageFileFilter"] = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All files (*.*)|*.*",
        ["UnableImportLibrary"] = "Unable to import library.",
        ["ImportCancelledSummary"] = "Import cancelled. Added: {0}, replaced: {1}, skipped: {2}.",
        ["ImportCompleteSummary"] = "Import complete. Added: {0}, replaced: {1}, skipped: {2}.",
        ["ClearImageCacheQuestion"] = "Delete image files that are not linked to any game?",
        ["UnableClearImageCache"] = "Unable to clear image cache.",
        ["ImageCacheCleared"] = "Image cache cleared.",
        ["ImageCleanupSummary"] = "Image cleanup: deleted {0} of {1} files.",
        ["ImageCleanupPartialSummary"] = "Image cleanup: deleted {0} of {1} files, failed to delete {2}.",
        ["ClearLibraryQuestion"] = "Delete all local games? References and settings will be kept.",
        ["UnableClearLibrary"] = "Unable to clear library.",
        ["LocalLibraryCleared"] = "Local library cleared.",
        ["UnableConnectDrive"] = "Unable to connect Google Drive.",
        ["OpeningBrowserSignIn"] = "Opening browser sign-in...",
        ["DriveConnected"] = "Google Drive connected.",
        ["DriveDisconnected"] = "Google Drive disconnected.",
        ["UnableDisconnectDrive"] = "Unable to disconnect Google Drive.",
        ["UnableUploadBackup"] = "Unable to upload backup.",
        ["ZipUploadedDrive"] = "ZIP backup uploaded to root /GameVault.",
        ["UnableAutoExportDrive"] = "Unable to auto export to Google Drive.",
        ["TempZipUploadedDrive"] = "Temporary ZIP archive exported and uploaded to root /GameVault.",
        ["UnableRefreshBackups"] = "Unable to refresh backups.",
        ["LoadedDriveBackups"] = "Loaded {0} Google Drive backup(s) from root /GameVault.",
        ["UnableRestoreLatestBackup"] = "Unable to restore latest backup.",
        ["NoDriveBackups"] = "No backups found in root /GameVault.",
        ["UnableRestoreBackup"] = "Unable to restore backup.",
        ["MoveBackupToTrashQuestion"] = "Move '{0}' to Google Drive trash?",
        ["UnableMoveBackupTrash"] = "Unable to move backup to trash.",
        ["BackupMovedTrash"] = "Backup moved to Google Drive trash.",
        ["DownloadCoverProgress"] = "Downloading cover",
        ["DownloadGalleryProgress"] = "Downloading image",
        ["DownloadBackupProgress"] = "Downloading backup",
        ["DownloadProgressKnown"] = "{0}: {1:0}% ({2} of {3})",
        ["DownloadProgressUnknown"] = "{0}: downloaded {1}",
        ["ImportingBackup"] = "Importing backup...",
        ["DriveConnectedAutomatically"] = "Google Drive connected automatically.",
        ["DriveTokenInvalid"] = "Google Drive token is missing, expired, or no longer valid. Use Connect account to sign in again.",
        ["DriveNotAuthenticatedStartup"] = "Google Drive is not authenticated yet. Use Connect account once, then future starts will connect automatically.",
        ["DriveNotAuthenticatedAction"] = "Google Drive is not authenticated yet. Use Connect account once before using Drive actions.",
        ["LanguageChanged"] = "Interface language changed.",
        ["UnableSaveTheme"] = "Unable to save theme.",
        ["UnableDeleteTheme"] = "Unable to delete theme.",
        ["CustomThemeSaved"] = "Custom theme saved.",
        ["CustomThemeDeleted"] = "Custom theme reset.",
        ["StatusCompleted"] = "Completed",
        ["StatusInProgress"] = "In progress",
        ["StatusPostponed"] = "Postponed",
        ["StatusDropped"] = "Dropped",
        ["StatusPlanned"] = "Planned",
        ["StatusNeverPlayAgain"] = "Never play again",
        ["PlatformPc"] = "PC",
        ["PlatformConsole"] = "Console",
        ["PlatformMobile"] = "Mobile",
        ["Cover edit hint"] = "Click cover to adjust. Drag to pan, wheel to zoom."
    };

    private static readonly IReadOnlyDictionary<string, string> StaticTextTranslations = BuildStaticTextTranslations();

    private static IReadOnlyDictionary<string, string> BuildStaticTextTranslations()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in RussianText)
        {
            values[value] = key;
        }

        foreach (var (key, value) in RussianText)
        {
            if (EnglishText.TryGetValue(key, out var english))
            {
                values[english] = key;
            }

            values[key] = key;
        }

        return values;
    }
}
