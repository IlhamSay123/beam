// tests/Beam.Server.Tests/ReceiveEndpointsTests.cs
using System.Net.Http.Headers;
using System.Net;
using Beam.Server;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beam.Server.Tests;

public class ReceiveEndpointsTests
{
    [Theory]
    [InlineData(8, 1048584)]
    [InlineData(104857600, 105906176)]
    public async Task Receive_HostConfiguresExplicitRequestAndFormLimits(long uploadLimit, long requestLimit)
    {
        var options = new BeamServerOptions(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), "test-token", 0)
        {
            MaxUploadBytes = uploadLimit
        };
        await using var app = BeamServerHost.Build(options);

        Assert.Equal(requestLimit, app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        Assert.Equal(requestLimit, app.Services.GetRequiredService<IOptions<FormOptions>>().Value.MultipartBodyLengthLimit);
        Assert.Equal(requestLimit, app.Services.GetRequiredService<IOptions<FormOptions>>().Value.ValueLengthLimit);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Receive_FileAtLimit_PreservesBytes(bool multipart, bool chunked)
    {
        var (app, client, options) = await StartAppAsync(uploadLimit: 8);
        await using var appLifetime = app;
        using var clientLifetime = client;
        var bytes = new byte[] { 0, 255, 1, 128, 13, 10, 42, 0 };
        using var content = CreateFileBody(bytes, multipart, chunked);

        using var response = await client.PostAsync("/receive", content);

        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")).List());
        Assert.Equal(item.Id, json.RootElement.GetProperty("ids")[0].GetString());
        Assert.Equal(8, item.SizeBytes);
        if (multipart) Assert.Equal("original name.bin", item.Name);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(item.FilePath!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Receive_FileOverLimit_ReturnsHelpful413WithoutSaving(bool multipart, bool chunked)
    {
        var (app, client, options) = await StartAppAsync(uploadLimit: 8);
        await using var appLifetime = app;
        using var clientLifetime = client;
        using var content = CreateFileBody(new byte[9], multipart, chunked);

        using var response = await client.PostAsync("/receive", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("file", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        AssertNothingSaved(options);
    }

    [Fact]
    public async Task Receive_OversizeLaterFile_RejectsWholeForm()
    {
        var (app, client, options) = await StartAppAsync(uploadLimit: 8);
        await using var appLifetime = app;
        using var clientLifetime = client;
        using var form = CreateMixedForm("valid");
        form.Add(new ByteArrayContent(new byte[9]), "files", "too-large.bin");

        using var response = await client.PostAsync("/receive", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        AssertNothingSaved(options);
    }

    [Fact]
    public async Task Receive_RepeatedTextFields_ValidatesCombinedUtf8SizeBeforeSaving()
    {
        var (app, client, options) = await StartAppAsync(textLimit: 8);
        await using var appLifetime = app;
        using var clientLifetime = client;
        using var form = CreateMixedForm("1234");
        form.Add(new StringContent("5678"), "text");

        using var response = await client.PostAsync("/receive", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        AssertNothingSaved(options);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, -1024)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task Receive_RequestOverheadLimit_AppliesToWholeMultipartBody(bool chunked, int extraBytes)
    {
        var (app, client, options) = await StartAppAsync(uploadLimit: 8);
        await using var appLifetime = app;
        using var clientLifetime = client;
        client.DefaultRequestHeaders.ExpectContinue = true;
        using var emptyPaddingForm = CreatePaddedForm(0);
        var overhead = (await emptyPaddingForm.ReadAsByteArrayAsync()).Length;
        var form = CreatePaddedForm(1048584 - overhead + extraBytes);
        using HttpContent content = chunked ? new UnknownLengthContent(form) : form;

        using var response = await client.PostAsync("/receive", content);

        // Kestrel counts chunk framing against its request limit too.
        if (extraBytes <= 0)
        {
            response.EnsureSuccessStatusCode();
            Assert.Single(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")).List());
        }
        else
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Contains("request", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            AssertNothingSaved(options);
        }
    }

    private static MultipartFormDataContent CreatePaddedForm(int paddingLength)
    {
        var form = new MultipartFormDataContent("beam-boundary");
        form.Add(new ByteArrayContent(new byte[8]), "files", "eight.bin");
        form.Add(new StringContent(new string('x', paddingLength)), "padding");
        return form;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receive_CombinedFilesOverRequestCap_RejectsWholeForm(bool chunked)
    {
        var (app, client, options) = await StartAppAsync(uploadLimit: 1024 * 1024);
        await using var appLifetime = app;
        using var clientLifetime = client;
        client.DefaultRequestHeaders.ExpectContinue = true;
        var form = new MultipartFormDataContent();
        for (var i = 0; i < 3; i++)
            form.Add(new ByteArrayContent(new byte[768 * 1024]), "files", $"file-{i}.bin");
        using HttpContent content = chunked ? new UnknownLengthContent(form) : form;

        using var response = await client.PostAsync("/receive", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        AssertNothingSaved(options);
    }

    [Fact]
    public async Task Receive_OversizeBodyWithoutToken_Returns401()
    {
        var (app, client, options) = await StartAppAsync(uploadLimit: 8);
        await using var appLifetime = app;
        using var clientLifetime = client;
        client.DefaultRequestHeaders.ExpectContinue = true;
        client.DefaultRequestHeaders.Remove("X-Beam-Token");
        using var content = CreateFileBody(new byte[1048585], multipart: false, chunked: false);

        using var response = await client.PostAsync("/receive", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNothingSaved(options);
    }

    [Fact]
    public async Task Receive_EmptyChunkedDirectFile_Returns400WithoutSaving()
    {
        var (app, client, options) = await StartAppAsync();
        await using var appLifetime = app;
        using var clientLifetime = client;
        using var content = CreateFileBody(Array.Empty<byte>(), multipart: false, chunked: true);

        using var response = await client.PostAsync("/receive", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNothingSaved(options);
    }

    private static void AssertNothingSaved(BeamServerOptions options)
    {
        Assert.Empty(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")).List());
        Assert.Empty(Directory.GetFiles(Path.Combine(options.DataDirectory, "Inbox")));
    }

    private static HttpContent CreateFileBody(byte[] bytes, bool multipart, bool chunked)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpContent content = file;
        if (multipart)
        {
            var form = new MultipartFormDataContent();
            form.Add(file, "files", "original name.bin");
            content = form;
        }
        return chunked ? new UnknownLengthContent(content) : content;
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _inner;

        public UnknownLengthContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receive_OversizeUtf8Text_RejectsWholeForm(bool multipart)
    {
        var (app, client, options) = await StartAppAsync();
        await using var appLifetime = app;
        using var clientLifetime = client;
        var text = new string('\u00e9', 32769);
        using HttpContent form = multipart
            ? CreateMixedForm(text)
            : new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text });

        using var response = await client.PostAsync("/receive", form);

        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("text", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")).List());
        Assert.Empty(Directory.GetFiles(Path.Combine(options.DataDirectory, "Inbox")));
    }

    [Fact]
    public async Task Receive_TextAtUtf8Limit_PreservesText()
    {
        var (app, client, options) = await StartAppAsync();
        await using var appLifetime = app;
        using var clientLifetime = client;
        var text = new string('\u00e9', 32768);
        using var form = CreateMixedForm(text);

        using var response = await client.PostAsync("/receive", form);

        response.EnsureSuccessStatusCode();
        var items = new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")).List();
        var item = Assert.Single(items.Where(item => item.IsText));
        Assert.Equal(text, item.TextContent);
        Assert.Equal(65536, item.SizeBytes);
    }

    [Fact]
    public async Task Receive_ZeroByteMultipartFile_PreservesOriginalNameAndEmptyBytes()
    {
        var (app, client, options) = await StartAppAsync();
        await using var appLifetime = app;
        using var clientLifetime = client;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Array.Empty<byte>()), "files", "empty note.txt");

        using var response = await client.PostAsync("/receive", form);

        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")).List());
        Assert.Equal(item.Id, json.RootElement.GetProperty("ids")[0].GetString());
        Assert.Equal("empty note.txt", item.Name);
        Assert.Equal(0, item.SizeBytes);
        Assert.Empty(await File.ReadAllBytesAsync(item.FilePath!));
    }

    private static MultipartFormDataContent CreateMixedForm(string text)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 0, 255, 42 }), "files", "original name.bin");
        form.Add(new StringContent(text), "text");
        return form;
    }

    [Theory]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("application/octet-stream", ".bin")]
    public async Task Receive_DirectFileBody_SavesExactBytes(string contentType, string extension)
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var bytes = new byte[] { 255, 216, 1, 2, 3, 255, 217 };
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            var response = await client.PostAsync("/receive", content);
            response.EnsureSuccessStatusCode();
            var result = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, result.RootElement.GetProperty("ids").GetArrayLength());
            var item = Assert.Single(new HistoryIndex(Path.Combine(options.DataDirectory, "history.json")).List());
            Assert.Equal("inbound", item.Direction);
            Assert.Equal(contentType, item.ContentType);
            Assert.EndsWith(extension, item.Name);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(item.FilePath!));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Receive_EmptyForm_ReturnsHelpfulErrorInsteadOfEmptySuccess()
    {
        var (app, client, _) = await StartAppAsync();
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["files"] = "photo.jpg" });
            var response = await client.PostAsync("/receive", form);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("No file bytes or text received", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Receive_EmptyDirectFile_Returns400WithoutSavingItem()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            using var content = new ByteArrayContent(Array.Empty<byte>());
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var response = await client.PostAsync("/receive", content);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(Directory.GetFiles(Path.Combine(options.DataDirectory, "Inbox")));
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<(WebApplication app, HttpClient client, BeamServerOptions options)> StartAppAsync(
        long uploadLimit = 104857600, int textLimit = 65536)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new BeamServerOptions(dataDir, "test-token", Port: 0)
        {
            MaxUploadBytes = uploadLimit,
            MaxTextBytes = textLimit
        };
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
