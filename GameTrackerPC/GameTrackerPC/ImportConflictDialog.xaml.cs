using System.Windows;
using GameTrackerPC.Services;

namespace GameTrackerPC;

public partial class ImportConflictDialog : Window
{
    public ImportConflictDecision Decision { get; private set; } = ImportConflictDecision.Cancel;

    public ImportConflictDialog(ImportConflictInfo conflict)
    {
        InitializeComponent();
        ConflictText.Text = $"A game already exists by {conflict.Reason}. Choose how to continue.";
        ExistingText.Text = $"{conflict.Existing.Title}\n{conflict.Existing.Year?.ToString() ?? "-"} / {conflict.Existing.PlatformType}\nID: {conflict.Existing.Id}";
        IncomingText.Text = $"{conflict.Incoming.Title}\n{conflict.Incoming.Year?.ToString() ?? "-"} / {conflict.Incoming.PlatformType}\nID: {conflict.Incoming.Id}";
    }

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
