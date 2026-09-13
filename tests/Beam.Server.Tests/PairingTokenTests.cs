using Beam.Server.Security;

namespace Beam.Server.Tests;

public class PairingTokenTests
{
    [Fact]
    public void LoadOrCreate_CreatesAndPersistsToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var first = PairingToken.LoadOrCreate(dir);
            var second = PairingToken.LoadOrCreate(dir);

            Assert.False(string.IsNullOrWhiteSpace(first));
            Assert.Equal(first, second); // same directory => same token on reload
            Assert.True(File.Exists(Path.Combine(dir, "token.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadOrCreate_DifferentDirectories_ProduceDifferentTokens()
    {
        var dirA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var dirB = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            var tokenA = PairingToken.LoadOrCreate(dirA);
            var tokenB = PairingToken.LoadOrCreate(dirB);
            Assert.NotEqual(tokenA, tokenB);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }
}
