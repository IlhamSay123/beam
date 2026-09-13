using Makaretu.Dns;

namespace Beam.App;

public class MdnsAdvertiser
{
    private ServiceDiscovery? _discovery;

    // Makaretu.Dns computes the advertised hostname from the service profile
    // (e.g. "beam-pc" + "_beam._tcp" yields "beam-pc.beam.local", not the
    // naively-expected "beam-pc.local"). Exposed here so callers can advertise
    // and display the *actual* published hostname instead of guessing at it.
    public string? HostName { get; private set; }

    public void Start(int port)
    {
        var service = new ServiceProfile("beam-pc", "_beam._tcp", (ushort)port);
        HostName = service.HostName.ToString();
        _discovery = new ServiceDiscovery();
        _discovery.Advertise(service);
    }

    public void Stop()
    {
        _discovery?.Dispose();
        _discovery = null;
    }
}
