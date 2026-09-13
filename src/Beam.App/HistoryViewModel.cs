using System.Collections.ObjectModel;
using Beam.Server.Storage;

namespace Beam.App;

public class HistoryEntryViewModel
{
    public required string Id { get; init; }
    public required string Direction { get; init; }
    public required string Name { get; init; }
    public required bool IsText { get; init; }
    public string? TextContent { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public string DisplayTime => TimestampUtc.ToLocalTime().ToString("t");
}

public class HistoryViewModel
{
    private readonly HistoryIndex _index;

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new();

    public HistoryViewModel(HistoryIndex index)
    {
        _index = index;
        Refresh();
    }

    public void Refresh()
    {
        Entries.Clear();
        foreach (var item in _index.List())
        {
            Entries.Add(new HistoryEntryViewModel
            {
                Id = item.Id,
                Direction = item.Direction,
                Name = item.Name,
                IsText = item.IsText,
                TextContent = item.TextContent,
                TimestampUtc = item.TimestampUtc
            });
        }
    }
}
