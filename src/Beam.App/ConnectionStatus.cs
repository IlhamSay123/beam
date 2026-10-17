using Beam.Server.Connection;

namespace Beam.App;

public sealed record ConnectionStatus(string Header, string Pill, string Detail)
{
    public static ConnectionStatus Describe(ConnectionSnapshot snapshot)
    {
        var state = snapshot.PhoneActive ? "Browser active" : snapshot.LastSeenUtc is null ? "No browser activity" : "Browser inactive";
        var count = snapshot.PhoneActive && snapshot.ActiveBrowsers > 1 ? $" ({snapshot.ActiveBrowsers})" : "";
        var seen = snapshot.LastSeenUtc is { } last ? $"Last browser activity {last.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC." : "No remote browser has reached Beam this session.";
        return new($"Server ready / {state}{count}", state,
            $"{seen}\nRecent browser activity expires after 15 seconds. It is not device identity or proof of receipt.");
    }
}
