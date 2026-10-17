using System.IO;
using System.Reflection;
using Beam.App;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

internal static class StartupChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run(string folder)
    {
        var type = typeof(App).Assembly.GetType("Beam.App.WindowsStartupRegistration");
        Require(type is not null, "Windows startup registration is not implemented.");
        var subkey = @"Software\Beam\SmokeTests\" + Guid.NewGuid().ToString("N");
        var executable = Path.Combine(folder, "A folder with spaces", "Beam.App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, []); // Never executed; only the persisted command is inspected.
        object Registration(string path) => Activator.CreateInstance(type!, path, subkey, "Beam")!;
        bool Enabled(object registration) => (bool)type!.GetProperty("IsEnabled")!.GetValue(registration)!;
        void Enable(object registration, bool enabled) => type!.GetMethod("SetEnabled")!.Invoke(registration, [enabled]);
        var expected = '"' + executable + "\" --startup";
        try
        {
            var registration = Registration(executable);
            Require(!Enabled(registration), "New startup preference must be off.");
            Enable(registration, false);
            using (var absent = Registry.CurrentUser.OpenSubKey(subkey))
                Require(absent is null, "Turning off an absent preference created a registry key.");
            using (var sentinel = Registry.CurrentUser.CreateSubKey(subkey)) sentinel.SetValue("Unrelated", "keep");
            Enable(registration, true);
            using (var saved = Registry.CurrentUser.OpenSubKey(subkey))
                Require((string?)saved!.GetValue("Beam") == expected && (string?)saved.GetValue("Unrelated") == "keep",
                    "Startup command must quote the entire executable and preserve unrelated values.");
            Require(Enabled(Registration(executable)), "Startup state did not survive a new registration instance.");
            Enable(registration, false);
            using (var saved = Registry.CurrentUser.OpenSubKey(subkey))
                Require(saved!.GetValue("Beam") is null && (string?)saved.GetValue("Unrelated") == "keep",
                    "Disabling startup removed another registry value.");
            Enable(registration, true);
            var other = Registration(Path.Combine(folder, "Other copy", "Beam.App.exe"));
            Require(!Enabled(other), "A different Beam copy was reported as this executable's registration.");
            Enable(other, false);
            using (var saved = Registry.CurrentUser.OpenSubKey(subkey))
                Require((string?)saved!.GetValue("Beam") == expected, "Disabling another copy removed this copy's command.");
            var rejected = false;
            try { Enable(other, true); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { rejected = true; }
            Require(rejected && Enabled(registration), "An invalid executable replaced the working startup command.");

            var longExecutable = Path.Combine(folder, new string('a', 80), new string('b', 80), new string('c', 80), "Beam.App.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(longExecutable)!);
            File.WriteAllBytes(longExecutable, []);
            rejected = false;
            try { Enable(Registration(longExecutable), true); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { rejected = true; }
            Require(rejected && Enabled(registration), "An oversized Run command changed the working registration.");

            var errors = new List<string>();
            var menu = new StartupMenuItem((IStartupRegistration)registration, errors.Add);
            Require(menu.IsChecked, "Startup menu did not read the existing preference.");
            menu.IsChecked = false;
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(!Enabled(registration), "Startup menu did not remove the current registration.");
            menu.IsChecked = true;
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(Enabled(registration), "Startup menu did not enable registration.");
            var failure = new ControlledStartup { FailWrite = true };
            var failingMenu = new StartupMenuItem(failure, errors.Add) { IsChecked = true };
            failingMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(errors.Count == 1 && !failingMenu.IsChecked, "Failed preference write claimed startup was enabled.");
            failure.FailRead = true;
            failingMenu.RefreshState();
            Require(!failingMenu.IsEnabled, "An unreadable preference remained editable.");
            failure.FailRead = false;
            failingMenu.RefreshState();
            Require(failingMenu.IsEnabled && ((string)failingMenu.ToolTip).StartsWith("Start in"),
                "Recovered startup preference retained its read-error tooltip.");

            var gate = typeof(App).Assembly.GetType("Beam.App.AppInstanceGate");
            Require(gate is not null, "Desktop instance lifetime gate is missing.");
            var acquire = gate!.GetMethod("TryAcquire")!;
            var name = @"Local\Beam.Smoke." + Guid.NewGuid().ToString("N");
            var first = (IDisposable?)acquire.Invoke(null, [name]);
            Require(first is not null, "The first instance could not start.");
            try { Require(acquire.Invoke(null, [name]) is null, "A duplicate instance acquired the same lifetime."); }
            finally { first!.Dispose(); }
            using var reopened = (IDisposable?)acquire.Invoke(null, [name]);
            Require(reopened is not null, "A released instance lifetime could not be acquired again.");
            Console.WriteLine("PASS: startup defaults off; quoted command persists; disable preserves other values/copies; invalid/long paths roll back; duplicate instance is blocked and can reopen.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false); }
    }

    private sealed class ControlledStartup : IStartupRegistration
    {
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        private bool _enabled;
        public bool IsEnabled => FailRead ? throw new UnauthorizedAccessException() : _enabled;
        public void SetEnabled(bool enabled)
        {
            if (FailWrite) throw new UnauthorizedAccessException();
            _enabled = enabled;
        }
    }
}
