namespace Beam.App;

public static class AppInstanceGate
{
    // Shared with Beam.iss; keep the handle alive until process termination.
    public const string MutexName = @"Local\Beam.Desktop.6B0808A8-7811-4EBF-9223-BD041CAB8D4C";

    public static Mutex? TryAcquire(string name = MutexName)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out var created);
        if (created) return mutex;
        mutex.Dispose();
        return null;
    }
}
