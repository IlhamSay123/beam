using Makaretu.Dns;

namespace Beam.App;

public class MdnsAdvertiser
{
    private ServiceDiscovery? _discovery;

    public void Start(int port)
    {
        var service = new ServiceProfile("beam-pc", "_beam._tcp", (ushort)port);
        _discovery = new ServiceDiscovery();
        _discovery.Advertise(service);
    }

    public void Stop()
    {
        _discovery?.Dispose();
        _discovery = null;
    }
}
