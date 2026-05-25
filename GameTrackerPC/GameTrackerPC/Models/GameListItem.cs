using System.Windows.Media;

namespace GameTrackerPC.Models;

public sealed class GameListItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Statuses { get; set; } = string.Empty;
    public string YearText { get; set; } = string.Empty;
    public string UpdatedText { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public ImageSource? CoverPreview { get; set; }
    public double ImageScale { get; set; } = 1;
    public double ImageOffsetX { get; set; }
    public double ImageOffsetY { get; set; }
}
