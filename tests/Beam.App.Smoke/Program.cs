using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beam.App;
using Beam.Server;
using Beam.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

internal sealed class SmokeApplication : App
{
    protected override void OnStartup(StartupEventArgs e) { }
}

internal static class Program
{
    private static void DropFile(ExpandedPanel panel, IDataObject data)
    {
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { data, DragDropKeyStates.None, DragDropEffects.Copy, panel, new Point() }, null)!;
        args.RoutedEvent = DragDrop.DropEvent;
        panel.RaiseEvent(args);
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
        Uri? serverAddress = null;
        TrayIconService? tray = null;
        var expectedItemCountAfterQuit = 0;
        using var timeout = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL: desktop smoke test timed out.");
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);

        application.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
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
                var panel = window.Panel;
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
                Require(model.Entries.Single().Id == "preview" && model.Entries[0].Thumbnail is not null, "Direction/type filter or preview failed.");
                model.DirectionFilter = "All";
                model.KindFilter = "All types";
                model.OldestFirst = true;
                model.Refresh();
                Require(model.Entries[0].Id == item.Id, "Oldest-first sort failed.");
                index.MarkRead(item.Id);
                model.Refresh();
                Require(model.Entries.First(e => e.Id == item.Id).State == "Delivered", "Delivery status failed.");

                panel.RefreshHistory();
                var list = (ListBox)panel.FindName("HistoryList");
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "preview");
                status.Text = "Ready";
                await Task.Delay(150);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-transfer.png");
                panel.ShowVault();
                await Task.Delay(100);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-vault.png");
                var search = (TextBox)panel.FindName("SearchInput");
                search.Text = "quieter";
                Require(list.Items.Count == 1 && ((HistoryEntryViewModel)list.Items[0]).Id == "note", "Search UI failed.");
                search.Text = "";
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "note");
                Click(panel, "ResendButton");
                Require(index.List().Any(i => i.Direction == "outbound" && i.TextContent?.StartsWith("Studio notes") == true), "Note resend failed.");
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "preview");
                Click(panel, "ResendButton");
                await WaitForQueue(panel);
                Require(index.List().Any(i => i.Direction == "outbound" && i.Name == "Beam study.png"), "File resend lost original name.");
                window.Width = 780;
                window.Height = 600;
                await Task.Delay(100);
                if (screenshotFolder is not null) Capture((FrameworkElement)window.Content, screenshotFolder, "beam-vault-small.png");

                Click(window, "PillButton");
                Require(window.IsPillMode && window.Width == 164 && window.Height == 48 && !window.ShowInTaskbar, "Pill geometry failed.");
                window.Pulse("6 received");
                await Task.Delay(150);
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
                panel.RefreshHistory();
                list.SelectedItem = list.Items.Cast<HistoryEntryViewModel>().Single(e => e.Id == "preview");
                Require(!((Button)panel.FindName("OpenItemButton")).IsEnabled && !((Button)panel.FindName("ResendButton")).IsEnabled, "Missing file actions were not disabled.");
                Console.WriteLine("PASS: drop, search/filter/sort, thumbnails, delivery status, resend, missing-file actions, resize, pill, minimize, hide/reopen.");
                expectedItemCountAfterQuit = index.List().Count + 1;
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
            Require(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == serverAddress.Port), "HTTP server still listens after Quit.");
            Require(tray is not null && typeof(TrayIconService).GetField("_taskbarIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray) is null, "Tray icon was not disposed.");
            Require(new HistoryIndex(Path.Combine(directory, "history.json")).List().Count == expectedItemCountAfterQuit, "Quit lost transfer records.");
            Require(!Directory.GetFiles(directory, "*.tmp").Any(), "History save left temporary files.");
            Console.WriteLine("PASS: close shuts down the application, tray icon and HTTP server.");
        }
        return result;
    }
}
