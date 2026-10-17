using System.IO;
using Microsoft.Win32;

namespace Beam.App;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class WindowsStartupRegistration(
    string executablePath,
    string runSubKey = WindowsStartupRegistration.DefaultRunSubKey,
    string valueName = "Beam") : IStartupRegistration
{
    public const string DefaultRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private string Command => '"' + executablePath + "\" --startup";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runSubKey);
            return string.Equals(key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!Path.IsPathFullyQualified(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || executablePath.Contains('"') || !File.Exists(executablePath) || Command.Length > 260)
                throw new ArgumentException("Windows startup needs an existing executable with a command no longer than 260 characters.");
            using var key = Registry.CurrentUser.CreateSubKey(runSubKey, writable: true);
            key.SetValue(valueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(runSubKey, writable: true);
            if (string.Equals(key?.GetValue(valueName) as string, Command, StringComparison.OrdinalIgnoreCase))
                key!.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
