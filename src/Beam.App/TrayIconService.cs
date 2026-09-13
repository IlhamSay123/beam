using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace Beam.App;

public class TrayIconService : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private PillWindow? _pillWindow;

    public void Initialize()
    {
        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Beam",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = BuildContextMenu()
        };

        _taskbarIcon.TrayLeftMouseUp += (_, _) => TogglePill();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void TogglePill()
    {
        _pillWindow ??= new PillWindow();

        if (!_pillWindow.IsVisible)
        {
            _pillWindow.Show();
        }

        _pillWindow.ToggleExpanded();
    }

    public void Dispose()
    {
        _taskbarIcon?.Dispose();
    }
}
