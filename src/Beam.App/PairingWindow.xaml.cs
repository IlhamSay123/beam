using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beam.Server.Connection;
using Beam.Server.Security;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;

namespace Beam.App;

public partial class PairingWindow : Window
{
    private string _token;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ConnectionPresence? _presence;
    private readonly PairingTokenStore? _tokens;
    private bool _resetting;
    private bool _refreshing;
    private bool _checking;
    private bool _closed;
    private bool? _localReachable;
    private string? _qrKey;
    public string? PairingLink { get; private set; }

    public PairingWindow(int port, string token) : this(null, port, token) { }

    public PairingWindow(string? hostname, int port, string token)
    {
        InitializeComponent();
        _token = token;
        _port = port;
        _presence = App.ServerApp?.Services.GetService<ConnectionPresence>();
        _tokens = App.ServerApp?.Services.GetService<PairingTokenStore>();
        TokenText.Text = token;
        Width = Math.Min(700, SystemParameters.WorkArea.Width);
        Height = Math.Min(800, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(600, Width);
        MinHeight = Math.Min(560, Height);
        if (hostname is not null && NetworkSetup.IsLanAddress(hostname))
            RefreshAdapters(new[] { new SetupAdapter("manual", "LAN", hostname, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, true, true) });
        else RefreshAdapters(Array.Empty<SetupAdapter>());
        Loaded += async (_, _) =>
        {
            if (hostname is null) await RefreshFromSystemAsync();
            await CheckLocalAsync();
            if (!_closed) _timer.Start();
        };
        _timer.Tick += async (_, _) => { RenderAddress(); await RefreshFromSystemAsync(); };
        Closed += (_, _) => { _closed = true; _timer.Stop(); _lifetime.Cancel(); };
    }

    public void RefreshAdapters(IEnumerable<SetupAdapter> adapters)
    {
        var selected = AdapterSelector.SelectedItem as SetupAdapter;
        var usable = NetworkSetup.Filter(adapters);
        if (AdapterSelector.ItemsSource is IEnumerable<SetupAdapter> current && current.SequenceEqual(usable))
        {
            RenderAddress();
            return;
        }
        var next = NetworkSetup.Select(usable, selected?.Key);
        AdapterSelector.ItemsSource = usable;
        AdapterSelector.SelectedItem = next;
        AdapterSelector.IsEnabled = usable.Count > 0;
        RenderAddress();
    }

    private async Task RefreshFromSystemAsync()
    {
        if (_closed || _refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var adapters = await Task.Run(NetworkSetup.Discover, _lifetime.Token);
            if (!_closed) RefreshAdapters(adapters);
        }
        catch (OperationCanceledException) when (_closed) { }
        finally { _refreshing = false; if (!_closed) RefreshButton.IsEnabled = true; }
    }

    private void RenderAddress()
    {
        var busy = _resetting || _tokens?.IsRotating == true;
        ResetPairingButton.IsEnabled = _tokens is not null && !busy;
        if (!busy && _tokens is not null && _token != _tokens.CurrentToken)
        {
            _token = _tokens.CurrentToken;
            ResetStatus.Text = "Pairing reset. Scan the new QR on each phone, and update any Shortcuts.";
        }
        TokenText.Text = busy ? "" : _token;
        var adapter = AdapterSelector.SelectedItem as SetupAdapter;
        var address = adapter is null ? null : $"http://{adapter.Address}:{_port}/app/";
        HostText.Text = address ?? "No active LAN IPv4 address";
        PairingLink = address is null || busy ? null : $"{address}?token={Uri.EscapeDataString(_token)}";
        CopyLinkButton.IsEnabled = CopyAddressButton.IsEnabled = CopyTokenButton.IsEnabled = adapter is not null && !busy;
        QrBorder.Visibility = adapter is null || busy ? Visibility.Collapsed : Visibility.Visible;
        OfflineText.Visibility = adapter is null || busy ? Visibility.Visible : Visibility.Collapsed;
        OfflineText.Text = busy ? "Resetting pairing..." : "No active LAN IPv4 address";
        AdapterStatus.Text = adapter is null ? "LAN adapter unavailable. Connect to Wi-Fi or Ethernet, then refresh." : $"Selected LAN IPv4: {adapter.Address}";
        if (_qrKey != PairingLink)
        {
            QrImage.Source = null;
            _qrKey = PairingLink;
            if (PairingLink is not null)
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(PairingLink, QRCodeGenerator.ECCLevel.Q);
                using var qr = new PngByteQRCode(data);
                using var stream = new System.IO.MemoryStream(qr.GetGraphic(10));
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                QrImage.Source = bitmap;
            }
        }
        UpdatePresence();
    }

    private ConnectionSnapshot Snapshot() => _presence?.Snapshot() ?? new(false, 0, null);
    private void UpdatePresence()
    {
        var status = ConnectionStatus.Describe(Snapshot());
        BrowserStatus.Text = status.Pill;
        LastSeenText.Text = status.Detail;
    }

    private async Task CheckLocalAsync()
    {
        if (_closed || _checking) return;
        _checking = true;
        CheckButton.IsEnabled = false;
        LocalStatus.Text = "Checking local HTTP...";
        try
        {
            var result = await ConnectionDiagnostics.CheckLocalAsync(_port, _lifetime.Token);
            if (_closed) return;
            _localReachable = result.Reachable;
            LocalStatus.Text = result.Detail;
            UpdatePresence();
        }
        catch (OperationCanceledException) when (_closed) { }
        finally { _checking = false; if (!_closed) CheckButton.IsEnabled = true; }
    }

    private void Adapter_Changed(object sender, SelectionChangedEventArgs e) => RenderAddress();
    public void RefreshPairing() => RenderAddress();

    private async void ResetPairing_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _resetting || _tokens is null || _tokens.IsRotating) return;
        var answer = MessageBox.Show(this,
            "Disconnect every paired browser and Shortcut? Active transfers may be interrupted. Your Vault and saved files will be kept. Scan the new QR to pair again.",
            "Reset pairing", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await ResetPairingAsync();
    }

    private async Task ResetPairingAsync()
    {
        if (_closed || _resetting || _tokens is null || _tokens.IsRotating) return;
        _resetting = true;
        ResetStatus.Text = "Resetting pairing. Stopping active transfers...";
        RenderAddress();
        try
        {
            await _tokens.RotateAsync();
            if (!_closed) ResetStatus.Text = "Pairing reset. Scan the new QR on each phone, and update any Shortcuts.";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            if (!_closed) ResetStatus.Text = "Could not reset pairing. The current pairing is unchanged. Check that Beam can write to its data folder, then try again.";
        }
        finally
        {
            _resetting = false;
            if (!_closed) RenderAddress();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshFromSystemAsync();
    private async void Check_Click(object sender, RoutedEventArgs e) { await RefreshFromSystemAsync(); await CheckLocalAsync(); }
    private void Later_Click(object sender, RoutedEventArgs e) => Close();
    private void CopyToken_Click(object sender, RoutedEventArgs e) { if (PairingLink is not null) Copy(_token); }
    private void CopyAddress_Click(object sender, RoutedEventArgs e) { if (PairingLink is not null) Copy(HostText.Text); }
    private void CopyLink_Click(object sender, RoutedEventArgs e) { if (PairingLink is not null) Copy(PairingLink); }
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => Copy(ConnectionDiagnostics.Format(
        AdapterSelector.SelectedItem as SetupAdapter, _localReachable, Snapshot(), _port));
    private static void Copy(string value)
    {
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.COMException) { MessageBox.Show("The clipboard is busy. Try again.", "Beam"); }
    }
}
