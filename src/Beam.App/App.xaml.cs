using System.Windows;
using Beam.Server;
using Beam.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.App;

public partial class App : Application
{
    public static WebApplication ServerApp { get; private set; } = null!;
    public static BeamServerOptions Options { get; private set; } = null!;

    public static MdnsAdvertiser Advertiser { get; } = new();

    private TrayIconService? _trayIconService;
    private static PairingWindow? _pairingWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Beam");
        try
        {
            var token = PairingToken.LoadOrCreate(dataDirectory);
            Options = new BeamServerOptions(dataDirectory, token, Port: 8787);
            ServerApp = BeamServerHost.Build(Options);
            if (!await TryStartServerAsync(ServerApp, Options.Port,
                message => MessageBox.Show(message, "Beam could not start", MessageBoxButton.OK, MessageBoxImage.Warning)))
            {
                ServerApp = null!;
                Shutdown(1);
                return;
            }
            Advertiser.Start(Options.Port);
            _trayIconService = new TrayIconService();
            _trayIconService.Initialize();
            var setupDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beam");
            if (new SetupMarker(setupDirectory).TryClaimOffer()) ShowPairing();
        }
        catch (Exception)
        {
            MessageBox.Show("Beam could not start. Close any other Beam instance, check that your Documents folder is available, and try again.",
                "Beam could not start", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }

    public static async Task<bool> TryStartServerAsync(WebApplication server, int port, Action<string> reportFailure)
    {
        try { await server.StartAsync(); return true; }
        catch (Exception)
        {
            try { await server.DisposeAsync(); } catch (Exception) { }
            reportFailure($"Beam could not listen on port {port}. Close any other Beam instance or app using this port, then reopen Beam. No firewall settings were changed.");
            return false;
        }
    }

    public static void ShowPairing()
    {
        var owner = Current.MainWindow;
        if (owner?.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        if (_pairingWindow is not null)
        {
            if (_pairingWindow.WindowState == WindowState.Minimized) _pairingWindow.WindowState = WindowState.Normal;
            _pairingWindow.RefreshPairing();
            _pairingWindow.Show();
            _pairingWindow.Activate();
            return;
        }
        _pairingWindow = new PairingWindow(Options.Port, ServerApp.Services.GetRequiredService<PairingTokenStore>().CurrentToken) { Owner = owner };
        _pairingWindow.Closed += (_, _) => _pairingWindow = null;
        _pairingWindow.Show();
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
            catch (Exception) { }
        }
        base.OnExit(e);
    }
}
