using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;

namespace Beam.App;

public readonly record struct TransferSignalFrame(double PacketLeft, double PacketWidth, double TrailLeft, double TrailWidth);

public static class TransferSignalGeometry
{
    public static TransferSignalFrame At(double width, double packetWidth, double phase, bool outgoing, bool animationsEnabled)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        packetWidth = double.IsFinite(packetWidth) ? Math.Clamp(packetWidth, 0, width) : 0;
        phase = animationsEnabled && double.IsFinite(phase) ? Math.Clamp(phase, 0, 1) : .5;
        var left = (outgoing ? phase : 1 - phase) * (width - packetWidth);
        var edge = outgoing ? Math.Max(0, left - 5) : Math.Min(width, left + packetWidth + 5);
        var length = animationsEnabled ? Math.Min(32, outgoing ? edge : width - edge) : 0;
        return new(left, packetWidth, outgoing ? edge - length : edge, length);
    }
}

// This glide announces a persisted event. Its position never represents network progress.
public sealed class TransferMotionView : FrameworkElement
{
    private static readonly Brush Rail = FrozenBrush(51, 58, 63);
    private static readonly Brush Panel = FrozenBrush(28, 32, 35);
    private static readonly Brush Cyan = FrozenBrush(120, 221, 208);
    private static readonly Brush Gold = FrozenBrush(244, 206, 106);
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _animationsEnabled;
    private readonly Stopwatch _elapsed = new();
    private TransferActivity? _activity;
    private bool _motionAllowed;
    private bool _stopped;
    public bool IsAnimating => _timer.IsEnabled;
    public TransferActivity? Activity => _activity;
    public ImageSource? Thumbnail { get; private set; }

    public TransferMotionView() : this(() => SystemParameters.ClientAreaAnimation) { }
    public TransferMotionView(Func<bool> animationsEnabled)
    {
        _animationsEnabled = animationsEnabled;
        ClipToBounds = true;
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public void ShowActivity(TransferActivity activity)
    {
        if (_stopped) return;
        _activity = activity;
        Thumbnail = null;
        _motionAllowed = _animationsEnabled();
        _elapsed.Restart();
        AutomationProperties.SetName(this, $"{activity.Status}. {activity.Detail}");
        UpdateTimer();
        InvalidateVisual();
    }
    public void SetThumbnail(ImageSource thumbnail)
    {
        if (_stopped || _activity is null || TransferSymbol.For(_activity.Items[0]) != TransferSymbol.Photo) return;
        Thumbnail = thumbnail;
        InvalidateVisual();
    }

    public void Stop()
    {
        _stopped = true;
        _timer.Stop();
        _elapsed.Stop();
        SystemParameters.StaticPropertyChanged -= PreferenceChanged;
        InvalidateVisual();
    }
    public void ClearActivity()
    {
        _timer.Stop();
        _elapsed.Reset();
        _activity = null;
        Thumbnail = null;
        InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= PreferenceChanged;
        if (!_stopped) SystemParameters.StaticPropertyChanged += PreferenceChanged;
        UpdateTimer();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        SystemParameters.StaticPropertyChanged -= PreferenceChanged;
    }
    private void PreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (!_animationsEnabled()) _motionAllowed = false;
        UpdateTimer();
        InvalidateVisual();
    }
    private void UpdateTimer()
    {
        if (!_animationsEnabled()) _motionAllowed = false;
        if (!_stopped && IsVisible && _activity is not null && _motionAllowed && _elapsed.Elapsed.TotalSeconds < 1.25) _timer.Start();
        else _timer.Stop();
    }
    private void Tick(object? sender, EventArgs e) { UpdateTimer(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        drawing.DrawRectangle(Rail, null, new Rect(0, Math.Max(0, (height - 2) / 2), width, Math.Min(2, height)));
        if (_activity is not null)
        {
            var size = Math.Min(height, 28);
            var moving = !_stopped && _motionAllowed && _animationsEnabled();
            var phase = Math.Clamp(_elapsed.Elapsed.TotalSeconds / 1.25, 0, 1);
            var frame = TransferSignalGeometry.At(width, size, phase, _activity.Outgoing, moving);
            var color = _activity.Outgoing ? Gold : Cyan;
            if (moving && phase < 1 && frame.TrailWidth > 0)
            {
                drawing.PushOpacity(.6);
                drawing.DrawRectangle(color, null, new Rect(frame.TrailLeft, (height - 2) / 2, frame.TrailWidth, 2));
                drawing.Pop();
            }
            var packet = new Rect(frame.PacketLeft, (height - size) / 2, frame.PacketWidth, size);
            if (size > 18) drawing.DrawRoundedRectangle(Panel, new Pen(color, 1), new Rect(packet.X + .5, packet.Y + .5, Math.Max(0, packet.Width - 1), Math.Max(0, packet.Height - 1)), 4, 4);
            else drawing.DrawRectangle(Panel, null, packet);
            if (size > 18 && Thumbnail is { Width: > 0, Height: > 0 } thumbnail)
            {
                var imageBounds = new Rect(packet.X + 3, packet.Y + 3, Math.Max(0, packet.Width - 6), size - 6);
                var scale = Math.Max(imageBounds.Width / thumbnail.Width, imageBounds.Height / thumbnail.Height);
                drawing.PushClip(new RectangleGeometry(imageBounds, 2, 2));
                drawing.DrawImage(thumbnail, new Rect(imageBounds.X + (imageBounds.Width - thumbnail.Width * scale) / 2,
                    imageBounds.Y + (imageBounds.Height - thumbnail.Height * scale) / 2, thumbnail.Width * scale, thumbnail.Height * scale));
                drawing.Pop();
            }
            else
            {
                var glyph = new FormattedText(TransferSymbol.For(_activity.Items[0]), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe MDL2 Assets"), Math.Max(1, Math.Min(16, size - 2)), color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                drawing.DrawText(glyph, new Point(packet.X + (packet.Width - glyph.Width) / 2, (height - glyph.Height) / 2));
            }
        }
        drawing.Pop();
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
