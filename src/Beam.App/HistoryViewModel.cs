using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using Beam.Server.Storage;

namespace Beam.App;

public class HistoryEntryViewModel : INotifyPropertyChanged
{
    internal HistoryThumbnailCache? ThumbnailCache { get; init; }
    private WeakReference<ImageSource>? _thumbnail;
    private Task _thumbnailLoad = Task.CompletedTask;
    public event PropertyChangedEventHandler? PropertyChanged;
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
    public string DirectionLabel => Direction == "inbound" ? "From phone" : "To phone";
    public string State => Missing ? "File missing" : Direction == "inbound" ? "Received from phone" : Read ? IsText ? "Copy acknowledged" : "Download started on phone" : "Queued for phone";
    public Brush StateBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Missing ? "#FF8F86" : Direction == "outbound" ? "#F4CE6A" : "#78DDD0"));
    public string Icon => TransferSymbol.For(new HistoryItem { Id = Id, Direction = Direction, Name = Name, IsText = IsText, ContentType = ContentType, TimestampUtc = TimestampUtc });
    public string DisplayTime => TimestampUtc.ToLocalTime().ToString("dd MMM, HH:mm");
    public string DisplaySize => IsText ? "Note" : SizeBytes < 1024 ? $"{SizeBytes} B" : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024d:0.#} KB" : $"{SizeBytes / (1024d * 1024):0.#} MB";
    public string Subtitle => $"{DirectionLabel}  /  {DisplaySize}  /  {DisplayTime}";
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is not null && _thumbnail.TryGetTarget(out var image)) return image;
            _ = EnsureThumbnailAsync();
            return _thumbnail is not null && _thumbnail.TryGetTarget(out image) ? image : null;
        }
    }
    public Task EnsureThumbnailAsync(CancellationToken cancellationToken = default)
    {
        if (!IsImage || Missing || ThumbnailCache is null || (_thumbnail is not null && _thumbnail.TryGetTarget(out _))) return Task.CompletedTask;
        if (_thumbnailLoad.IsCompleted) _thumbnailLoad = LoadThumbnailAsync();
        return cancellationToken.CanBeCanceled ? _thumbnailLoad.WaitAsync(cancellationToken) : _thumbnailLoad;
    }
    private async Task LoadThumbnailAsync()
    {
        var image = await ThumbnailCache!.GetAsync(FilePath);
        if (image is null) return;
        // Entries do not pin every bitmap that has ever been scrolled into view.
        _thumbnail = new WeakReference<ImageSource>(image);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }
}

public class HistoryViewModel
{
    private readonly HistoryIndex _index;
    private readonly HistoryThumbnailCache _thumbnails;
    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new();
    public string SearchQuery { get; set; } = "";
    public string DirectionFilter { get; set; } = "All";
    public string KindFilter { get; set; } = "All types";
    public bool OldestFirst { get; set; }
    public int TotalCount { get; private set; }
    public int QueuedCount { get; private set; }
    public HistoryViewModel(HistoryIndex index, HistoryThumbnailCache? thumbnails = null)
    {
        _index = index;
        _thumbnails = thumbnails ?? new HistoryThumbnailCache();
    }
    public void StopThumbnailLoading() => _thumbnails.Dispose();
    public Task<ImageSource?> LoadActivityThumbnailAsync(HistoryItem item, CancellationToken cancellationToken) =>
        TransferSymbol.For(item) == TransferSymbol.Photo ? _thumbnails.GetAsync(item.FilePath, cancellationToken) : Task.FromResult<ImageSource?>(null);
    public void Refresh()
    {
        Apply(Capture(SearchQuery, DirectionFilter, KindFilter, OldestFirst, Entries.ToDictionary(e => e.Id)));
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default, Action? beforeApply = null)
    {
        var search = SearchQuery;
        var direction = DirectionFilter;
        var kind = KindFilter;
        var oldest = OldestFirst;
        var previous = Entries.ToDictionary(e => e.Id);
        var snapshot = await Task.Run(() => Capture(search, direction, kind, oldest, previous), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        beforeApply?.Invoke();
        Apply(snapshot);
    }
    private sealed record HistorySnapshot(int Total, int Queued, List<HistoryEntryViewModel> Entries);
    private HistorySnapshot Capture(string search, string direction, string kind, bool oldest, IReadOnlyDictionary<string, HistoryEntryViewModel> previous)
    {
        var items = _index.List().Select(TransferActivity.Snapshot).ToArray();
        var candidates = items.Select(item => new HistoryEntryViewModel
        {
            ThumbnailCache = _thumbnails, Id = item.Id, Direction = item.Direction, Name = item.Name, IsText = item.IsText,
            Read = item.Read, TextContent = item.TextContent, FilePath = item.FilePath,
            ContentType = item.ContentType, SizeBytes = item.SizeBytes, TimestampUtc = item.TimestampUtc,
            Missing = !item.IsText && (item.FilePath is null || !File.Exists(item.FilePath))
        }).Where(entry =>
            (direction == "All" || entry.Direction == (direction == "Received" ? "inbound" : "outbound"))
            && (kind == "All types" || entry.Kind == kind)
            && (string.IsNullOrWhiteSpace(search) || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (entry.TextContent?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)));
        var ordered = (oldest ? candidates.OrderBy(e => e.TimestampUtc) : candidates.OrderByDescending(e => e.TimestampUtc)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            if (previous.TryGetValue(entry.Id, out var cached) && cached.State == entry.State) ordered[i] = cached;
        }
        return new(items.Length, items.Count(i => i.Direction == "outbound" && !i.Read), ordered);
    }
    private void Apply(HistorySnapshot snapshot)
    {
        TotalCount = snapshot.Total;
        QueuedCount = snapshot.Queued;
        var ordered = snapshot.Entries;
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
