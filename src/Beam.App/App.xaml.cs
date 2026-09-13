using System.Windows;
using Beam.Server;
using Beam.Server.Security;
using Microsoft.AspNetCore.Builder;

namespace Beam.App;

public partial class App : Application
{
    public static WebApplication ServerApp { get; private set; } = null!;
    public static BeamServerOptions Options { get; private set; } = null!;

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

        _trayIconService = new TrayIconService();
        _trayIconService.Initialize();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        await ServerApp.StopAsync();
        base.OnExit(e);
    }
}
