using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace Beam.App;

public class TrayIconService : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private PillWindow? _pillWindow;
    private Icon? _icon;
    public void Initialize()
    {
        _pillWindow = new PillWindow();
        Application.Current.MainWindow = _pillWindow;
        _icon = CreateIcon();
        _taskbarIcon = new TaskbarIcon { ToolTipText = "Beam - LAN transfer", Icon = _icon, ContextMenu = BuildContextMenu() };
        _taskbarIcon.TrayLeftMouseUp += (_, _) => _pillWindow.OpenWorkspace();
        _pillWindow.OpenWorkspace();
    }
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        Add("Open Beam", () => _pillWindow?.OpenWorkspace());
        Add("Open Vault", () => _pillWindow?.OpenWorkspace(true));
        Add("Pill Mode", () => _pillWindow?.ShowPillMode());
        Add("Pair Phone", App.ShowPairing);
        Add("Hide to Tray", () => _pillWindow?.Hide());
        menu.Items.Add(new Separator());
        Add("Quit Beam", () => _pillWindow?.Close());
        return menu;
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var cyan = new SolidBrush(Color.FromArgb(120, 221, 208));
        using var yellow = new SolidBrush(Color.FromArgb(244, 206, 106));
        graphics.FillRectangle(cyan, 5, 4, 5, 24);
        graphics.FillRectangle(cyan, 14, 8, 5, 16);
        graphics.FillRectangle(yellow, 23, 12, 5, 8);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    public void Dispose()
    {
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _icon?.Dispose();
        _icon = null;
    }
}
