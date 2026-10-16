using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beam.Server.Storage;

namespace Beam.App;

public class HistoryEntryViewModel
{
    public required string Id { get; init; }
    public required string Direction { get; init; }
    public required string Name { get; init; }
    public required bool IsText { get; init; }
    public bool Read { get; init; }
    public string? TextContent { get; init; }
    public string? FilePath { get; init; }
    public required string ContentType { get; init; }
    public long SizeBytes { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public bool Missing { get; init; }
    public string Kind => IsText ? "Notes" : IsImage ? "Images" : "Files";
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic" }.Contains(Path.GetExtension(Name).ToLowerInvariant());
    public string Title => IsText ? (TextContent ?? "Note").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Note" : Name;
    public string DirectionLabel => Direction == "inbound" ? "Received" : "Sent";
    public string State => Missing ? "File missing" : Direction == "inbound" ? "Received" : Read ? "Delivered" : "Queued";
    public Brush StateBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Missing ? "#FF8F86" : State == "Queued" ? "#F4CE6A" : "#78DDD0"));
    public string Icon => IsText ? "\uE8A5" : IsImage ? "\uEB9F" : "\uE8A5";
    public string DisplayTime => TimestampUtc.ToLocalTime().ToString("dd MMM, HH:mm");
    public string DisplaySize => IsText ? "Note" : SizeBytes < 1024 ? $"{SizeBytes} B" : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024d:0.#} KB" : $"{SizeBytes / (1024d * 1024):0.#} MB";
    public string Subtitle => $"{DirectionLabel}  /  {DisplaySize}  /  {DisplayTime}";
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnailLoaded) return _thumbnail;
            _thumbnailLoaded = true;
            if (!IsImage || Missing) return null;
            try
            {
                using var stream = File.OpenRead(FilePath!);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 600;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                _thumbnail = bitmap;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException) { }
            return _thumbnail;
        }
    }
}

public class HistoryViewModel
{
    private readonly HistoryIndex _index;
    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new();
    public string SearchQuery { get; set; } = "";
    public string DirectionFilter { get; set; } = "All";
    public string KindFilter { get; set; } = "All types";
    public bool OldestFirst { get; set; }
    public int TotalCount { get; private set; }
    public int QueuedCount { get; private set; }
    public HistoryViewModel(HistoryIndex index) { _index = index; Refresh(); }
    public void Refresh()
    {
        var items = _index.List();
        TotalCount = items.Count;
        QueuedCount = items.Count(i => i.Direction == "outbound" && !i.Read);
        var candidates = items.Select(item => new HistoryEntryViewModel
        {
            Id = item.Id, Direction = item.Direction, Name = item.Name, IsText = item.IsText,
            Read = item.Read, TextContent = item.TextContent, FilePath = item.FilePath,
            ContentType = item.ContentType, SizeBytes = item.SizeBytes, TimestampUtc = item.TimestampUtc,
            Missing = !item.IsText && (item.FilePath is null || !File.Exists(item.FilePath))
        }).Where(entry =>
            (DirectionFilter == "All" || entry.DirectionLabel == DirectionFilter)
            && (KindFilter == "All types" || entry.Kind == KindFilter)
            && (string.IsNullOrWhiteSpace(SearchQuery) || entry.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
                || (entry.TextContent?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false)));
        var ordered = (OldestFirst ? candidates.OrderBy(e => e.TimestampUtc) : candidates.OrderByDescending(e => e.TimestampUtc)).ToList();
        if (Entries.Select(e => e.Id).SequenceEqual(ordered.Select(e => e.Id)))
        {
            for (var i = 0; i < ordered.Count; i++)
                if (Entries[i].State != ordered[i].State) Entries[i] = ordered[i];
            return;
        }
        var existing = Entries.ToDictionary(e => e.Id);
        Entries.Clear();
        foreach (var entry in ordered)
            Entries.Add(existing.TryGetValue(entry.Id, out var previous) && previous.State == entry.State ? previous : entry);
    }
}
