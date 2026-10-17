using Beam.Server;
using Beam.Server.Connection;
using Beam.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Beam.Server.Tests;

public class TokenAuthMiddlewareTests
{
    private static async Task<(WebApplication app, HttpClient client)> StartAppAsync(string expectedToken)
    {
        var options = new BeamServerOptions(Path.GetTempPath(), expectedToken, Port: 0);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ConnectionPresence>();
        builder.Services.AddSingleton<PairingTokenStore>();
        var app = builder.Build();
        app.UseMiddleware<TokenAuthMiddleware>();
        app.MapGet("/protected", () => "ok");
        app.MapGet("/", () => "public");
        await app.StartAsync();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client);
    }

    [Fact]
    public async Task Request_WithCorrectToken_Succeeds()
    {
        var (app, client) = await StartAppAsync("secret123");
        try
        {
            client.DefaultRequestHeaders.Add("X-Beam-Token", "secret123");
            var response = await client.GetAsync("/protected");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Request_WithWrongToken_Returns401()
    {
        var (app, client) = await StartAppAsync("secret123");
        try
        {
            client.DefaultRequestHeaders.Add("X-Beam-Token", "wrong");
            var response = await client.GetAsync("/protected");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Pair again with the PC", body);
            Assert.DoesNotContain("secret123", body);
            Assert.DoesNotContain("wrong", body);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Request_WithMissingToken_Returns401()
    {
        var (app, client) = await StartAppAsync("secret123");
        try
        {
            var response = await client.GetAsync("/protected");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Invalid or missing token", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task RootPath_NeverRequiresToken()
    {
        var (app, client) = await StartAppAsync("secret123");
        try
        {
            var response = await client.GetAsync("/");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
