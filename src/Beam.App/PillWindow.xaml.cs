using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Beam.Server.Storage;
using Beam.Server.Connection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Beam.App;

public partial class PillWindow : Window
{
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private readonly TransferActivityTracker _activities = new();
    private readonly List<TransferActivity> _waitingActivities = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _seedHistory;
    private Task _pollTask = Task.CompletedTask;
    private TransferActivity? _currentActivity;
    private int _queuedCount;
    private bool _closed;
    private readonly HistoryIndex _index;
    private readonly ConnectionPresence _presence;
    private readonly ExpandedPanel _panel;
    private Rect _workspaceBounds;
    private Point _pillPosition;
    private Point? _dragStart;
    private bool _dragged;
    private bool _closingAfterQueue;
    public bool IsPillMode { get; private set; }
    public ExpandedPanel Panel => _panel;
    public event EventHandler<TransferActivity>? ActivityObserved;

    public PillWindow()
    {
        InitializeComponent();
        _index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        _presence = App.ServerApp.Services.GetRequiredService<ConnectionPresence>();
        _seedHistory = SeedHistoryAsync();
        _panel = new ExpandedPanel();
        _panel.TransferQueued += TransferQueued;
        ContentHost.Children.Add(_panel);
        RefreshConnectionStatus();
        var area = SystemParameters.WorkArea;
        Width = Math.Min(980, area.Width);
        Height = Math.Min(700, area.Height);
        MinWidth = Math.Min(780, area.Width);
        MinHeight = Math.Min(600, area.Height);
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        _workspaceBounds = new Rect(Left, Top, Width, Height);
        _pillPosition = new Point(area.Right - 188, area.Bottom - 76);
        _activityTimer.Tick += (_, _) => ShowNextActivity();
        _pollTimer.Tick += async (_, _) => await PollHistoryAsync();
        _pollTimer.Start();
        Closing += async (_, e) =>
        {
            if (_panel.PendingTransfers.IsCompleted) return;
            e.Cancel = true;
            if (_closingAfterQueue) return;
            _closingAfterQueue = true;
            _activityTimer.Stop();
            _panel.IsEnabled = false;
            PillTitle.Text = "Beam";
            PillStatus.Text = "Finishing transfer";
            ServerStatus.Text = "Finishing queued files...";
            try { await _panel.PendingTransfers; }
            finally { Close(); }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _pollTimer.Stop();
            _activityTimer.Stop();
            _waitingActivities.Clear();
            _panel.TransferQueued -= TransferQueued;
            _panel.StopActivity();
            PillRail.Stop();
            if (Application.Current is App) Application.Current.Shutdown();
        };
    }
    private async Task SeedHistoryAsync()
    {
        var items = await Task.Run(() => _index.List().Select(TransferActivity.Snapshot).ToArray());
        if (!_closed) _activities.Seed(items);
    }
    public Task PollHistoryAsync()
    {
        if (_closed || _closingAfterQueue) return Task.CompletedTask;
        RefreshConnectionStatus();
        if (!_pollTask.IsCompleted) return _pollTask;
        _pollTask = PollHistoryCoreAsync();
        return _pollTask;
    }
    private async Task PollHistoryCoreAsync()
    {
        try
        {
            await _seedHistory;
            if (_closed || _closingAfterQueue) return;
            var items = await Task.Run(() => _index.List().Select(TransferActivity.Snapshot).ToArray(), _lifetime.Token);
            if (_closed || _closingAfterQueue) return;
            foreach (var activity in _activities.Observe(items, _panel.IsQueuing)) PublishActivity(activity);
            _queuedCount = items.Count(i => i.Direction == "outbound" && !i.Read);
            RefreshConnectionStatus();
            await _panel.RefreshHistoryAsync();
        }
        catch (OperationCanceledException) when (_closed) { }
    }
    private async void TransferQueued(object? sender, TransferActivity activity)
    {
        await _seedHistory;
        if (_closed || _closingAfterQueue) return;
        foreach (var queued in _activities.RecordQueued(activity.Items)) PublishActivity(queued);
    }
    private void PublishActivity(TransferActivity activity)
    {
        ActivityObserved?.Invoke(this, activity);
        if (activity.Kind == TransferActivityKind.Received) NotifyInboundReceived(activity.Items);
        if (_currentActivity is null) ShowActivity(activity);
        else
        {
            var same = _waitingActivities.FindIndex(a => a.Kind == activity.Kind && a.Status == activity.Status);
            if (same >= 0) _waitingActivities[same] = new TransferActivity(activity.Kind, _waitingActivities[same].Items.Concat(activity.Items));
            else _waitingActivities.Add(activity);
        }
    }
    private void ShowActivity(TransferActivity activity)
    {
        _currentActivity = activity;
        PillTitle.Text = activity.PillStatus;
        PillStatus.Text = activity.Detail;
        PillRoot.ToolTip = $"{activity.Status}\n{activity.Detail}";
        PillKind.Text = TransferSymbol.For(activity.Items[0]);
        PillKind.Foreground = new SolidColorBrush(activity.Outgoing ? Color.FromRgb(244, 206, 106) : Color.FromRgb(120, 221, 208));
        System.Windows.Automation.AutomationProperties.SetName(PillRoot, $"{activity.Status}. {activity.Detail}");
        PillRail.ShowActivity(activity);
        _panel.ShowActivity(activity);
        _activityTimer.Stop();
        _activityTimer.Start();
    }
    private void ShowNextActivity()
    {
        _activityTimer.Stop();
        if (_closed || _closingAfterQueue) return;
        if (_waitingActivities.Count > 0)
        {
            var next = _waitingActivities[0];
            _waitingActivities.RemoveAt(0);
            ShowActivity(next);
        }
        else { _currentActivity = null; ShowIdleStatus(); }
    }
    private void ShowIdleStatus()
    {
        var connection = ConnectionStatus.Describe(_presence.Snapshot());
        PillTitle.Text = "Beam";
        PillStatus.Text = _queuedCount > 0 ? $"{_queuedCount} queued for phone" : connection.Pill;
        PillRoot.ToolTip = $"{connection.Pill}\n{connection.Detail}";
        PillKind.Text = "\uE898";
        PillKind.Foreground = (Brush)FindResource("BeamBrush");
        System.Windows.Automation.AutomationProperties.SetName(PillRoot, $"Beam. {PillStatus.Text}");
        PillRail.ClearActivity();
    }
    private void RefreshConnectionStatus()
    {
        var connection = ConnectionStatus.Describe(_presence.Snapshot());
        ServerStatus.Text = connection.Header + (_queuedCount > 0 ? $" / {_queuedCount} queued" : "");
        ServerStatus.ToolTip = connection.Detail;
        System.Windows.Automation.AutomationProperties.SetName(ServerStatus, $"{ServerStatus.Text}. {connection.Detail}");
        if (_currentActivity is null) ShowIdleStatus();
    }
    private static void NotifyInboundReceived(IReadOnlyList<HistoryItem> items)
    {
        try
        {
            new ToastContentBuilder().AddText("Beam")
                .AddText(items.Count > 1 ? $"{items.Count} items received" : items[0].IsText ? "New note received" : "New file received").Show();
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            // Windows notifications are optional; receiving and refreshing must still work.
        }
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
