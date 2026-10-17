using System.IO;
using Beam.Server.Storage;

namespace Beam.App;

public enum TransferActivityKind { Queued, Received, Acknowledged }

public sealed class TransferActivity : EventArgs
{
    public TransferActivityKind Kind { get; }
    public IReadOnlyList<HistoryItem> Items { get; }
    public bool Outgoing => Kind != TransferActivityKind.Received;
    public string Status => Kind switch
    {
        TransferActivityKind.Queued => "Queued for phone",
        TransferActivityKind.Received => "Received from phone",
        _ => Items.All(i => i.IsText) ? "Copy acknowledged" : "Download started on phone"
    };
    public string PillStatus => Kind switch
    {
        TransferActivityKind.Queued => "Queued",
        TransferActivityKind.Received => "Received",
        _ => Items.All(i => i.IsText) ? "Copy acknowledged" : "Download started"
    };
    public string Detail => Items.Count > 1 ? $"{Items.Count} items / {Title(Items[0])}" : Title(Items[0]);

    public TransferActivity(TransferActivityKind kind, IEnumerable<HistoryItem> items)
    {
        Kind = kind;
        Items = items.Select(Snapshot).ToArray();
        if (Items.Count == 0) throw new ArgumentException("An activity must contain an item.", nameof(items));
    }

    private static string Title(HistoryItem item) => item.IsText
        ? (item.TextContent ?? "Note").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Note"
        : item.Name;

    // HistoryIndex returns mutable records; freeze read flags before crossing to the dispatcher.
    public static HistoryItem Snapshot(HistoryItem item) => new()
    {
        Id = item.Id, Direction = item.Direction, Name = item.Name, ContentType = item.ContentType,
        SizeBytes = item.SizeBytes, TimestampUtc = item.TimestampUtc, Read = item.Read,
        IsText = item.IsText, TextContent = item.TextContent, FilePath = item.FilePath
    };
}

public sealed class TransferActivityTracker
{
    private readonly Dictionary<string, bool> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announcedQueues = new(StringComparer.Ordinal);

    public void Seed(IEnumerable<HistoryItem> items)
    {
        foreach (var item in items) _known.TryAdd(item.Id, item.Read);
    }

    public IReadOnlyList<TransferActivity> Observe(IEnumerable<HistoryItem> items, bool deferQueued = false)
    {
        var changes = new List<(TransferActivityKind Kind, HistoryItem Item)>();
        foreach (var item in items.OrderBy(i => i.TimestampUtc))
        {
            var known = _known.TryGetValue(item.Id, out var wasRead);
            if (item.Direction == "outbound")
            {
                if (item.Read && (!known || !wasRead)) changes.Add((TransferActivityKind.Acknowledged, item));
                else if (!known && !item.Read)
                {
                    if (deferQueued) continue;
                    _announcedQueues.Add(item.Id);
                    changes.Add((TransferActivityKind.Queued, item));
                }
            }
            else if (item.Direction == "inbound" && !known) changes.Add((TransferActivityKind.Received, item));
            // A stale snapshot must never turn an acknowledgement back into an unread item.
            _known[item.Id] = wasRead || item.Read;
        }
        return Batch(changes);
    }

    public IReadOnlyList<TransferActivity> RecordQueued(IEnumerable<HistoryItem> items)
    {
        var changes = new List<(TransferActivityKind Kind, HistoryItem Item)>();
        foreach (var item in items)
        {
            if (item.Direction != "outbound" || (_known.TryGetValue(item.Id, out var read) && read) || !_announcedQueues.Add(item.Id)) continue;
            _known.TryAdd(item.Id, false);
            changes.Add((TransferActivityKind.Queued, item));
        }
        return Batch(changes);
    }

    private static IReadOnlyList<TransferActivity> Batch(IEnumerable<(TransferActivityKind Kind, HistoryItem Item)> changes) => changes
        .GroupBy(c => (c.Kind, Copy: c.Kind == TransferActivityKind.Acknowledged && c.Item.IsText))
        .OrderBy(g => g.Key.Kind)
        .Select(g => new TransferActivity(g.Key.Kind, g.Select(c => c.Item))).ToArray();
}

public static class TransferSymbol
{
    public const string Photo = "\uEB9F", Message = "\uE8F2", Document = "\uE8A5",
        Video = "\uE714", Audio = "\uE8D6", Archive = "\uF012";

    public static string For(HistoryItem item)
    {
        if (item.IsText) return Message;
        var extension = Path.GetExtension(item.Name).ToLowerInvariant();
        var type = item.ContentType;
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".avif" }.Contains(extension)) return Photo;
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || new[] { ".mp4", ".mov", ".m4v", ".webm", ".avi", ".mkv" }.Contains(extension)) return Video;
        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || new[] { ".mp3", ".m4a", ".wav", ".flac", ".aac", ".ogg" }.Contains(extension)) return Audio;
        if (new[] { ".zip", ".7z", ".rar", ".gz", ".tar", ".bz2" }.Contains(extension) || type.Contains("zip", StringComparison.OrdinalIgnoreCase) || type.Contains("compressed", StringComparison.OrdinalIgnoreCase)) return Archive;
        return Document;
    }
}
