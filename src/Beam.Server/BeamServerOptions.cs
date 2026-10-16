namespace Beam.Server;

public record BeamServerOptions(string DataDirectory, string ExpectedToken, int Port)
{
    public long MaxUploadBytes { get; init; } = 100 * 1024 * 1024;
    public int MaxTextBytes { get; init; } = 64 * 1024;
    public long MaxRequestBodyBytes => checked(MaxUploadBytes + 1024 * 1024);
}
