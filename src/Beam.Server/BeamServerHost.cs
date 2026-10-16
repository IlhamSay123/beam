using Beam.Server.Endpoints;
using Beam.Server.Security;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;

namespace Beam.Server;

public static class BeamServerHost
{
    public static WebApplication Build(BeamServerOptions options, Action<IWebHostBuilder>? configureWebHost = null)
    {
        // ContentRootPath must be pinned to the running assembly's directory,
        // not the process's working directory (the default). `dotnet run
        // --project src/Beam.App` sets the CWD to the source project folder,
        // which has no wwwroot of its own (only the build output does, via
        // Beam.Server's project reference) — leaving it on the default
        // caused every /app/* request to 404.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        if (configureWebHost is not null)
        {
            configureWebHost(builder.WebHost);
        }
        else
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        }

        builder.WebHost.ConfigureKestrel(server =>
            server.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = options.MaxRequestBodyBytes;
            form.ValueLengthLimit = (int)Math.Min(int.MaxValue, options.MaxRequestBodyBytes);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")));
        builder.Services.AddSingleton(new ItemStore(options.DataDirectory));

        var app = builder.Build();

        app.UseMiddleware<TokenAuthMiddleware>();
        app.MapCapabilitiesEndpoints();
        app.MapReceiveEndpoints();
        app.MapOutboxEndpoints();
        app.MapConnectEndpoints();

        // Serves wwwroot/index.html (and app.js/styles.css/manifest) under
        // /app once Task 13 adds content there. UseDefaultFiles must be
        // registered before UseStaticFiles for /app/ to resolve to index.html.
        // No-cache: these files are small, served rarely, and change during
        // development — a stale cached app.js on the phone (e.g. from before
        // a bugfix) would silently keep running old code with no visible sign.
        var staticFileOptions = new StaticFileOptions
        {
            RequestPath = "/app",
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Context.Response.Headers.Pragma = "no-cache";
                ctx.Context.Response.Headers.Expires = "0";
            }
        };
        app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/app" });
        app.UseStaticFiles(staticFileOptions);

        return app;
    }
}
