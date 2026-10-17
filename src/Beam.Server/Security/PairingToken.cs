using System.Security.Cryptography;
using System.Text;

namespace Beam.Server.Security;

public static class PairingToken
{
    public static string LoadOrCreate(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, "token.txt");
        var existed = File.Exists(tokenPath);
        if (existed)
        {
            var existing = File.ReadAllText(tokenPath).Trim();
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
        }
        var token = Generate();
        try { Save(dataDirectory, token, replace: existed); return token; }
        catch (IOException) when (!existed && File.Exists(tokenPath))
        {
            // Another first launch won the atomic create; use its persisted key.
            var existing = File.ReadAllText(tokenPath).Trim();
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
            throw;
        }
    }

    internal static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static void Save(string dataDirectory, string token, bool replace = true)
    {
        Directory.CreateDirectory(dataDirectory);
        var temporary = Path.Combine(dataDirectory, ".token-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                file.Write(Encoding.UTF8.GetBytes(token));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(dataDirectory, "token.txt"), overwrite: replace);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
