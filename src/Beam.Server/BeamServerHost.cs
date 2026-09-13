using Beam.Server.Endpoints;
using Beam.Server.Security;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Hosting;

namespace Beam.Server;

public static class BeamServerHost
{
    public static WebApplication Build(BeamServerOptions options, Action<IWebHostBuilder>? configureWebHost = null)
    {
        var builder = WebApplication.CreateBuilder();

        if (configureWebHost is not null)
        {
            configureWebHost(builder.WebHost);
        }
        else
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")));
        builder.Services.AddSingleton(new ItemStore(options.DataDirectory));

        var app = builder.Build();

        app.UseMiddleware<TokenAuthMiddleware>();
        app.MapReceiveEndpoints();
        app.MapOutboxEndpoints();

        // Serves wwwroot/index.html (and app.js/styles.css/manifest) under
        // /app once Task 13 adds content there. UseDefaultFiles must be
        // registered before UseStaticFiles for /app/ to resolve to index.html.
        var staticFileOptions = new StaticFileOptions { RequestPath = "/app" };
        app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/app" });
        app.UseStaticFiles(staticFileOptions);

        return app;
    }
}
