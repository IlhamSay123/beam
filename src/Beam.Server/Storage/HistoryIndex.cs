using System.Text.Json;

namespace Beam.Server.Storage;

public class HistoryIndex
{
    private readonly string _indexFilePath;
    private readonly object _lock = new();
    private List<HistoryItem> _items;

    public HistoryIndex(string indexFilePath)
    {
        _indexFilePath = indexFilePath;
        _items = Load();
    }

    private const int DefaultMaxItems = 50;
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    public HistoryItem Add(HistoryItem item)
    {
        lock (_lock)
        {
            _items.Add(item);
            PruneLocked(DefaultMaxItems, DefaultMaxAge);
            Save();
            return item;
        }
    }

    public IReadOnlyList<HistoryItem> List()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(i => i.TimestampUtc).ToList();
        }
    }

    public IReadOnlyList<HistoryItem> ListUnread(string direction)
    {
        lock (_lock)
        {
            return _items
                .Where(i => i.Direction == direction && !i.Read)
                .OrderByDescending(i => i.TimestampUtc)
                .ToList();
        }
    }

    public HistoryItem? Get(string id)
    {
        lock (_lock)
        {
            return _items.FirstOrDefault(i => i.Id == id);
        }
    }

    public bool MarkRead(string id)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
            {
                return false;
            }

            item.Read = true;
            Save();
            return true;
        }
    }

    public void Prune(int maxItems, TimeSpan maxAge)
    {
        lock (_lock)
        {
            PruneLocked(maxItems, maxAge);
            Save();
        }
    }

    // Caller must hold _lock. Does not call Save() — callers that also
    // mutate _items (e.g. Add) save once after this returns.
    private void PruneLocked(int maxItems, TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        _items = _items
            .Where(i => i.TimestampUtc >= cutoff)
            .OrderByDescending(i => i.TimestampUtc)
            .Take(maxItems)
            .ToList();
    }

    private List<HistoryItem> Load()
    {
        if (!File.Exists(_indexFilePath))
        {
            return new List<HistoryItem>();
        }

        var json = File.ReadAllText(_indexFilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<HistoryItem>();
        }

        return JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? new List<HistoryItem>();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_indexFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_items);
        File.WriteAllText(_indexFilePath, json);
    }
}
