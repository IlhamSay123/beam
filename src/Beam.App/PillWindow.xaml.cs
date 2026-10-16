using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Beam.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Beam.App;

public partial class PillWindow : Window
{
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> _seenInboundIds = new();
    private readonly HistoryIndex _index;
    private readonly ExpandedPanel _panel;
    private Rect _workspaceBounds;
    private Point _pillPosition;
    private Point? _dragStart;
    private bool _dragged;
    private DateTimeOffset _lastActivity;
    private bool _closingAfterQueue;
    public bool IsPillMode { get; private set; }
    public ExpandedPanel Panel => _panel;

    public PillWindow()
    {
        InitializeComponent();
        _index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        _panel = new ExpandedPanel();
        _panel.TransferQueued += (_, _) => Pulse("Queued");
        ContentHost.Children.Add(_panel);
        var area = SystemParameters.WorkArea;
        Width = Math.Min(980, area.Width);
        Height = Math.Min(700, area.Height);
        MinWidth = Math.Min(780, area.Width);
        MinHeight = Math.Min(600, area.Height);
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        _workspaceBounds = new Rect(Left, Top, Width, Height);
        _pillPosition = new Point(area.Right - 188, area.Bottom - 76);
        foreach (var item in _index.List().Where(i => i.Direction == "inbound")) _seenInboundIds.Add(item.Id);
        _pollTimer.Tick += Poll;
        _pollTimer.Start();
        Closing += async (_, e) =>
        {
            if (_panel.PendingTransfers.IsCompleted) return;
            e.Cancel = true;
            if (_closingAfterQueue) return;
            _closingAfterQueue = true;
            _panel.IsEnabled = false;
            PillStatus.Text = "Finishing transfer";
            ServerStatus.Text = "Finishing queued files...";
            try { await _panel.PendingTransfers; }
            finally { Close(); }
        };
        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            if (Application.Current is App) Application.Current.Shutdown();
        };
    }
    private void Poll(object? sender, EventArgs e)
    {
        var items = _index.List();
        var received = items.Where(i => i.Direction == "inbound" && !_seenInboundIds.Contains(i.Id)).ToList();
        foreach (var item in received) _seenInboundIds.Add(item.Id);
        if (received.Count > 0) NotifyInboundReceived(received);
        _panel.RefreshHistory();
        var queued = items.Count(i => i.Direction == "outbound" && !i.Read);
        if (DateTimeOffset.UtcNow - _lastActivity > TimeSpan.FromSeconds(5))
            PillStatus.Text = queued > 0 ? $"{queued} queued" : "Ready";
        ServerStatus.Text = queued > 0 ? $"LAN ready / {queued} queued" : "LAN ready";
    }
    public void NotifyInboundReceived(IReadOnlyList<HistoryItem> items)
    {
        Pulse(items.Count > 1 ? $"{items.Count} received" : "Received");
        try
        {
            new ToastContentBuilder().AddText("Beam")
                .AddText(items.Count > 1 ? $"{items.Count} items received" : items[0].IsText ? "New note received" : "New file received").Show();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Windows notifications are optional; receiving and refreshing must still work.
        }
        _panel.RefreshHistory();
    }
    public void Pulse(string status)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        PillStatus.Text = status;
        SignalLine.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1100)));
        SignalTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-110, Width, TimeSpan.FromMilliseconds(950)));
        RootBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromRgb(120, 221, 208), Color.FromRgb(60, 72, 75), TimeSpan.FromMilliseconds(1200)));
    }
    public void OpenWorkspace(bool vault = false)
    {
        if (IsPillMode)
        {
            _pillPosition = new Point(Left, Top);
            IsPillMode = false;
            MinWidth = Math.Min(780, SystemParameters.WorkArea.Width);
            MinHeight = Math.Min(600, SystemParameters.WorkArea.Height);
            ResizeMode = ResizeMode.CanResize;
            Width = _workspaceBounds.Width;
            Height = _workspaceBounds.Height;
            Left = _workspaceBounds.Left;
            Top = _workspaceBounds.Top;
        }
        Topmost = false;
        ShowInTaskbar = true;
        RootBorder.CornerRadius = new CornerRadius(8);
        PillRoot.Visibility = Visibility.Collapsed;
        WorkspaceRoot.Visibility = Visibility.Visible;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ClampToWorkArea();
        Show();
        Activate();
        if (vault) _panel.ShowVault();
        _panel.RefreshHistory();
    }
    public void ShowPillMode()
    {
        if (!IsPillMode)
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _workspaceBounds = bounds;
            WindowState = WindowState.Normal;
            IsPillMode = true;
            MinWidth = 0;
            MinHeight = 0;
            ResizeMode = ResizeMode.NoResize;
            Width = 164;
            Height = 48;
            Left = _pillPosition.X;
            Top = _pillPosition.Y;
        }
        WorkspaceRoot.Visibility = Visibility.Collapsed;
        PillRoot.Visibility = Visibility.Visible;
        RootBorder.CornerRadius = new CornerRadius(24);
        ShowInTaskbar = false;
        Topmost = true;
        ClampToWorkArea();
        Show();
    }
    public void ToggleExpanded() { if (IsPillMode) OpenWorkspace(); else ShowPillMode(); }
    private void ClampToWorkArea()
    {
        var area = GetCurrentWorkArea();
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
    private Rect GetCurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    private static bool IsButtonSource(object source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
    private void DragSurface_Down(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonSource(e.OriginalSource)) return;
        if (!IsPillMode && e.ClickCount == 2) { ToggleMaximized(); return; }
        _dragStart = e.GetPosition(this);
        _dragged = false;
        ((UIElement)sender).CaptureMouse();
    }
    private void DragSurface_Move(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        _dragged = true;
        _dragStart = null;
        ((UIElement)sender).ReleaseMouseCapture();
        try { DragMove(); } catch (InvalidOperationException) { }
        if (IsPillMode) { ClampToWorkArea(); _pillPosition = new Point(Left, Top); }
    }
    private void DragSurface_Up(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }
    private void PillSurface_Up(object sender, MouseButtonEventArgs e)
    {
        var open = _dragStart is not null && !_dragged && !IsButtonSource(e.OriginalSource);
        DragSurface_Up(sender, e);
        if (open) OpenWorkspace();
    }
    private async void Pill_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = ((string[])e.Data.GetData(DataFormats.FileDrop)!).ToArray();
        e.Handled = true;
        e.Effects = DragDropEffects.Copy;
        await _panel.QueueFilesAsync(paths);
    }
    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Pill_Click(object sender, RoutedEventArgs e) => ShowPillMode();
    private void HideTray_Click(object sender, RoutedEventArgs e) => Hide();
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (IsPillMode) OpenWorkspace();
        WindowState = WindowState.Minimized;
    }
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void Quit_Click(object sender, RoutedEventArgs e) => Close();
    private void OpenWorkspace_Click(object sender, RoutedEventArgs e) => OpenWorkspace();
    private void OpenVault_Click(object sender, RoutedEventArgs e) => OpenWorkspace(true);
    private void Pair_Click(object sender, RoutedEventArgs e) => App.ShowPairing();
}
