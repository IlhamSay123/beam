using System.Net;
using Beam.Server;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.Server.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task Root_RedirectsToSetupWithoutDisclosingToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await using var app = BeamServerHost.Build(new(directory, "private-token", 0),
            host => host.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Urls.First())
        };

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/app/", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain("private-token", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task App_StaticAssetsAreAvailableWithoutAuthentication()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await using var app = BeamServerHost.Build(new(directory, "private-token", 0),
            host => host.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        foreach (var path in new[] { "/app/", "/app/app.js", "/app/styles.css" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task TransferAndAcknowledgement_SurviveServerRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new BeamServerOptions(directory, "test-token", 0);
        string outboundId;
        await using (var app = BeamServerHost.Build(options, host => host.UseUrls("http://127.0.0.1:0")))
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent([1, 2, 3]), "files", "a.jpg");
            form.Add(new ByteArrayContent([4, 5]), "files", "b.jpg");
            form.Add(new StringContent("phone note"), "text");
            (await client.PostAsync("/receive", form)).EnsureSuccessStatusCode();
            var index = app.Services.GetRequiredService<HistoryIndex>();
            outboundId = OutboxQueue.QueueText(index, "PC note").Id;
            (await client.PostAsync($"/outbox/{outboundId}/ack", null)).EnsureSuccessStatusCode();
            await app.StopAsync();
        }

        await using var restarted = BeamServerHost.Build(options, host => host.UseUrls("http://127.0.0.1:0"));
        await restarted.StartAsync();
        var saved = restarted.Services.GetRequiredService<HistoryIndex>();
        Assert.Equal(3, saved.List().Count(item => item.Direction == "inbound"));
        Assert.Equal("phone note", saved.List().Single(item => item.Direction == "inbound" && item.IsText).TextContent);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(saved.List().Single(item => item.Name == "a.jpg").FilePath!));
        Assert.True(saved.Get(outboundId)!.Read);
        Assert.Empty(saved.ListUnread("outbound"));
    }
}
