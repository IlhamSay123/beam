namespace Beam.Server.Connection;

public sealed record ConnectionSnapshot(bool PhoneActive, int ActiveBrowsers, DateTimeOffset? LastSeenUtc);

public sealed class ConnectionPresence(TimeProvider? clock = null)
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastSeenUtc;

    public void RecordHeartbeat(string sessionId)
    {
        if (sessionId?.Length != 32 || !Guid.TryParseExact(sessionId, "N", out _))
            throw new ArgumentException("A browser session must be 32 hexadecimal characters.", nameof(sessionId));
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            if (!_sessions.ContainsKey(sessionId) && _sessions.Count >= 32)
                _sessions.Remove(_sessions.MinBy(entry => entry.Value).Key);
            _sessions[sessionId] = now;
            _lastSeenUtc = _clock.GetUtcNow();
        }
    }

    public ConnectionSnapshot Snapshot()
    {
        lock (_gate)
        {
            Prune(_clock.GetTimestamp());
            return new(_sessions.Count > 0, _sessions.Count, _lastSeenUtc);
        }
    }

    private void Prune(long now)
    {
        foreach (var key in _sessions.Where(entry => _clock.GetElapsedTime(entry.Value, now) >= ActiveWindow)
                     .Select(entry => entry.Key).ToArray())
            _sessions.Remove(key);
    }
}
