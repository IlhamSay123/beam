using System.Windows;
using Beam.Server;
using Beam.Server.Security;
using Microsoft.AspNetCore.Builder;

namespace Beam.App;

public partial class App : Application
{
    public static WebApplication ServerApp { get; private set; } = null!;
    public static BeamServerOptions Options { get; private set; } = null!;

    public static MdnsAdvertiser Advertiser { get; } = new();

    private TrayIconService? _trayIconService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Beam");
        var token = PairingToken.LoadOrCreate(dataDirectory);
        Options = new BeamServerOptions(dataDirectory, token, Port: 8787);

        ServerApp = BeamServerHost.Build(Options);
        await ServerApp.StartAsync();

        Advertiser.Start(Options.Port);

        _trayIconService = new TrayIconService();
        _trayIconService.Initialize();
    }

    public static void ShowPairing()
    {
        var host = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 or System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)
            .OrderByDescending(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
        new PairingWindow(host?.ToString() ?? Advertiser.HostName ?? "beam-pc.local", Options.Port, Options.ExpectedToken).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        Advertiser.Stop();
        if (ServerApp is not null)
        {
            try
            {
                Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ServerApp.StopAsync(timeout.Token);
                    await ServerApp.DisposeAsync();
                }).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
        }
        base.OnExit(e);
    }
}
