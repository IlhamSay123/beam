namespace Beam.Server.Transfers;

public sealed class ReceiveCoordinator
{
    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPending(string key) { lock (_gate) return _pending.ContainsKey(key); }

    public async Task<IDisposable?> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out entry!))
            {
                if (_pending.Count >= 32) return null;
                entry = new Entry();
                _pending.Add(key, entry);
            }
            if (entry.References >= 4) return null;
            entry.References++;
        }
        try { await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { Release(key, entry, ownsSemaphore: false); throw; }
        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry, bool ownsSemaphore)
    {
        lock (_gate)
        {
            if (ownsSemaphore) entry.Semaphore.Release();
            if (--entry.References != 0) return;
            _pending.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Lease(ReceiveCoordinator owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key, entry, ownsSemaphore: true);
        }
    }
}
