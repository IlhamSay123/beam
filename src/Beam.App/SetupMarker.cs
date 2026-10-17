using System.IO;

namespace Beam.App;

public sealed class SetupMarker(string directory)
{
    public bool TryClaimOffer()
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var marker = new FileStream(Path.Combine(directory, "setup-offered-v1"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.WriteByte(1);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
