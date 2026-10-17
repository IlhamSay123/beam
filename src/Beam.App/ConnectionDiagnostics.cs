using System.Net.Http;
using Beam.Server.Connection;

namespace Beam.App;

public sealed record LocalCheckResult(bool Reachable, string Detail);

public static class ConnectionDiagnostics
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<LocalCheckResult> CheckLocalAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await Client.GetAsync($"http://127.0.0.1:{port}/app/", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.StatusCode == System.Net.HttpStatusCode.OK
                ? new(true, "Local HTTP reachable") : new(false, "Local HTTP did not return the Beam page. Restart Beam and check the port.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "Local HTTP timed out. Restart Beam and check the port."); }
        catch (HttpRequestException)
        { return new(false, "Local HTTP unavailable. Restart Beam and check the port."); }
    }

    public static string Format(SetupAdapter? adapter, bool? localReachable, ConnectionSnapshot snapshot, int port)
    {
        // Only known fields enter the clipboard: adapter names and exception text may contain private data.
        var address = adapter is not null && NetworkSetup.IsLanAddress(adapter.Address) ? adapter.Address : "No active LAN IPv4 adapter";
        var local = localReachable is true ? "reachable" : localReachable is false ? "unavailable" : "not checked";
        return $"Beam connection diagnostics\nPort: {port}\nSelected IPv4: {address}\nLocal HTTP: {local}\n{ConnectionStatus.Describe(snapshot).Header}\n{ConnectionStatus.Describe(snapshot).Detail}\n" +
            "Local checks cannot verify the phone's Wi-Fi, router isolation, VPN or firewall policy. Check the same Wi-Fi and Windows private-network firewall permission. Beam does not change firewall settings.";
    }
}
