namespace Beam.Server.Endpoints;

public static class CapabilitiesEndpoints
{
    public static void MapCapabilitiesEndpoints(this WebApplication app)
    {
        app.MapGet("/capabilities", (BeamServerOptions options) => Results.Ok(new
        {
            maxUploadBytes = options.MaxUploadBytes,
            maxTextBytes = options.MaxTextBytes
        }));
    }
}
