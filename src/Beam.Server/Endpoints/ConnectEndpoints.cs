namespace Beam.Server.Endpoints;

public static class ConnectEndpoints
{
    public static void MapConnectEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/app/"));
    }
}
