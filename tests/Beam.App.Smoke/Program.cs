using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beam.App;
using Beam.Server;
using Beam.Server.Storage;
using Beam.Server.Connection;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

internal sealed class SmokeApplication : App
{
    protected override void OnStartup(StartupEventArgs e) { }
}

internal static class Program
{
    private sealed class ControlledClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan span) { _now += span; _ticks += span.Ticks; }
    }
    private static Type NativeType(string name)
    {
        var type = typeof(App).Assembly.GetType("Beam.App." + name);
        Require(type is not null, $"Missing native setup behavior: {name}.");
        return type!;
    }
    private static object InvokeNative(string type, string method, params object?[] args)
    {
        var entry = NativeType(type).GetMethod(method);
        Require(entry is not null, $"Missing native setup behavior: {type}.{method}.");
        return entry!.Invoke(null, args)!;
    }
    private static object Adapter(string id, string address, NetworkInterfaceType kind = NetworkInterfaceType.Wireless80211,
        bool up = true, bool gateway = true, string? name = null) =>
        Activator.CreateInstance(NativeType("SetupAdapter"), id, name ?? id, address, kind, up, gateway)!;
    private static Array AdapterArray(params object[] adapters)
    {
        var array = Array.CreateInstance(NativeType("SetupAdapter"), adapters.Length);
        for (var i = 0; i < adapters.Length; i++) array.SetValue(adapters[i], i);
        return array;
    }
    private static async Task CheckConnectionSetup(string folder, string? screenshots)
    {
        var failures = new List<string>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine("PASS: " + name); }
            catch (Exception error)
            {
                var cause = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : error;
                failures.Add(name + ": " + cause!.Message);
                Console.Error.WriteLine("FAIL: " + failures[^1]);
            }
        }
        await Check("adapter filtering and stable explicit selection", () =>
        {
            var wifi = Adapter("wifi", "192.168.1.22");
            var ethernet = Adapter("ethernet", "10.0.0.8", NetworkInterfaceType.Ethernet);
            var alternate = Adapter("vpn", "10.8.0.2", NetworkInterfaceType.Tunnel, gateway: false);
            var all = AdapterArray(alternate, ethernet, wifi, Adapter("down", "192.168.1.23", up: false),
                Adapter("loopback", "127.0.0.1"), Adapter("link-local", "169.254.1.2"), Adapter("ipv6", "fe80::1"), Adapter("invalid", "offline.local"));
            var usable = ((System.Collections.IEnumerable)InvokeNative("NetworkSetup", "Filter", all)).Cast<object>().ToArray();
            Require(usable.Length == 3 && ReferenceEquals(usable[0], wifi), "Inactive, loopback, link-local or non-IPv4 address was offered, or preferred Wi-Fi was lost.");
            var key = (string)alternate.GetType().GetProperty("Key")!.GetValue(alternate)!;
            Require(ReferenceEquals(InvokeNative("NetworkSetup", "Select", AdapterArray(usable), key), alternate), "Explicit alternate adapter selection was discarded.");
            Require(ReferenceEquals(InvokeNative("NetworkSetup", "Select", AdapterArray(ethernet, wifi), key), wifi), "Disappeared adapter did not fall back to a preferred usable address.");
            Require(InvokeNative("NetworkSetup", "Select", AdapterArray(), null) is null, "Offline selection invented a host.");
            return Task.CompletedTask;
        });
        await Check("first offer persists independently without touching token/history", () =>
        {
            var root = Path.Combine(folder, "first-offer");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "history.json"), "private-history");
            File.WriteAllText(Path.Combine(root, "pairing-token.txt"), "private-token");
            var marker = Activator.CreateInstance(NativeType("SetupMarker"), root)!;
            bool Claim(object value) => (bool)value.GetType().GetMethod("TryClaimOffer")!.Invoke(value, null)!;
            Require(Claim(marker), "First launch was not offered setup.");
            Require(!Claim(Activator.CreateInstance(NativeType("SetupMarker"), root)!), "Later launch repeated the first-run offer.");
            Require(File.ReadAllText(Path.Combine(root, "history.json")) == "private-history" && File.ReadAllText(Path.Combine(root, "pairing-token.txt")) == "private-token", "Setup marker changed private data.");
            Require(!Claim(Activator.CreateInstance(NativeType("SetupMarker"), Path.Combine(root, "history.json"))!), "Unwritable setup marker should fail safely.");
            return Task.CompletedTask;
        });
        await Check("pairing adapter changes synchronize address/link/QR and offline clears pairing link", async () =>
        {
            await using var fixture = BeamServerHost.Build(new BeamServerOptions(Path.Combine(folder, "pairing-http"), "smoke-token", 0));
            await fixture.StartAsync();
            var port = new Uri(fixture.Urls.First()).Port;
            var workspace = new Window();
            Application.Current.MainWindow = workspace;
            var pairing = new PairingWindow("192.168.1.22", port, "smoke-token");
            try
            {
                pairing.Show();
                var refresh = typeof(PairingWindow).GetMethod("RefreshAdapters");
                Require(refresh is not null, "Pairing has no refreshable adapter selection.");
                var available = AdapterArray(Adapter("wifi", "192.168.1.22"), Adapter("ethernet", "10.0.0.8", NetworkInterfaceType.Ethernet));
                refresh!.Invoke(pairing, new object[] { available });
                var selector = (ComboBox)pairing.FindName("AdapterSelector");
                var qr = (Image)pairing.FindName("QrImage");
                var firstQr = qr.Source;
                selector.SelectedIndex = 1;
                Require(((TextBlock)pairing.FindName("HostText")).Text == $"http://10.0.0.8:{port}/app/", "Manual address did not follow adapter selection.");
                Require(qr.Source is not null && !ReferenceEquals(qr.Source, firstQr), "QR retained the previous adapter.");
                Require((string)typeof(PairingWindow).GetProperty("PairingLink")!.GetValue(pairing)! == $"http://10.0.0.8:{port}/app/?token=smoke-token", "Pairing link and manual address diverged.");
                var selectedQr = qr.Source;
                var selectedAdapter = selector.SelectedItem;
                var pickerSource = selector.ItemsSource;
                refresh.Invoke(pairing, new object[] { AdapterArray(Adapter("wifi", "192.168.1.22"), Adapter("ethernet", "10.0.0.8", NetworkInterfaceType.Ethernet)) });
                Require(ReferenceEquals(selector.ItemsSource, pickerSource) && ReferenceEquals(selector.SelectedItem, selectedAdapter) &&
                    ReferenceEquals(qr.Source, selectedQr) && selector.SelectedIndex == 1, "An unchanged adapter refresh reset the picker, regenerated the QR or lost explicit selection.");
                pairing.Width = 600;
                await WaitForThumbnail(() => ((TextBlock)pairing.FindName("LocalStatus")).Text == "Local HTTP reachable");
                Require(!fixture.Services.GetRequiredService<ConnectionPresence>().Snapshot().PhoneActive && fixture.Services.GetRequiredService<HistoryIndex>().List().Count == 0,
                    "Pairing self-check created remote activity or transfer history.");
                if (screenshots is not null) Capture((FrameworkElement)pairing.Content, screenshots, "pairing-ready.png");
                refresh.Invoke(pairing, new object[] { AdapterArray() });
                Require(qr.Source is null && !((Button)pairing.FindName("CopyLinkButton")).IsEnabled &&
                    string.IsNullOrEmpty((string?)typeof(PairingWindow).GetProperty("PairingLink")!.GetValue(pairing)), "Offline setup retained a usable pairing link or QR.");
                Require(!((TextBlock)pairing.FindName("HostText")).Text.Contains(".local"), "Offline setup invented a hostname.");
                await Task.Delay(50);
                if (screenshots is not null) Capture((FrameworkElement)pairing.Content, screenshots, "pairing-offline.png");
                var main = Application.Current.MainWindow;
                Click(pairing, "LaterButton");
                Require(!pairing.IsVisible && ReferenceEquals(Application.Current.MainWindow, main) && !Application.Current.Dispatcher.HasShutdownStarted, "Set up later exited or changed the workspace.");
                Require(!((DispatcherTimer)typeof(PairingWindow).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pairing)!).IsEnabled,
                    "Pairing activity polling survived close.");
            }
            finally { pairing.Close(); workspace.Close(); await fixture.StopAsync(); }
        });
        await Check("local HTTP check is asynchronous, cancellable, and creates no presence", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var cancellation = new CancellationTokenSource();
                var pending = (Task)InvokeNative("ConnectionDiagnostics", "CheckLocalAsync", ((IPEndPoint)listener.LocalEndpoint).Port, cancellation.Token);
                Require(!pending.IsCompleted, "Local check blocked or completed before HTTP responded.");
                using var connection = await listener.AcceptTcpClientAsync();
                var requestBytes = new byte[4096];
                var count = await connection.GetStream().ReadAsync(requestBytes);
                var request = System.Text.Encoding.ASCII.GetString(requestBytes, 0, count);
                Require(request.StartsWith("GET /app/ HTTP/") && !request.Contains("token", StringComparison.OrdinalIgnoreCase), "Self-check used heartbeat/auth or sent a token.");
                cancellation.Cancel();
                try { await pending; } catch (OperationCanceledException) { }
                Require(pending.IsCompleted, "Cancelled HTTP check remained pending.");
            }
            finally { listener.Stop(); }
        });
        await Check("diagnostics redact untrusted adapter names and distinguish remote uncertainty", () =>
        {
            var adapter = Adapter("private-file.txt?token=SECRET", "192.168.1.22", name: "SECRET private-file.txt\nAuthorization: SECRET");
            var text = (string)InvokeNative("ConnectionDiagnostics", "Format", adapter, true, new ConnectionSnapshot(false, 0, null), 8787);
            Require(text.Contains("192.168.1.22") && text.Contains("8787") && text.Contains("reachable", StringComparison.OrdinalIgnoreCase), "Diagnostics omitted verified local results.");
            Require(!text.Contains("SECRET") && !text.Contains("private-file") && !text.Contains("?token") && !text.Contains("Authorization"), "Copied diagnostics exposed untrusted/private data.");
            Require(text.Contains("firewall", StringComparison.OrdinalIgnoreCase) && text.Contains("cannot", StringComparison.OrdinalIgnoreCase), "Local checks claimed to verify remote firewall policy.");
            return Task.CompletedTask;
        });
        await Check("native activity expires at 15 seconds while retaining last-seen detail", () =>
        {
            var clock = new ControlledClock();
            var presence = new ConnectionPresence(clock);
            string Field(string name) => (string)InvokeNative("ConnectionStatus", "Describe", presence.Snapshot()).GetType().GetProperty(name)!.GetValue(
                InvokeNative("ConnectionStatus", "Describe", presence.Snapshot()))!;
            Require(Field("Pill") == "No browser activity", "Idle status invented a remote connection.");
            presence.RecordHeartbeat("00112233445566778899aabbccddeeff");
            Require(Field("Pill") == "Browser active" && Field("Header").Contains("Browser active"), "Real remote activity was not displayed.");
            clock.Advance(TimeSpan.FromSeconds(15));
            Require(Field("Pill") == "Browser inactive" && Field("Detail").Contains("12:00") &&
                !Field("Header").Contains("Browser active"), "Expired presence stayed active or lost last-seen time.");
            Require(Field("Detail").Contains("receipt", StringComparison.OrdinalIgnoreCase), "Presence detail omitted its receipt limitation.");
            return Task.CompletedTask;
        });
        await Check("occupied port returns a safe actionable startup failure", async () =>
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = BeamServerHost.Build(new BeamServerOptions(Path.Combine(folder, "startup-failure"), "private-startup-token", port));
            string? message = null;
            try
            {
                var result = (Task<bool>)InvokeNative("App", "TryStartServerAsync", server, port, (Action<string>)(value => message = value));
                Require(!await result, "Occupied port was treated as a successful startup.");
                Require(message is not null && message.Contains(port.ToString()) && message.Contains("close", StringComparison.OrdinalIgnoreCase)
                    && !message.Contains("private-startup-token"), "Startup failure lacks a safe, actionable port-conflict message.");
            }
            finally { listener.Stop(); await server.DisposeAsync(); }
        });
        await Check("optional mDNS network failures permit IP setup and safe shutdown", () =>
        {
            var constructor = typeof(MdnsAdvertiser).GetConstructor(new[] { typeof(Func<Makaretu.Dns.ServiceDiscovery>) });
            Require(constructor is not null, "Optional discovery does not isolate multicast construction failures.");
            foreach (var failure in new Exception[] { new SocketException((int)SocketError.NetworkDown), new NetworkInformationException(),
                new InvalidOperationException("No multicast interface"), new NotSupportedException("Multicast unavailable") })
            {
                var advertiser = (MdnsAdvertiser)constructor!.Invoke(new object[] { (Func<Makaretu.Dns.ServiceDiscovery>)(() => throw failure) });
                Require(advertiser.GetType().GetMethod("Start")!.Invoke(advertiser, new object[] { 8787 }) is false,
                    "Optional multicast failure prevented normal startup.");
                Require(advertiser.HostName is null, "Failed multicast startup advertised a usable hostname.");
                advertiser.Stop();
            }
            return Task.CompletedTask;
        });
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void DropFile(ExpandedPanel panel, IDataObject data)
    {
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { data, DragDropKeyStates.None, DragDropEffects.Copy, panel, new Point() }, null)!;
        args.RoutedEvent = DragDrop.DropEvent;
        panel.RaiseEvent(args);
    }
    private static async Task CheckPairingRestore(TrayIconService tray, PillWindow owner)
    {
        var icon = typeof(TrayIconService).GetField("_taskbarIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
        var menu = (ContextMenu)icon.GetType().GetProperty("ContextMenu")!.GetValue(icon)!;
        var pairCommand = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Pair Phone");
        void OpenPairing() => pairCommand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PairingWindow? pairing = null;
        try
        {
            OpenPairing();
            await Task.Delay(60);
            pairing = Application.Current.Windows.OfType<PairingWindow>().Single();
            pairing.WindowState = WindowState.Minimized;
            await Task.Delay(60);
            OpenPairing();
            await Task.Delay(60);
            Require(ReferenceEquals(pairing, Application.Current.Windows.OfType<PairingWindow>().Single()) &&
                pairing.WindowState == WindowState.Normal && pairing.IsVisible,
                "Tray Pair Phone left the existing pairing window minimized or created a duplicate.");

            owner.WindowState = WindowState.Minimized;
            OpenPairing();
            await Task.Delay(60);
            Require(owner.WindowState != WindowState.Minimized && pairing.WindowState == WindowState.Normal && pairing.IsVisible,
                "Pair Phone did not restore a minimized owner and its pairing window.");
            pairing.Close();
            pairing = null;
            owner.WindowState = WindowState.Minimized;
            OpenPairing();
            await Task.Delay(60);
            pairing = Application.Current.Windows.OfType<PairingWindow>().Single();
            Require(owner.WindowState != WindowState.Minimized && pairing.WindowState == WindowState.Normal && pairing.IsVisible,
                "Opening new pairing from a minimized workspace left its QR inaccessible.");
            Console.WriteLine("PASS: tray Pair Phone restores the same minimized dialog and its owner without duplicating windows.");
        }
        finally { pairing?.Close(); owner.OpenWorkspace(); }
    }

    private static async Task CheckDesktopPresence(PillWindow window, ConnectionPresence presence, string? screenshots)
    {
        await window.PollHistoryAsync();
        Require(((TextBlock)window.FindName("PillStatus")).Text == "No browser activity", "Native idle pill claims remote activity before a heartbeat.");
        presence.RecordHeartbeat("00112233445566778899aabbccddeeff");
        await window.PollHistoryAsync();
        Require(((TextBlock)window.FindName("ServerStatus")).Text.Contains("Browser active"), "Native header ignored real remote activity.");
        window.ShowPillMode();
        Require(((TextBlock)window.FindName("PillStatus")).Text == "Browser active", "Idle pill did not display real activity.");
        await Task.Delay(50);
        CheckPillTextBounds(window);
        if (screenshots is not null) Capture((FrameworkElement)window.Content, screenshots, "beam-browser-active-pill.png");
        window.OpenWorkspace();
        await Task.Delay(TimeSpan.FromSeconds(15.1));
        await window.PollHistoryAsync();
        Require(((TextBlock)window.FindName("PillStatus")).Text == "Browser inactive" &&
            !((TextBlock)window.FindName("ServerStatus")).Text.Contains("Browser active"), "Expired browser activity stayed active in the native window.");
        Require(((FrameworkElement)window.FindName("ServerStatus")).ToolTip?.ToString()?.Contains("Last browser activity") == true, "Native header lost last-seen detail.");
        window.ShowPillMode();
        await Task.Delay(50);
        CheckPillTextBounds(window);
        if (screenshots is not null) Capture((FrameworkElement)window.Content, screenshots, "beam-browser-inactive-pill.png");
        window.OpenWorkspace();
        window.UpdateLayout();
        var status = (FrameworkElement)window.FindName("ServerStatus");
        var quit = (FrameworkElement)window.FindName("PillButton");
        var bounds = status.TransformToAncestor(window).TransformBounds(new Rect(status.RenderSize));
        var buttonBounds = quit.TransformToAncestor(window).TransformBounds(new Rect(quit.RenderSize));
        Require(bounds.Right <= buttonBounds.Left && bounds.Left >= 0, "Header status overlaps window controls at 780px.");
        if (screenshots is not null) Capture((FrameworkElement)window.Content, screenshots, "beam-browser-inactive.png");
        Console.WriteLine("PASS: actual native header/pill show remote activity, expire after 15s, retain last-seen and fit at 780px.");
    }
    private static void Click(FrameworkElement view, string name) =>
        ((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task WaitForQueue(ExpandedPanel panel)
    {
        var busy = typeof(ExpandedPanel).GetField("_isSendingFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while ((bool)busy.GetValue(panel)!) await Task.Delay(10);
        await panel.PendingTransfers;
        await panel.RefreshHistoryAsync();
    }
    private static HistoryItem ActivityItem(string id, string direction, bool read = false, bool text = false, string name = "brief.pdf", string type = "application/pdf") => new()
    {
        Id = id, Direction = direction, Read = read, IsText = text, Name = name,
        ContentType = type, TextContent = text ? "A note\nSecond line" : null, TimestampUtc = DateTimeOffset.UtcNow
    };
    private static void CheckActivityRecognition()
    {
        var tracker = new TransferActivityTracker();
        var previous = ActivityItem("old-out", "outbound");
        var seed = new[] { ActivityItem("old-in", "inbound"), previous, ActivityItem("old-read", "outbound", true) };
        tracker.Seed(seed);
        Require(tracker.Observe(seed).Count == 0, "Startup history replayed.");
        previous.Read = true;
        var acknowledgements = tracker.Observe(seed);
        Require(acknowledgements.Single().Status == "Download started on phone", "Read flags were not copied at seed time.");
        Require(tracker.Observe(seed).Count == 0, "Acknowledgement replayed.");
        var arrivals = new[] { ActivityItem("photo", "inbound", name: "photo.jpg", type: "image/jpeg"), ActivityItem("message", "inbound", text: true) };
        var batch = tracker.Observe(seed.Concat(arrivals)).Single();
        Require(batch.Kind == TransferActivityKind.Received && batch.Items.Count == 2 && batch.Status == "Received from phone", "Inbound photo/note batch was split or mislabelled.");
        var queued = new[] { ActivityItem("file", "outbound"), ActivityItem("copy", "outbound", text: true) };
        Require(tracker.RecordQueued(queued).Single().Items.Count == 2, "Queue batch was split.");
        Require(tracker.Observe(seed.Concat(arrivals).Concat(queued)).Count == 0, "Queue event replayed during polling.");
        queued[0].Read = queued[1].Read = true;
        var read = tracker.Observe(seed.Concat(arrivals).Concat(queued));
        Require(read.Count == 2 && read.Any(a => a.Status == "Copy acknowledged") && read.Any(a => a.Status == "Download started on phone"), "File and copy acknowledgements were conflated.");
        Require(tracker.RecordQueued(queued).Count == 0, "Late queue notification overwrote acknowledgement.");
        Require(tracker.Observe(seed.Concat(arrivals).Concat(queued)).Count == 0, "Unchanged poll replayed a batch.");
        Require(tracker.Observe(new[] { ActivityItem("fast-read", "outbound", true) }).Single().Kind == TransferActivityKind.Acknowledged, "An already-read new ID was labelled queued.");
        Require(tracker.Observe(new[] { ActivityItem("external-queue", "outbound") }).Single().Status == "Queued for phone", "Polling missed an external queue.");
        var deferred = ActivityItem("batch-still-copying", "outbound");
        Require(tracker.Observe(new[] { deferred }, deferQueued: true).Count == 0, "Polling split an active local batch.");
        Require(tracker.RecordQueued(new[] { deferred }).Single().Items.Count == 1, "Deferred queue was lost.");
        Require(TransferSymbol.For(ActivityItem("p", "inbound", name: "PHOTO.JPG", type: "application/octet-stream")) == TransferSymbol.Photo, "Photo extension fallback failed.");
        Require(TransferSymbol.For(ActivityItem("m", "inbound", text: true)) == TransferSymbol.Message, "Note symbol failed.");
        Require(TransferSymbol.For(ActivityItem("v", "inbound", name: "clip.mp4")) == TransferSymbol.Video, "Video symbol failed.");
        Require(TransferSymbol.For(ActivityItem("a", "inbound", name: "track.flac")) == TransferSymbol.Audio, "Audio symbol failed.");
        Require(TransferSymbol.For(ActivityItem("z", "inbound", name: "bundle.zip")) == TransferSymbol.Archive, "Archive symbol failed.");
        Require(TransferSymbol.For(ActivityItem("d", "inbound")) == TransferSymbol.Document, "Document symbol failed.");
        foreach (var width in new[] { 0d, 8, 130, 360, 680 })
        foreach (var phase in new[] { 0d, .01, .11, .5, 1 })
        foreach (var outgoing in new[] { false, true })
        {
            var frame = TransferSignalGeometry.At(width, 28, phase, outgoing, true);
            Require(frame.PacketLeft >= 0 && frame.PacketLeft + frame.PacketWidth <= width + .001, "Symbol escaped rail.");
            Require(frame.TrailLeft >= 0 && frame.TrailLeft + frame.TrailWidth <= width + .001, "Trail escaped rail.");
            Require(frame.TrailWidth <= 32, "Trail exceeded design length.");
            if (phase == 0) Require(frame.PacketLeft == (outgoing ? 0 : width - frame.PacketWidth), "Rail direction was reversed.");
            var reduced = TransferSignalGeometry.At(width, 28, phase, outgoing, false);
            Require(reduced.TrailWidth == 0 && reduced == TransferSignalGeometry.At(width, 28, .5, outgoing, false), "Reduced motion moved or retained a trail.");
        }
        var reducedView = new TransferMotionView(() => false) { Width = 130, Height = 16 };
        reducedView.ShowActivity(batch);
        Require(!reducedView.IsAnimating, "Reduced motion started a timer.");
        reducedView.Stop();
        Console.WriteLine("PASS: activity seeding, receipt/queue/acknowledgement batches, deduplication, symbols, signal bounds and reduced motion.");
    }
    private static void AdvanceToActivity(PillWindow window, TransferActivityKind kind, string? status = null)
    {
        var rail = (TransferMotionView)window.FindName("PillRail");
        bool Matches() => rail.Activity?.Kind == kind && (status is null || rail.Activity.Status == status);
        for (var attempt = 0; !Matches() && attempt < 8; attempt++)
            typeof(PillWindow).GetMethod("ShowNextActivity", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(Matches(), $"No actual {kind} / {status} event reached the pill.");
        Require(((TransferMotionView)window.Panel.FindName("ActivityRail")).Activity?.Kind == kind, "Desktop and pill activity diverged.");
    }
    private static void CheckPillTextBounds(PillWindow window)
    {
        window.UpdateLayout();
        Require(window.ActualWidth == 164 && window.ActualHeight == 48, "Rendered pill dimensions changed.");
        var bounds = new Dictionary<string, Rect>();
        foreach (var name in new[] { "PillTitle", "PillStatus", "PillKind", "PillQuitButton", "PillRail" })
        {
            var element = (FrameworkElement)window.FindName(name);
            var rect = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            Require(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= 164 && rect.Bottom <= 48, $"{name} escaped the pill.");
            bounds.Add(name, rect);
        }
        foreach (var first in bounds)
        foreach (var second in bounds.Where(b => string.CompareOrdinal(b.Key, first.Key) > 0))
        {
            var overlap = Rect.Intersect(first.Value, second.Value);
            Require(overlap.IsEmpty || overlap.Width < .1 || overlap.Height < .1, $"Pill occlusion: {first.Key} {first.Value} / {second.Key} {second.Value}; overlap {overlap}.");
        }
        var title = (TextBlock)window.FindName("PillTitle");
        var fullTitle = new FormattedText(title.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch), title.FontSize, title.Foreground, VisualTreeHelper.GetDpi(title).PixelsPerDip);
        Require(fullTitle.Width <= title.ActualWidth, "Pill activity title was truncated.");
        Require(!title.Text.Contains('%') && !((TextBlock)window.FindName("PillStatus")).Text.Contains('%'), "Pill invented transport percentages.");
        Console.WriteLine($"PASS: pill 164x48, no text/icon/button/rail overlap ({title.Text}).");
    }
    private static async Task CheckReducedMotionTimer()
    {
        var enabled = true;
        var rail = new TransferMotionView(() => enabled) { Height = 28, Width = 130 };
        var host = new Window { Content = rail, Width = 164, Height = 48, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(20, 22, 24)) };
        try
        {
            host.Show();
            var activity = new TransferActivity(TransferActivityKind.Received, new[] { ActivityItem("motion", "inbound") });
            rail.ShowActivity(activity);
            Require(rail.IsVisible && rail.IsAnimating, "Visible motion did not start.");
            enabled = false;
            await Task.Delay(100);
            Require(!rail.IsAnimating, "Disabling motion left the timer running.");
            rail.ShowActivity(activity);
            Require(!rail.IsAnimating, "Reduced motion started a visible animation.");
            enabled = true;
            rail.ShowActivity(activity);
            rail.Stop();
            Require(!rail.IsAnimating, "Stopping the motion surface left a timer running.");
            rail.ShowActivity(activity);
            Require(!rail.IsAnimating, "A stopped surface restarted.");
        }
        finally { rail.Stop(); host.Close(); }
    }
    private static async Task CheckSelectionDuringRefresh(ExpandedPanel panel, HistoryIndex index)
    {
        await panel.RefreshHistoryAsync();
        var list = (ListBox)panel.FindName("HistoryList");
        list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "preview");
        var indexLock = typeof(HistoryIndex).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index)!;
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = Task.Run(() =>
        {
            lock (indexLock)
            {
                index.Add(ActivityItem("selection-refresh", "inbound", text: true));
                holding.Set();
                release.Wait();
            }
        });
        await Task.Run(() => holding.Wait());
        Task refresh;
        try
        {
            refresh = panel.RefreshHistoryAsync();
            Require(!refresh.IsCompleted, "Slow-refresh regression did not hold the snapshot read.");
            list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "note");
        }
        finally { release.Set(); }
        await blocked;
        await refresh;
        Require(((HistoryEntryViewModel?)list.SelectedItem)?.Id == "note", "Refresh restored an older selection over the user's latest choice.");
        Require(((TextBlock)panel.FindName("PreviewName")).Text == "Studio notes", "Refresh changed the current Open/Resend target.");
        Console.WriteLine("PASS: a selection changed during a blocked refresh survives Entries.Clear and remains the action target.");
    }
    private static async Task CheckLazyThumbnailHistory(string folder)
    {
        var dispatcher = Application.Current.Dispatcher;
        var decodes = 0;
        using var cache = new HistoryThumbnailCache(_ =>
        {
            Require(!dispatcher.CheckAccess(), "Thumbnail decoder ran on the dispatcher.");
            Interlocked.Increment(ref decodes);
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            image.Freeze();
            return image;
        });
        var index = await Task.Run(() =>
        {
            var history = new HistoryIndex(Path.Combine(folder, "thumbnail-regression", "history.json"));
            Directory.CreateDirectory(Path.Combine(folder, "thumbnail-regression"));
            for (var i = 0; i < HistoryThumbnailCache.Capacity * 3; i++)
            {
                var path = Path.Combine(folder, "thumbnail-regression", $"photo-{i}.png");
                File.WriteAllBytes(path, Array.Empty<byte>());
                history.Add(new HistoryItem { Id = $"lazy-{i}", Direction = "inbound", Name = $"photo-{i}.png",
                    ContentType = "image/png", FilePath = path, TimestampUtc = DateTimeOffset.UtcNow });
            }
            return history;
        });
        var model = new HistoryViewModel(index, cache);
        await model.RefreshAsync();
        Require(model.Entries.Count == HistoryThumbnailCache.Capacity * 3 && decodes == 0, "Refreshing retained photo history eagerly decoded images.");
        var requested = model.Entries[0];
        await requested.EnsureThumbnailAsync();
        Require(decodes == 1 && requested.Thumbnail is BitmapSource { IsFrozen: true }, "A requested thumbnail was not loaded lazily off the dispatcher.");
        await model.RefreshAsync();
        Require(decodes == 1, "Refreshing after a lazy request decoded unrelated history.");
        foreach (var entry in model.Entries) await entry.EnsureThumbnailAsync();
        Require(cache.CachedCount <= HistoryThumbnailCache.Capacity, "Scrolling history retained an unbounded strong bitmap cache.");
        cache.Dispose();
        Require(await cache.GetAsync("after-close.png") is null && cache.CachedCount == 0, "Stopped thumbnail cache restarted or retained bitmaps.");
        Console.WriteLine($"PASS: {model.Entries.Count}-photo refresh decodes zero images; requested previews load off UI; strong cache stays at {HistoryThumbnailCache.Capacity} entries and clears on stop.");
    }
    private static async Task WaitForThumbnail(Func<bool> loaded)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!loaded()) await Task.Delay(10, timeout.Token);
    }
    private static void Capture(FrameworkElement view, string folder, string name)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Require(pixels.Where((_, i) => i % 4 != 3).Distinct().Count() > 20, "Rendered UI is blank.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name));
        encoder.Save(file);
    }
    private static byte[] MakePreview()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(226, 236, 238)), null, new Rect(0, 0, 640, 400));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(29, 44, 46)), null, new Rect(60, 55, 520, 290));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 221, 208)), null, new Rect(150, 95, 45, 210));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 221, 208)), null, new Rect(275, 130, 45, 140));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 206, 106)), null, new Rect(400, 165, 45, 70));
        }
        var bitmap = new RenderTargetBitmap(640, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var application = new SmokeApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var screenshotFolder = args.Length == 2 && args[0] == "--screenshots" ? args[1] : null;
        var connectionOnly = args.Contains("--connection-only");
        Uri? serverAddress = null;
        TrayIconService? tray = null;
        PillWindow? testedWindow = null;
        var expectedItemCountAfterQuit = 0;
        using var timeout = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL: desktop smoke test timed out.");
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(75), Timeout.InfiniteTimeSpan);

        application.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                await CheckConnectionSetup(directory, screenshotFolder);
                if (connectionOnly) { application.Shutdown(); return; }
                CheckActivityRecognition();
                await CheckReducedMotionTimer();
                Directory.CreateDirectory(directory);
                await CheckLazyThumbnailHistory(directory);
                var source = Path.Combine(directory, "screenshot.png");
                var bytes = new byte[1024 * 1024];
                new Random(42).NextBytes(bytes);
                await File.WriteAllBytesAsync(source, bytes);
                var options = new BeamServerOptions(directory, "smoke-token", 0);
                var server = BeamServerHost.Build(options);
                await server.StartAsync();
                serverAddress = new Uri(server.Urls.First().Replace("0.0.0.0", "127.0.0.1"));
                typeof(App).GetProperty(nameof(App.ServerApp))!.SetValue(null, server);
                typeof(App).GetProperty(nameof(App.Options))!.SetValue(null, options);
                tray = new TrayIconService();
                typeof(App).GetField("_trayIconService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(application, tray);
                tray.Initialize();
                var window = (PillWindow)application.MainWindow;
                testedWindow = window;
                var panel = window.Panel;
                window.Width = 780;
                window.Height = 600;
                await CheckPairingRestore(tray, window);
                var polling = (DispatcherTimer)typeof(PillWindow).GetField("_pollTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Require(polling.IsEnabled, "History polling did not start.");
                polling.Stop(); // Drive snapshots explicitly so batch assertions do not race the two-second poll.
                await window.PollHistoryAsync();
                await CheckDesktopPresence(window, server.Services.GetRequiredService<ConnectionPresence>(), screenshotFolder);
                var observed = new List<TransferActivity>();
                window.ActivityObserved += (_, activity) => observed.Add(activity);
                DropFile(panel, new DataObject(DataFormats.FileDrop, new[] { source }));
                var heartbeat = false;
                await application.Dispatcher.InvokeAsync(() => heartbeat = true);
                await WaitForQueue(panel);

                var index = server.Services.GetRequiredService<HistoryIndex>();
                var item = index.ListUnread("outbound").Single();
                Require(heartbeat && (await File.ReadAllBytesAsync(item.FilePath!)).SequenceEqual(bytes), "Drop bytes or dispatcher responsiveness failed.");
                var status = (TextBlock)panel.FindName("TransferStatus");
                Require(status.Text.Contains("queued"), "Queue status failed.");
                DropFile(panel, new DataObject(DataFormats.FileDrop, new[] { Path.Combine(directory, "missing.png") }));
                await WaitForQueue(panel);
                Require(status.Text.Contains("missing.png") && index.ListUnread("outbound").Count == 1, "Failed drop did not report an error.");

                var model = new HistoryViewModel(index) { SearchQuery = "no-match" };
                model.Refresh();
                Require(model.Entries.Count == 0, "Vault search failed.");
                model.SearchQuery = "screenshot";
                model.Refresh();
                Require(model.Entries.Count == 1, "Filename search failed.");
                var store = server.Services.GetRequiredService<ItemStore>();
                var previewPath = await store.SaveFileAsync("inbound", "Beam study.png", new MemoryStream(MakePreview()));
                index.Add(new HistoryItem { Id = "preview", Direction = "inbound", Name = "Beam study.png", IsText = false,
                    ContentType = "image/png", SizeBytes = new FileInfo(previewPath).Length, FilePath = previewPath, TimestampUtc = DateTimeOffset.UtcNow });
                index.Add(new HistoryItem { Id = "note", Direction = "inbound", Name = "Text", IsText = true,
                    ContentType = "text/plain", TextContent = "Studio notes\nLight, movement, and a quieter workspace.", TimestampUtc = DateTimeOffset.UtcNow });
                model.SearchQuery = "quieter";
                model.Refresh();
                Require(model.Entries.Single().Id == "note", "Note body search failed.");
                model.SearchQuery = "";
                model.DirectionFilter = "Received";
                model.KindFilter = "Images";
                model.Refresh();
                await model.Entries.Single().EnsureThumbnailAsync();
                Require(model.Entries.Single().Id == "preview" && model.Entries[0].Thumbnail is not null, "Direction/type filter or preview failed.");
                model.DirectionFilter = "All";
                model.KindFilter = "All types";
                model.OldestFirst = true;
                model.Refresh();
                Require(model.Entries[0].Id == item.Id, "Oldest-first sort failed.");
                index.MarkRead(item.Id);
                model.Refresh();
                Require(model.Entries.First(e => e.Id == item.Id).State == "Download started on phone", "Download acknowledgement status failed.");

                await window.PollHistoryAsync();
                Require(observed.Any(a => a.Kind == TransferActivityKind.Queued && a.Status == "Queued for phone"), "Native queue activity was not observed.");
                Require(observed.Any(a => a.Kind == TransferActivityKind.Received && a.Items.Count == 2), "Native inbound batch was not observed.");
                Require(observed.Any(a => a.Kind == TransferActivityKind.Acknowledged), "Native acknowledgement was not observed.");
                var observedCount = observed.Count;
                await window.PollHistoryAsync();
                Require(observed.Count == observedCount, "Native poll replayed activity.");
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-queued.png");
                AdvanceToActivity(window, TransferActivityKind.Received);
                Require(((TextBlock)panel.FindName("ActivityStatus")).Text == "Received from phone", "Receipt label was overwritten.");
                var photoRail = (TransferMotionView)panel.FindName("ActivityRail");
                await WaitForThumbnail(() => photoRail.Thumbnail is not null);
                Require(photoRail.Thumbnail is BitmapSource { IsFrozen: true, PixelWidth: <= 600 }, "Travelling photo did not reuse a frozen, downsampled preview.");
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-received.png");
                window.ShowPillMode();
                await Task.Delay(100);
                CheckPillTextBounds(window);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-pill-received.png");
                AdvanceToActivity(window, TransferActivityKind.Acknowledged);
                var activeTitle = ((TextBlock)window.FindName("PillTitle")).Text;
                server.Services.GetRequiredService<ConnectionPresence>().RecordHeartbeat("ffeeddccbbaa99887766554433221100");
                await window.PollHistoryAsync();
                Require(((TextBlock)window.FindName("PillTitle")).Text == activeTitle && activeTitle == "Download started", "Polling overwrote an active acknowledgement.");
                CheckPillTextBounds(window);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-pill-acknowledged.png");
                window.OpenWorkspace();

                await panel.RefreshHistoryAsync();
                var list = (ListBox)panel.FindName("HistoryList");
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "preview");
                await WaitForThumbnail(() => ((Image)panel.FindName("PreviewImage")).Source is not null);
                status.Text = "Ready";
                await Task.Delay(150);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-transfer.png");
                panel.ShowVault();
                await Task.Delay(100);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-vault.png");
                var search = (TextBox)panel.FindName("SearchInput");
                search.Text = "quieter";
                await panel.RefreshHistoryAsync();
                Require(list.Items.Count == 1 && ((HistoryEntryViewModel)list.Items[0]).Id == "note", "Search UI failed.");
                search.Text = "";
                await panel.RefreshHistoryAsync();
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "note");
                Click(panel, "ResendButton");
                await WaitForQueue(panel);
                Require(index.List().Any(i => i.Direction == "outbound" && i.TextContent?.StartsWith("Studio notes") == true), "Note resend failed.");
                var copiedNote = index.List().Single(i => i.Direction == "outbound" && i.TextContent?.StartsWith("Studio notes") == true);
                await Task.Run(() => index.MarkRead(copiedNote.Id));
                await window.PollHistoryAsync();
                Require(observed.Any(a => a.Status == "Copy acknowledged" && a.Items.Any(i => i.Id == copiedNote.Id)), "Native copy acknowledgement was not observed.");
                AdvanceToActivity(window, TransferActivityKind.Acknowledged, "Copy acknowledged");
                Require(((TransferMotionView)panel.FindName("ActivityRail")).Thumbnail is null, "A note retained the preceding photo thumbnail.");
                window.ShowPillMode();
                await Task.Delay(100);
                CheckPillTextBounds(window);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-pill-copy.png");
                window.OpenWorkspace();
                await panel.RefreshHistoryAsync();
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "preview");
                Click(panel, "ResendButton");
                await WaitForQueue(panel);
                Require(index.List().Any(i => i.Direction == "outbound" && i.Name == "Beam study.png"), "File resend lost original name.");
                var beforeBatch = observed.Count;
                DropFile(panel, new DataObject(DataFormats.FileDrop, new[] { source, source }));
                await WaitForQueue(panel);
                await window.PollHistoryAsync();
                Require(observed.Skip(beforeBatch).Where(a => a.Kind == TransferActivityKind.Queued).Single().Items.Count == 2, "Native multi-file queue was not one batch.");
                await CheckSelectionDuringRefresh(panel, index);
                window.Width = 780;
                window.Height = 600;
                await Task.Delay(100);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-vault-small.png");
                ((RadioButton)panel.FindName("TransferTab")).IsChecked = true;
                await panel.RefreshHistoryAsync();
                window.UpdateLayout();
                var strip = (FrameworkElement)panel.FindName("ActivityStrip");
                var historyList = (FrameworkElement)panel.FindName("HistoryList");
                var stripBounds = strip.TransformToAncestor(window).TransformBounds(new Rect(strip.RenderSize));
                var historyBounds = historyList.TransformToAncestor(window).TransformBounds(new Rect(historyList.RenderSize));
                Require(stripBounds.Left >= 0 && stripBounds.Right <= 780 && stripBounds.Bottom <= historyBounds.Top && historyBounds.Height >= 50, "Activity strip clipped or displaced history at 780x600.");
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-motion-small.png");

                Click(window, "PillButton");
                Require(window.IsPillMode && window.Width == 164 && window.Height == 48 && !window.ShowInTaskbar, "Pill geometry failed.");
                await Task.Delay(150);
                CheckPillTextBounds(window);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-pill.png");
                window.OpenWorkspace();
                Require(!window.IsPillMode && window.Width == 780 && window.Height == 600 && window.ShowInTaskbar, "Workspace restore failed.");
                Click(window, "MinimizeButton");
                Require(window.WindowState == WindowState.Minimized && window.ShowInTaskbar, "Taskbar minimize failed.");
                window.OpenWorkspace();
                Click(window, "HideButton");
                Require(!window.IsVisible, "Hide to tray failed.");
                window.OpenWorkspace();
                Require(window.IsVisible, "Reopen failed.");
                File.Delete(previewPath);
                await panel.RefreshHistoryAsync();
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "preview");
                Require(!((Button)panel.FindName("OpenItemButton")).IsEnabled && !((Button)panel.FindName("ResendButton")).IsEnabled, "Missing file actions were not disabled.");
                Console.WriteLine("PASS: drop, search/filter/sort, thumbnails, truthful acknowledgement, resend, missing-file actions, motion layout, pill, minimize, hide/reopen.");
                expectedItemCountAfterQuit = index.List().Count + 1;
                polling.Start();
                using var holding = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var indexLock = typeof(HistoryIndex).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index)!;
                var delayedSave = Task.Run(() => { lock (indexLock) { holding.Set(); release.Wait(); } });
                await Task.Run(() => holding.Wait());
                try
                {
                    DropFile(panel, new DataObject(DataFormats.FileDrop, new[] { source }));
                    Click(window, "QuitButton");
                    Require(window.IsVisible && !panel.PendingTransfers.IsCompleted, "Quit interrupted an active copy.");
                    var icon = typeof(TrayIconService).GetField("_taskbarIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
                    var menu = (ContextMenu)icon.GetType().GetProperty("ContextMenu")!.GetValue(icon)!;
                    ((MenuItem)menu.Items[menu.Items.Count - 1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Require(window.IsVisible, "Tray Quit interrupted an active copy.");
                }
                finally { release.Set(); }
                await delayedSave;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                application.Shutdown(1);
            }
        });
        var result = application.Run();
        if (result == 0 && serverAddress is not null)
        {
            Require(testedWindow is not null && !((TransferMotionView)testedWindow.FindName("PillRail")).IsAnimating
                && !((TransferMotionView)testedWindow.Panel.FindName("ActivityRail")).IsAnimating, "Motion timers survived close.");
            foreach (var field in new[] { "_pollTimer", "_activityTimer" })
                Require(!((DispatcherTimer)typeof(PillWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(testedWindow)!).IsEnabled, $"{field} survived close.");
            Require(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == serverAddress.Port), "HTTP server still listens after Quit.");
            Require(tray is not null && typeof(TrayIconService).GetField("_taskbarIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray) is null, "Tray icon was not disposed.");
            Require(new HistoryIndex(Path.Combine(directory, "history.json")).List().Count == expectedItemCountAfterQuit, "Quit lost transfer records.");
            Require(!Directory.GetFiles(directory, "*.tmp").Any(), "History save left temporary files.");
            Console.WriteLine("PASS: close shuts down the application, tray icon and HTTP server.");
        }
        return result;
    }
}
