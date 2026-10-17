using System.Security.Cryptography;
using System.Text;
using Beam.Server.Connection;

namespace Beam.Server.Security;

public sealed class PairingTokenStore(BeamServerOptions options, ConnectionPresence presence) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _rotation = new(1, 1);
    private Generation _current = new(options.ExpectedToken);
    private bool _rotating;
    private bool _authorizationPaused;
    private bool _disposed;

    public string CurrentToken { get { lock (_gate) return _current.Token; } }
    public bool IsRotating { get { lock (_gate) return _rotating; } }

    public RequestLease? TryAcquire(string? provided)
    {
        if (string.IsNullOrEmpty(provided) || provided.Length > 4096) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        try
        {
            lock (_gate)
            {
                if (_disposed || _authorizationPaused || !CryptographicOperations.FixedTimeEquals(hash, _current.Hash)) return null;
                var generation = _current;
                generation.Active++;
                return new(generation.Revoked.Token, () => Release(generation));
            }
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    public async Task<string> RotateAsync()
    {
        await _rotation.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _rotating = true;
            }
            var replacement = PairingToken.Generate();
            // Failed persistence leaves active credentials and old requests intact.
            await Task.Run(() => PairingToken.Save(options.DataDirectory, replacement)).ConfigureAwait(false);
            Generation previous;
            lock (_gate)
            {
                _authorizationPaused = true;
                previous = _current;
                _current = new(replacement);
                Retire(previous);
            }
            await RevokeAndDrain(previous).ConfigureAwait(false);
            lock (_gate)
            {
                presence.Clear();
                _authorizationPaused = false;
            }
            return replacement;
        }
        finally
        {
            lock (_gate) _rotating = false;
            _rotation.Release();
        }
    }

    private static void Retire(Generation generation)
    {
        generation.Retired = true;
        if (generation.Active == 0) generation.Drained.TrySetResult();
    }

    private static async Task RevokeAndDrain(Generation generation)
    {
        try { await generation.Revoked.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* Still drain all leases if a disconnect callback fails. */ }
        await generation.Drained.Task.ConfigureAwait(false);
        generation.Revoked.Dispose();
        CryptographicOperations.ZeroMemory(generation.Hash);
    }

    private void Release(Generation generation)
    {
        lock (_gate)
        {
            generation.Active--;
            if (generation.Retired && generation.Active == 0) generation.Drained.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _rotation.WaitAsync().ConfigureAwait(false);
        try
        {
            Generation current;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _authorizationPaused = true;
                current = _current;
                Retire(current);
            }
            await RevokeAndDrain(current).ConfigureAwait(false);
        }
        finally { _rotation.Release(); }
    }

    private sealed class Generation(string token)
    {
        public string Token { get; } = token;
        public byte[] Hash { get; } = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        public CancellationTokenSource Revoked { get; } = new();
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active;
        public bool Retired;
    }

    public sealed class RequestLease : IDisposable
    {
        private Action? _release;
        public CancellationToken Revoked { get; }
        internal RequestLease(CancellationToken revoked, Action release) { Revoked = revoked; _release = release; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
