using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;

namespace Beam.App;

public sealed class StartupMenuItem : MenuItem
{
    private const string StartupHint = "Start in the system tray when you sign in. Windows startup settings can override this preference.";
    private readonly IStartupRegistration _registration;
    private readonly Action<string> _reportError;

    public StartupMenuItem() : this(new WindowsStartupRegistration(Environment.ProcessPath ?? ""),
        message => { MessageBox.Show(message, "Windows startup", MessageBoxButton.OK, MessageBoxImage.Warning); }) { }

    public StartupMenuItem(IStartupRegistration registration, Action<string> reportError)
    {
        _registration = registration;
        _reportError = reportError;
        Header = "Start Beam with Windows";
        IsCheckable = true;
        ToolTip = StartupHint;
        Loaded += (_, _) => RefreshState();
        Click += (_, _) =>
        {
            try { _registration.SetEnabled(IsChecked); }
            catch (Exception error) when (ExpectedFailure(error))
            {
                _reportError("Could not update Windows startup. Check that this Beam copy is still available and that Windows allows startup changes, then try again.");
            }
            RefreshState();
        };
        RefreshState();
    }

    public void RefreshState()
    {
        try { IsChecked = _registration.IsEnabled; IsEnabled = true; ToolTip = StartupHint; }
        catch (Exception error) when (ExpectedFailure(error))
        {
            IsChecked = false;
            IsEnabled = false;
            ToolTip = "Could not read Windows startup settings. Check your Windows account permissions, then reopen this menu.";
        }
    }

    private static bool ExpectedFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;
}
