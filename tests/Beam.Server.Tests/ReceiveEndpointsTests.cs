// tests/Beam.Server.Tests/ReceiveEndpointsTests.cs
using System.Net.Http.Headers;
using Beam.Server;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Beam.Server.Tests;

public class ReceiveEndpointsTests
{
    private static async Task<(WebApplication app, HttpClient client, BeamServerOptions options)> StartAppAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new BeamServerOptions(dataDir, "test-token", Port: 0);
        var app = BeamServerHost.Build(options, webHost => webHost.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
        return (app, client, options);
    }

    [Fact]
    public async Task Receive_SingleFile_SavesToInboxAndReturnsId()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(new byte[] { 1, 2, 3 });
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(fileContent, "files", "photo.jpg");

            var response = await client.PostAsync("/receive", form);
            response.EnsureSuccessStatusCode();

            var index = new HistoryIndex(Path.Combine(options.DataDirectory, "history.json"));
            var items = index.List();
            Assert.Single(items);
            Assert.Equal("inbound", items[0].Direction);
            Assert.Equal("photo.jpg", items[0].Name);
            Assert.True(File.Exists(items[0].FilePath));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Receive_MultipleFilesAndText_SavesAllAsOneBatch()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(new byte[] { 1 }), "files", "a.jpg");
            form.Add(new ByteArrayContent(new byte[] { 2 }), "files", "b.jpg");
            form.Add(new StringContent("call mom"), "text");

            var response = await client.PostAsync("/receive", form);
            response.EnsureSuccessStatusCode();

            var index = new HistoryIndex(Path.Combine(options.DataDirectory, "history.json"));
            var items = index.List();
            Assert.Equal(3, items.Count);
            Assert.Equal(2, items.Count(i => !i.IsText));
            Assert.Single(items.Where(i => i.IsText && i.TextContent == "call mom"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Receive_WithoutToken_Returns401()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new BeamServerOptions(dataDir, "test-token", Port: 0);
        var app = BeamServerHost.Build(options, webHost => webHost.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();
        try
        {
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("hi"), "text");

            var response = await client.PostAsync("/receive", form);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
