using Makaretu.Dns;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beam.App;

public class MdnsAdvertiser
{
    private ServiceDiscovery? _discovery;
    private readonly Func<ServiceDiscovery> _createDiscovery;

    public MdnsAdvertiser() : this(() => new ServiceDiscovery()) { }
    public MdnsAdvertiser(Func<ServiceDiscovery> createDiscovery) => _createDiscovery = createDiscovery;

    // Makaretu.Dns computes the advertised hostname from the service profile
    // (e.g. "beam-pc" + "_beam._tcp" yields "beam-pc.beam.local", not the
    // naively-expected "beam-pc.local"). Exposed here so callers can advertise
    // and display the *actual* published hostname instead of guessing at it.
    public string? HostName { get; private set; }

    public bool Start(int port)
    {
        Stop();
        try
        {
            var service = new ServiceProfile("beam-pc", "_beam._tcp", (ushort)port);
            _discovery = _createDiscovery();
            _discovery.Advertise(service);
            HostName = service.HostName.ToString();
            return true;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            Stop();
            return false;
        }
    }

    private static bool IsUnavailable(Exception error) => error is SocketException or NetworkInformationException
        or InvalidOperationException or NotSupportedException or ArgumentException;

    public void Stop()
    {
        try { _discovery?.Dispose(); }
        catch (Exception error) when (IsUnavailable(error)) { }
        finally { _discovery = null; HostName = null; }
    }
}
