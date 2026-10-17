using System.Net;
using System.Text;
using System.Text.Json;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.Server.Tests;

public class VaultEndpointsTests
{
    [Theory]
    [InlineData("/vault", null)]
    [InlineData("/vault", "wrong-token")]
    [InlineData("/vault/{id}/content", null)]
    [InlineData("/vault/{id}/content", "wrong-token")]
    [InlineData("/vault/{id}/preview", null)]
    [InlineData("/vault/{id}/preview", "wrong-token")]
    public async Task Vault_RequiresAuthentication(string route, string? token)
    {
        await using var server = await VaultServer.StartAsync();
        var item = await server.AddFileAsync("photo", "inbound", "photo.png", "image/png");
        server.Client.DefaultRequestHeaders.Remove("X-Beam-Token");
        if (token is not null) server.Client.DefaultRequestHeaders.Add("X-Beam-Token", token);

        using var response = await server.Client.GetAsync(route.Replace("{id}", item.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        server.AssertRead(item.Id, false);
    }

    [Fact]
    public async Task GetVault_ReturnsBothDirectionsIncludingReadItems_WithoutLocalPaths()
    {
        await using var server = await VaultServer.StartAsync();
        var inbound = await server.AddFileAsync("incoming", "inbound", "photo.png", "image/png", minute: 1);
        await server.AddFileAsync("sent", "outbound", "report.pdf", "application/pdf", read: true, minute: 2);
        server.AddText("read-note", "inbound", "retained note", read: true, minute: 3);
        server.AddText("pending-note", "outbound", "pending note", minute: 4);

        using var page = await server.GetPageAsync("/vault?search=");
        var root = page.RootElement;
        var items = root.GetProperty("items");

        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.False(root.GetProperty("hasMore").GetBoolean());
        AssertIds(items, "pending-note", "read-note", "sent", "incoming");
        Assert.Equal("outbound", items[2].GetProperty("direction").GetString());
        Assert.True(items[2].GetProperty("read").GetBoolean());
        Assert.Equal("inbound", items[3].GetProperty("direction").GetString());
        Assert.False(items[3].GetProperty("read").GetBoolean());
        Assert.Equal("photo.png", items[3].GetProperty("name").GetString());
        Assert.Equal("image/png", items[3].GetProperty("contentType").GetString());
        Assert.Equal(3, items[3].GetProperty("sizeBytes").GetInt64());
        Assert.Equal(inbound.TimestampUtc, items[3].GetProperty("timestampUtc").GetDateTimeOffset());
        Assert.True(items[1].GetProperty("isText").GetBoolean());
        Assert.Equal("retained note", items[1].GetProperty("textContent").GetString());
        Assert.Equal(JsonValueKind.Null, items[3].GetProperty("textContent").ValueKind);
        var fields = new[] { "id", "direction", "name", "contentType", "sizeBytes", "timestampUtc", "read", "isText", "textContent", "available" };
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal(fields.OrderBy(name => name), item.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
            Assert.True(item.GetProperty("available").GetBoolean());
            Assert.False(item.TryGetProperty("filePath", out _));
        }
        Assert.DoesNotContain(server.DataDirectory, root.GetRawText());
        server.AssertRead("incoming", false);
        server.AssertRead("sent", true);
        server.AssertRead("read-note", true);
        server.AssertRead("pending-note", false);
    }

    [Fact]
    public async Task GetVault_EmptyHistory_ReturnsEmptyPage()
    {
        await using var server = await VaultServer.StartAsync();

        using var page = await server.GetPageAsync("/vault");

        Assert.Empty(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, page.RootElement.GetProperty("total").GetInt32());
        Assert.False(page.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("direction=inbound", "svg,pdf,photo")]
    [InlineData("direction=outbound", "note,sent-photo")]
    [InlineData("kind=image", "svg,sent-photo,photo")]
    [InlineData("kind=file", "pdf")]
    [InlineData("kind=text", "note")]
    public async Task GetVault_FiltersDirectionAndKind(string query, string expectedIds)
    {
        await using var server = await VaultServer.StartAsync();
        await server.AddFileAsync("photo", "inbound", "photo.jpg", "image/jpeg", minute: 1);
        await server.AddFileAsync("sent-photo", "outbound", "sent.PNG", "application/octet-stream", minute: 2);
        await server.AddFileAsync("pdf", "inbound", "report.pdf", "application/pdf", minute: 3);
        server.AddText("note", "outbound", "note", minute: 4);
        await server.AddFileAsync("svg", "inbound", "drawing.svg", "image/svg+xml", minute: 5);

        using var page = await server.GetPageAsync($"/vault?{query}");

        AssertIds(page.RootElement.GetProperty("items"), expectedIds.Split(','));
        Assert.Equal(expectedIds.Split(',').Length, page.RootElement.GetProperty("total").GetInt32());
        Assert.False(page.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task GetVault_SearchesNamesAndTextCaseInsensitively_ButNotPathsOrMime()
    {
        await using var server = await VaultServer.StartAsync();
        await server.AddFileAsync("name-match", "inbound", "Needle.pdf", "application/pdf", minute: 1);
        server.AddText("body-match", "outbound", "A nEeDlE in the body", minute: 2);
        var pathOnly = await server.AddFileAsync("path-only", "inbound", "unrelated.pdf", "application/needle");
        server.Index.Add(new HistoryItem
        {
            Id = "hidden-path", Direction = "inbound", Name = "other.pdf", ContentType = "application/pdf",
            TimestampUtc = DateTimeOffset.UtcNow, FilePath = pathOnly.FilePath + "needle"
        });

        using var page = await server.GetPageAsync("/vault?search=NEEDLE");

        AssertIds(page.RootElement.GetProperty("items"), "body-match", "name-match");
        Assert.Equal(2, page.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetVault_CombinesFiltersBeforeOffsetPagination()
    {
        await using var server = await VaultServer.StartAsync();
        server.AddText("oldest", "outbound", "needle", minute: 1);
        server.AddText("middle", "outbound", "NEEDLE", minute: 2);
        server.AddText("newest", "outbound", "a needle", read: true, minute: 3);
        server.AddText("wrong-direction", "inbound", "needle", minute: 4);
        server.AddText("wrong-search", "outbound", "unrelated", minute: 5);
        await server.AddFileAsync("wrong-kind", "outbound", "needle.pdf", "application/pdf", minute: 6);

        using var page = await server.GetPageAsync("/vault?search=needle&direction=outbound&kind=text&offset=1&limit=1");

        AssertIds(page.RootElement.GetProperty("items"), "middle");
        Assert.Equal(3, page.RootElement.GetProperty("total").GetInt32());
        Assert.True(page.RootElement.GetProperty("hasMore").GetBoolean());
        using var last = await server.GetPageAsync("/vault?search=needle&direction=outbound&kind=text&offset=2&limit=1");
        AssertIds(last.RootElement.GetProperty("items"), "oldest");
        Assert.False(last.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task GetVault_DefaultsTo40_CapsAt100_AndHandlesOffsetsBeyondEnd()
    {
        await using var server = await VaultServer.StartAsync();
        for (var i = 0; i < 105; i++) server.AddText($"item-{i}", "inbound", "note", minute: i);

        using var first = await server.GetPageAsync("/vault");
        var firstItems = first.RootElement.GetProperty("items");
        Assert.Equal(40, firstItems.GetArrayLength());
        Assert.Equal("item-104", firstItems[0].GetProperty("id").GetString());
        Assert.Equal("item-65", firstItems[39].GetProperty("id").GetString());
        Assert.Equal(105, first.RootElement.GetProperty("total").GetInt32());
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        using var capped = await server.GetPageAsync("/vault?limit=2147483647");
        Assert.Equal(100, capped.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(capped.RootElement.GetProperty("hasMore").GetBoolean());
        using var last = await server.GetPageAsync("/vault?offset=100&limit=100");
        AssertIds(last.RootElement.GetProperty("items"), "item-4", "item-3", "item-2", "item-1", "item-0");
        Assert.False(last.RootElement.GetProperty("hasMore").GetBoolean());
        using var beyond = await server.GetPageAsync("/vault?offset=2147483647");
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(105, beyond.RootElement.GetProperty("total").GetInt32());
        Assert.False(beyond.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=abc")]
    [InlineData("offset=")]
    [InlineData("offset=2147483648")]
    [InlineData("limit=0")]
    [InlineData("limit=-1")]
    [InlineData("limit=abc")]
    [InlineData("limit=")]
    [InlineData("limit=2147483648")]
    [InlineData("direction=sideways")]
    [InlineData("direction=")]
    [InlineData("kind=video")]
    [InlineData("kind=")]
    [InlineData("offset=0&offset=1")]
    [InlineData("limit=1&limit=2")]
    [InlineData("direction=inbound&direction=outbound")]
    [InlineData("kind=image&kind=file")]
    [InlineData("search=one&search=two")]
    public async Task GetVault_InvalidQuery_ReturnsHelpful400(string query)
    {
        await using var server = await VaultServer.StartAsync();

        using var response = await server.Client.GetAsync($"/vault?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains(query.Split('=')[0], error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(server.DataDirectory, error);
    }

    [Theory]
    [InlineData("inbound", false)]
    [InlineData("inbound", true)]
    [InlineData("outbound", false)]
    [InlineData("outbound", true)]
    public async Task GetContent_IsRepeatableAttachment_WithoutChangingRead(string direction, bool read)
    {
        await using var server = await VaultServer.StartAsync();
        var bytes = new byte[] { 0, 255, 42 };
        var item = await server.AddFileAsync("file", direction, "report.pdf", "application/pdf", bytes, read);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await server.Client.GetAsync($"/vault/{item.Id}/content");
            response.EnsureSuccessStatusCode();
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            server.AssertRead(item.Id, read);
        }
    }

    [Theory]
    [InlineData("inbound")]
    [InlineData("outbound")]
    public async Task GetContent_ZeroByteFile_IsAvailableAndDownloadable(string direction)
    {
        await using var server = await VaultServer.StartAsync();
        var item = await server.AddFileAsync("empty", direction, "empty.txt", "text/plain", Array.Empty<byte>());

        using var page = await server.GetPageAsync("/vault");
        Assert.True(page.RootElement.GetProperty("items")[0].GetProperty("available").GetBoolean());
        using var response = await server.Client.GetAsync($"/vault/{item.Id}/content");
        response.EnsureSuccessStatusCode();
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        server.AssertRead(item.Id, false);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("preview")]
    public async Task GetFile_UnknownTextMissingPathOrMissingFile_Returns404(string endpoint)
    {
        await using var server = await VaultServer.StartAsync();
        server.AddText("note", "inbound", "note");
        server.Index.Add(new HistoryItem
        {
            Id = "no-path", Direction = "outbound", Name = "missing.png", ContentType = "image/png",
            TimestampUtc = DateTimeOffset.UtcNow
        });
        var missing = await server.AddFileAsync("missing-file", "outbound", "missing.png", "image/png");
        File.Delete(missing.FilePath!);

        foreach (var id in new[] { "unknown", "note", "no-path", "missing-file" })
        {
            using var response = await server.Client.GetAsync($"/vault/{id}/{endpoint}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var page = await server.GetPageAsync("/vault");
        foreach (var item in page.RootElement.GetProperty("items").EnumerateArray())
            Assert.Equal(item.GetProperty("isText").GetBoolean(), item.GetProperty("available").GetBoolean());
        server.AssertRead("missing-file", false);
    }

    [Theory]
    [InlineData("inbound", "image/jpeg", "photo.jpg")]
    [InlineData("outbound", "image/png", "photo.png")]
    [InlineData("inbound", "image/gif", "photo.gif")]
    [InlineData("outbound", "image/webp", "photo.webp")]
    [InlineData("inbound", "image/bmp", "photo.bmp")]
    [InlineData("outbound", "image/avif", "photo.avif")]
    public async Task GetPreview_SafeRaster_IsInlineWithoutChangingRead(string direction, string type, string name)
    {
        await using var server = await VaultServer.StartAsync();
        var bytes = new byte[] { 1, 2, 3 };
        var item = await server.AddFileAsync("image", direction, name, type, bytes);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await server.Client.GetAsync($"/vault/{item.Id}/preview");
            response.EnsureSuccessStatusCode();
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            server.AssertRead(item.Id, false);
        }
    }

    [Theory]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("photo.PNG", "image/png")]
    [InlineData("photo.gif", "image/gif")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("photo.bmp", "image/bmp")]
    [InlineData("photo.avif", "image/avif")]
    public async Task Vault_QueuedRasterWithGenericMime_UsesSafeExtensionType(string name, string expectedType)
    {
        await using var server = await VaultServer.StartAsync();
        var source = Path.Combine(server.DataDirectory, name);
        var bytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(source, bytes);
        var item = OutboxQueue.QueueFile(server.Index, server.Store, source);
        Assert.Equal("application/octet-stream", item.ContentType);

        using var page = await server.GetPageAsync("/vault?kind=image&direction=outbound");
        AssertIds(page.RootElement.GetProperty("items"), item.Id);
        Assert.Equal(expectedType, page.RootElement.GetProperty("items")[0].GetProperty("contentType").GetString());
        using var files = await server.GetPageAsync("/vault?kind=file");
        Assert.Empty(files.RootElement.GetProperty("items").EnumerateArray());
        using var preview = await server.Client.GetAsync($"/vault/{item.Id}/preview");
        preview.EnsureSuccessStatusCode();
        Assert.Equal(expectedType, preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", preview.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(bytes, await preview.Content.ReadAsByteArrayAsync());
        using var content = await server.Client.GetAsync($"/vault/{item.Id}/content");
        content.EnsureSuccessStatusCode();
        Assert.Equal("attachment", content.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(bytes, await content.Content.ReadAsByteArrayAsync());
        server.AssertRead(item.Id, false);
    }

    [Theory]
    [InlineData("image/svg+xml", "drawing.svg")]
    [InlineData("application/octet-stream", "drawing.svg")]
    [InlineData("application/octet-stream", "page.html")]
    [InlineData("application/octet-stream", "unknown.bin")]
    [InlineData("text/html", "photo.png")]
    [InlineData("text/html, image/png", "photo.png")]
    [InlineData("image/svg+xml", "photo.png")]
    [InlineData("application/xhtml+xml", "photo.jpg")]
    [InlineData("image/png", "drawing.svg")]
    [InlineData("image/png", "drawing.svgz")]
    [InlineData("image/jpeg", "page.html")]
    [InlineData("image/jpeg", "page.htm")]
    [InlineData("image/png", "page.xhtml")]
    [InlineData("image/png", "page.xml")]
    [InlineData("application/pdf", "report.pdf")]
    [InlineData("image/heic", "photo.heic")]
    public async Task GetPreview_RejectsActiveUnknownOrUnsupportedTypes(string type, string name)
    {
        await using var server = await VaultServer.StartAsync();
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>");
        var item = await server.AddFileAsync("unsafe", "outbound", name, type, bytes);

        using var preview = await server.Client.GetAsync($"/vault/{item.Id}/preview");

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        using var content = await server.Client.GetAsync($"/vault/{item.Id}/content");
        content.EnsureSuccessStatusCode();
        Assert.Equal("attachment", content.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(content.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(bytes, await content.Content.ReadAsByteArrayAsync());
        server.AssertRead(item.Id, false);
    }

    private static void AssertIds(JsonElement items, params string[] expected) =>
        Assert.Equal(expected, items.EnumerateArray().Select(item => item.GetProperty("id").GetString()));

    private sealed class VaultServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string DataDirectory { get; }
        public HttpClient Client { get; }
        public HistoryIndex Index => _app.Services.GetRequiredService<HistoryIndex>();
        public ItemStore Store => _app.Services.GetRequiredService<ItemStore>();

        private VaultServer(WebApplication app, string dataDirectory)
        {
            _app = app;
            DataDirectory = dataDirectory;
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            Client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
        }

        public static async Task<VaultServer> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "beam-vault-tests-" + Guid.NewGuid().ToString("N"));
            var app = BeamServerHost.Build(new BeamServerOptions(directory, "test-token", Port: 0),
                webHost => webHost.UseUrls("http://127.0.0.1:0"));
            await app.StartAsync();
            return new VaultServer(app, directory);
        }

        public HistoryItem AddText(string id, string direction, string text, bool read = false, int minute = 0) =>
            Index.Add(new HistoryItem
            {
                Id = id, Direction = direction, Name = "Text", ContentType = "text/plain",
                SizeBytes = Encoding.UTF8.GetByteCount(text), TimestampUtc = Timestamp(minute),
                Read = read, IsText = true, TextContent = text
            });

        public async Task<HistoryItem> AddFileAsync(string id, string direction, string name, string type,
            byte[]? bytes = null, bool read = false, int minute = 0)
        {
            bytes ??= new byte[] { 1, 2, 3 };
            using var stream = new MemoryStream(bytes);
            var path = await Store.SaveFileAsync(direction, name, stream);
            return Index.Add(new HistoryItem
            {
                Id = id, Direction = direction, Name = name, ContentType = type, SizeBytes = bytes.Length,
                TimestampUtc = Timestamp(minute), Read = read, FilePath = path
            });
        }

        public async Task<JsonDocument> GetPageAsync(string route)
        {
            using var response = await Client.GetAsync(route);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        public void AssertRead(string id, bool expected)
        {
            Assert.Equal(expected, Index.Get(id)!.Read);
            Assert.Equal(expected, new HistoryIndex(Path.Combine(DataDirectory, "history.json")).Get(id)!.Read);
        }

        private static DateTimeOffset Timestamp(int minute) =>
            new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero).AddMinutes(minute);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}
