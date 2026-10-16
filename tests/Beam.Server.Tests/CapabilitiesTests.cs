using System.Net;
using System.Text.Json;
using Beam.Server;
using Microsoft.AspNetCore.Hosting;

namespace Beam.Server.Tests;

public class CapabilitiesTests
{
    [Theory]
    [InlineData(104857600, 65536)]
    [InlineData(8, 4)]
    public async Task Capabilities_WithToken_ReturnsReceiveLimits(long uploadLimit, int textLimit)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new BeamServerOptions(directory, "test-token", 0)
        {
            MaxUploadBytes = uploadLimit,
            MaxTextBytes = textLimit
        };
        await using var app = BeamServerHost.Build(options,
            host => host.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");

        using var response = await client.GetAsync("/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(uploadLimit, json.RootElement.GetProperty("maxUploadBytes").GetInt64());
        Assert.Equal(textLimit, json.RootElement.GetProperty("maxTextBytes").GetInt32());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Capabilities_WithoutValidToken_Returns401(string? token)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await using var app = BeamServerHost.Build(new BeamServerOptions(directory, "test-token", 0),
            host => host.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        if (token is not null) client.DefaultRequestHeaders.Add("X-Beam-Token", token);

        using var response = await client.GetAsync("/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
