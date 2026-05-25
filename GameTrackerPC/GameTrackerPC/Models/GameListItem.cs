using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GameTrackerPC.Models;

public sealed class GameListItem : INotifyPropertyChanged
{
    private double _coverHeight;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Statuses { get; set; } = string.Empty;
    public string YearText { get; set; } = string.Empty;
    public string UpdatedText { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public double CoverAspectRatio { get; set; } = 3d / 4d;

    public double CoverHeight
    {
        get => _coverHeight;
        set
        {
            if (Math.Abs(_coverHeight - value) < 0.01)
            {
                return;
            }

            _coverHeight = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
