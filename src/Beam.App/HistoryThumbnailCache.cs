using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Beam.App;

// Called on the dispatcher; only the decoder runs on worker threads.
public sealed class HistoryThumbnailCache : IDisposable
{
    public const int Capacity = 24;
    private readonly Dictionary<string, LinkedListNode<(string Path, ImageSource? Image)>> _cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, ImageSource? Image)> _recent = new();
    private readonly Dictionary<string, TaskCompletionSource<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decoders = new(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _stopToken;
    private readonly Func<string, ImageSource?> _decode;
    private bool _disposed;
    public int CachedCount => _cached.Count;

    public HistoryThumbnailCache(Func<string, ImageSource?>? decoder = null)
    {
        _decode = decoder ?? Decode;
        _stopToken = _lifetime.Token;
    }

    public Task<ImageSource?> GetAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrEmpty(path)) return Task.FromResult<ImageSource?>(null);
        if (_cached.TryGetValue(path, out var cached))
        {
            _recent.Remove(cached);
            _recent.AddFirst(cached);
            return Task.FromResult(cached.Value.Image);
        }
        if (!_pending.TryGetValue(path, out var pending))
        {
            if (_pending.Count >= Capacity) return Task.FromResult<ImageSource?>(null);
            pending = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(path, pending);
            _ = LoadAsync(path, pending);
        }
        return cancellationToken.CanBeCanceled ? pending.Task.WaitAsync(cancellationToken) : pending.Task;
    }

    private async Task LoadAsync(string path, TaskCompletionSource<ImageSource?> completion)
    {
        var acquired = false;
        try
        {
            await _decoders.WaitAsync(_stopToken);
            acquired = true;
            var image = await Task.Run(() => _decode(path), _stopToken);
            _stopToken.ThrowIfCancellationRequested();
            var cached = _recent.AddFirst((path, image));
            _cached.Add(path, cached);
            while (_cached.Count > Capacity)
            {
                var oldest = _recent.Last!;
                _cached.Remove(oldest.Value.Path);
                _recent.RemoveLast();
            }
            completion.TrySetResult(image);
        }
        catch (OperationCanceledException) { completion.TrySetResult(null); }
        catch (Exception error) { completion.TrySetException(error); }
        finally
        {
            if (acquired) _decoders.Release();
            _pending.Remove(path);
        }
    }

    private static ImageSource? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 600;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var completion in _pending.Values) completion.TrySetResult(null);
        _pending.Clear();
        _cached.Clear();
        _recent.Clear();
    }
}
