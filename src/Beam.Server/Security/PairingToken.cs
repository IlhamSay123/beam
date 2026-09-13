using System.Security.Cryptography;

namespace Beam.Server.Security;

public static class PairingToken
{
    public static string LoadOrCreate(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, "token.txt");

        if (File.Exists(tokenPath))
        {
            var existing = File.ReadAllText(tokenPath).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var bytes = RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        File.WriteAllText(tokenPath, token);
        return token;
    }
}
