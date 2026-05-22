using System.Windows;
using GameTrackerPC.Services;

namespace GameTrackerPC;

public partial class ImportConflictDialog : Window
{
    public ImportConflictDecision Decision { get; private set; } = ImportConflictDecision.Cancel;

    public ImportConflictDialog(ImportConflictInfo conflict, string language)
    {
        InitializeComponent();
        var isRussian = !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        Title = isRussian ? "Конфликт импорта" : "Import conflict";
        DialogTitle.Text = Title;
        ExistingLabel.Text = isRussian ? "Существующая" : "Existing";
        IncomingLabel.Text = isRussian ? "Импортируемая" : "Incoming";
        ReplaceButton.Content = isRussian ? "Заменить" : "Replace";
        ReplaceAllButton.Content = isRussian ? "Заменить все" : "Replace all";
        SkipButton.Content = isRussian ? "Пропустить" : "Skip";
        CancelButton.Content = isRussian ? "Отмена" : "Cancel";

        var reason = conflict.Reason == "ID"
            ? "ID"
            : isRussian ? "названию, году и платформе" : "title + year + platform";
        ConflictText.Text = isRussian
            ? $"Игра уже существует по совпадению: {reason}. Выберите, как продолжить."
            : $"A game already exists by {reason}. Choose how to continue.";
        ExistingText.Text = $"{conflict.Existing.Title}\n{conflict.Existing.Year?.ToString() ?? "-"} / {FormatPlatform(conflict.Existing.PlatformType, isRussian)}\nID: {conflict.Existing.Id}";
        IncomingText.Text = $"{conflict.Incoming.Title}\n{conflict.Incoming.Year?.ToString() ?? "-"} / {FormatPlatform(conflict.Incoming.PlatformType, isRussian)}\nID: {conflict.Incoming.Id}";
    }

    private static string FormatPlatform(Models.PlatformType platform, bool isRussian) => platform switch
    {
        Models.PlatformType.PC => isRussian ? "ПК" : "PC",
        Models.PlatformType.CONSOLE => isRussian ? "Консоль" : "Console",
        Models.PlatformType.MOBILE => isRussian ? "Мобильная" : "Mobile",
        _ => platform.ToString()
    };

    private void Replace_Click(object sender, RoutedEventArgs e) => Complete(ImportConflictDecision.Replace);
    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => Complete(ImportConflictDecision.ReplaceAll);
    private void Skip_Click(object sender, RoutedEventArgs e) => Complete(ImportConflictDecision.Skip);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(ImportConflictDecision.Cancel);

    private void Complete(ImportConflictDecision decision)
    {
        Decision = decision;
        DialogResult = true;
        Close();
    }
}
