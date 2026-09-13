# Beam Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Beam — a token-gated, LAN-only HTTP server embedded in a Windows tray app (with an animated floating widget) that exchanges text/files/photos/videos with an iPhone via an Apple Shortcut (phone→PC) and an installable PWA (PC→phone).

**Architecture:** A .NET class library (`Beam.Server`) implements the HTTP contract, token auth, and JSON-indexed file storage, fully covered by integration tests run over real loopback sockets. A WPF app (`Beam.App`) hosts that server in-process and adds the tray icon, floating pill widget, pairing/QR screen, and drag-and-drop send. A static PWA (served by `Beam.Server`'s `wwwroot`) and a manually-configured iOS Shortcut are the two phone-side artifacts.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs (Kestrel), xUnit, WPF, Hardcodet.NotifyIcon.Wpf (tray icon), QRCoder (pairing QR), Makaretu.Dns (mDNS advertisement), vanilla HTML/CSS/JS for the PWA, Apple Shortcuts (no code) for the iOS share action.

**Spec:** `docs/superpowers/specs/2026-09-13-beam-design.md`

## Global Constraints

- Same-WiFi only; no relay server, no internet-facing component, no user accounts (spec: Constraints).
- Exactly one iPhone + one PC; no multi-device pairing UI (spec: Constraints).
- No custom-compiled iOS app or iOS widgets — phone-side send is via Apple Shortcuts only (spec: Constraints).
- Server uses plain HTTP, not HTTPS — LAN-only, and a self-signed cert would complicate the Shortcut's "Get Contents of URL" action and the PWA's fetch calls for no real security benefit on a trusted home network (implementation decision, consistent with spec's "lightweight shared-secret" security model).
- Default port: `8787`. Data directory: `%UserProfile%\Documents\Beam`, containing `Inbox\`, `Outbox\`, `history.json`, and `token.txt` (spec: Storage & History).
- History is capped to the last 50 items or 7 days, whichever is smaller, applied automatically whenever an item is added (spec: Storage & History default).
- Every non-`/app` request must carry a valid `X-Beam-Token` header or receive `401` (spec: Pairing & Security).
- `/receive` must accept a batch (multiple files and/or text) in one request (spec: HTTP Contract, amended).
- Text items support a "copy to clipboard" action on both ends instead of requiring a file download (spec: Data Flow, amended).

---

## File Structure

```
Beam.sln
src/
  Beam.Server/
    Beam.Server.csproj
    BeamServerOptions.cs
    BeamServerHost.cs
    Security/
      PairingToken.cs
      TokenAuthMiddleware.cs
    Storage/
      HistoryItem.cs
      HistoryIndex.cs
      ItemStore.cs
      OutboxQueue.cs
    Endpoints/
      ReceiveEndpoints.cs
      OutboxEndpoints.cs
    wwwroot/
      index.html
      app.js
      styles.css
      manifest.webmanifest
  Beam.App/
    Beam.App.csproj
    App.xaml
    App.xaml.cs
    TrayIconService.cs
    PillWindow.xaml
    PillWindow.xaml.cs
    ExpandedPanel.xaml
    ExpandedPanel.xaml.cs
    HistoryViewModel.cs
    PairingWindow.xaml
    PairingWindow.xaml.cs
tests/
  Beam.Server.Tests/
    Beam.Server.Tests.csproj
    PairingTokenTests.cs
    TokenAuthMiddlewareTests.cs
    HistoryIndexTests.cs
    ItemStoreTests.cs
    OutboxQueueTests.cs
    ReceiveEndpointsTests.cs
    OutboxEndpointsTests.cs
docs/
  setup/
    ios-shortcut-setup.md
```

Each backend file has one responsibility: `PairingToken` only generates/persists the token, `HistoryIndex` only tracks metadata, `ItemStore` only reads/writes bytes to disk, `OutboxQueue` is the one place that turns a "send this to phone" action into a stored file + history entry, and each `Endpoints` file maps exactly the routes named in its filename.

---

### Task 1: Project scaffolding + PairingToken

**Files:**
- Create: `Beam.sln`
- Create: `src/Beam.Server/Beam.Server.csproj`
- Create: `tests/Beam.Server.Tests/Beam.Server.Tests.csproj`
- Create: `src/Beam.Server/Security/PairingToken.cs`
- Test: `tests/Beam.Server.Tests/PairingTokenTests.cs`

**Interfaces:**
- Produces: `PairingToken.LoadOrCreate(string dataDirectory) : string`

- [ ] **Step 1: Create the solution and projects**

```bash
cd "D:/PROJECTS/Beam"
dotnet new sln -n Beam
dotnet new classlib -n Beam.Server -o src/Beam.Server -f net8.0
dotnet new xunit -n Beam.Server.Tests -o tests/Beam.Server.Tests -f net8.0
dotnet sln add src/Beam.Server/Beam.Server.csproj tests/Beam.Server.Tests/Beam.Server.Tests.csproj
dotnet add tests/Beam.Server.Tests/Beam.Server.Tests.csproj reference src/Beam.Server/Beam.Server.csproj
dotnet add src/Beam.Server/Beam.Server.csproj package Microsoft.AspNetCore.App --version 8.0.0 2>/dev/null || true
```

Edit `src/Beam.Server/Beam.Server.csproj` so the `<Project>` tag reads `Sdk="Microsoft.NET.Sdk.Web"` (needed for Kestrel/minimal APIs in a class library):

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Delete the placeholder `Class1.cs` files `dotnet new` generated in both projects.

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Beam.Server.Tests/PairingTokenTests.cs
using Beam.Server.Security;

namespace Beam.Server.Tests;

public class PairingTokenTests
{
    [Fact]
    public void LoadOrCreate_CreatesAndPersistsToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var first = PairingToken.LoadOrCreate(dir);
            var second = PairingToken.LoadOrCreate(dir);

            Assert.False(string.IsNullOrWhiteSpace(first));
            Assert.Equal(first, second); // same directory => same token on reload
            Assert.True(File.Exists(Path.Combine(dir, "token.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadOrCreate_DifferentDirectories_ProduceDifferentTokens()
    {
        var dirA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var dirB = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            var tokenA = PairingToken.LoadOrCreate(dirA);
            var tokenB = PairingToken.LoadOrCreate(dirB);
            Assert.NotEqual(tokenA, tokenB);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj`
Expected: FAIL — `PairingToken` does not exist.

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/Beam.Server/Security/PairingToken.cs
using System.Security.Cryptography;

namespace Beam.Server.Security;

public static class PairingToken
{
    public static string LoadOrCreate(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, "token.txt");

        if (File.Exists(tokenPath))
        {
            var existing = File.ReadAllText(tokenPath).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var bytes = RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        File.WriteAllText(tokenPath, token);
        return token;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj`
Expected: PASS (2 tests)

- [ ] **Step 6: Commit**

```bash
git add Beam.sln src/Beam.Server tests/Beam.Server.Tests
git commit -m "feat: scaffold solution and add PairingToken"
```

---

### Task 2: TokenAuthMiddleware

**Files:**
- Create: `src/Beam.Server/BeamServerOptions.cs`
- Create: `src/Beam.Server/Security/TokenAuthMiddleware.cs`
- Test: `tests/Beam.Server.Tests/TokenAuthMiddlewareTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 directly (options carry a token string, not `PairingToken` itself).
- Produces: `record BeamServerOptions(string DataDirectory, string ExpectedToken, int Port)`; `class TokenAuthMiddleware` registered via `app.UseMiddleware<TokenAuthMiddleware>()`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Beam.Server.Tests/TokenAuthMiddlewareTests.cs
using Beam.Server;
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter TokenAuthMiddlewareTests`
Expected: FAIL — `BeamServerOptions`/`TokenAuthMiddleware` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Beam.Server/BeamServerOptions.cs
namespace Beam.Server;

public record BeamServerOptions(string DataDirectory, string ExpectedToken, int Port);
```

```csharp
// src/Beam.Server/Security/TokenAuthMiddleware.cs
namespace Beam.Server.Security;

public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, BeamServerOptions options)
    {
        var path = context.Request.Path;
        if (path == "/" || path.StartsWithSegments("/app"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Beam-Token", out var provided) ||
            provided.Count == 0 ||
            provided[0] != options.ExpectedToken)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or missing token");
            return;
        }

        await _next(context);
    }
}
```

(`using Microsoft.AspNetCore.Http;` is implicit via `ImplicitUsings` in the Web SDK.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter TokenAuthMiddlewareTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Beam.Server/BeamServerOptions.cs src/Beam.Server/Security/TokenAuthMiddleware.cs tests/Beam.Server.Tests/TokenAuthMiddlewareTests.cs
git commit -m "feat: add token-gated auth middleware"
```

---

### Task 3: HistoryItem + HistoryIndex

**Files:**
- Create: `src/Beam.Server/Storage/HistoryItem.cs`
- Create: `src/Beam.Server/Storage/HistoryIndex.cs`
- Test: `tests/Beam.Server.Tests/HistoryIndexTests.cs`

**Interfaces:**
- Produces:
  - `record HistoryItem { Id, Direction, Name, ContentType, SizeBytes, TimestampUtc, Read (settable), IsText, TextContent?, FilePath? }`
  - `class HistoryIndex(string indexFilePath)` with `Add(HistoryItem) : HistoryItem`, `List() : IReadOnlyList<HistoryItem>`, `ListUnread(string direction) : IReadOnlyList<HistoryItem>`, `Get(string id) : HistoryItem?`, `MarkRead(string id) : bool`, `Prune(int maxItems, TimeSpan maxAge) : void`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Beam.Server.Tests/HistoryIndexTests.cs
using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class HistoryIndexTests
{
    private static HistoryItem MakeItem(string id, string direction, bool read = false, DateTimeOffset? ts = null) =>
        new()
        {
            Id = id,
            Direction = direction,
            Name = $"item-{id}",
            ContentType = "text/plain",
            SizeBytes = 10,
            TimestampUtc = ts ?? DateTimeOffset.UtcNow,
            Read = read,
            IsText = true,
            TextContent = "hello"
        };

    private static string TempIndexPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void Add_ThenList_ReturnsItem()
    {
        var index = new HistoryIndex(TempIndexPath());
        index.Add(MakeItem("1", "inbound"));

        var all = index.List();
        Assert.Single(all);
        Assert.Equal("1", all[0].Id);
    }

    [Fact]
    public void Add_Persists_AcrossNewInstance()
    {
        var path = TempIndexPath();
        new HistoryIndex(path).Add(MakeItem("1", "inbound"));

        var reloaded = new HistoryIndex(path);
        Assert.Single(reloaded.List());
    }

    [Fact]
    public void ListUnread_FiltersByDirectionAndReadState()
    {
        var index = new HistoryIndex(TempIndexPath());
        index.Add(MakeItem("1", "outbound", read: false));
        index.Add(MakeItem("2", "outbound", read: true));
        index.Add(MakeItem("3", "inbound", read: false));

        var unread = index.ListUnread("outbound");
        Assert.Single(unread);
        Assert.Equal("1", unread[0].Id);
    }

    [Fact]
    public void MarkRead_SetsReadTrue_AndPersists()
    {
        var path = TempIndexPath();
        var index = new HistoryIndex(path);
        index.Add(MakeItem("1", "outbound"));

        var result = index.MarkRead("1");

        Assert.True(result);
        Assert.True(new HistoryIndex(path).Get("1")!.Read);
    }

    [Fact]
    public void MarkRead_UnknownId_ReturnsFalse()
    {
        var index = new HistoryIndex(TempIndexPath());
        Assert.False(index.MarkRead("missing"));
    }

    [Fact]
    public void Prune_RemovesItemsBeyondMaxItemsOrMaxAge()
    {
        var index = new HistoryIndex(TempIndexPath());
        index.Add(MakeItem("old", "inbound", ts: DateTimeOffset.UtcNow.AddDays(-10)));
        index.Add(MakeItem("new", "inbound", ts: DateTimeOffset.UtcNow));

        index.Prune(maxItems: 50, maxAge: TimeSpan.FromDays(7));

        var remaining = index.List();
        Assert.Single(remaining);
        Assert.Equal("new", remaining[0].Id);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter HistoryIndexTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Beam.Server/Storage/HistoryItem.cs
namespace Beam.Server.Storage;

public class HistoryItem
{
    public required string Id { get; init; }
    public required string Direction { get; init; } // "inbound" or "outbound"
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public long SizeBytes { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public bool Read { get; set; }
    public bool IsText { get; init; }
    public string? TextContent { get; init; }
    public string? FilePath { get; init; }
}
```

```csharp
// src/Beam.Server/Storage/HistoryIndex.cs
using System.Text.Json;

namespace Beam.Server.Storage;

public class HistoryIndex
{
    private readonly string _indexFilePath;
    private readonly object _lock = new();
    private List<HistoryItem> _items;

    public HistoryIndex(string indexFilePath)
    {
        _indexFilePath = indexFilePath;
        _items = Load();
    }

    private const int DefaultMaxItems = 50;
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    public HistoryItem Add(HistoryItem item)
    {
        lock (_lock)
        {
            _items.Add(item);
            PruneLocked(DefaultMaxItems, DefaultMaxAge);
            Save();
            return item;
        }
    }

    public IReadOnlyList<HistoryItem> List()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(i => i.TimestampUtc).ToList();
        }
    }

    public IReadOnlyList<HistoryItem> ListUnread(string direction)
    {
        lock (_lock)
        {
            return _items
                .Where(i => i.Direction == direction && !i.Read)
                .OrderByDescending(i => i.TimestampUtc)
                .ToList();
        }
    }

    public HistoryItem? Get(string id)
    {
        lock (_lock)
        {
            return _items.FirstOrDefault(i => i.Id == id);
        }
    }

    public bool MarkRead(string id)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
            {
                return false;
            }

            item.Read = true;
            Save();
            return true;
        }
    }

    public void Prune(int maxItems, TimeSpan maxAge)
    {
        lock (_lock)
        {
            PruneLocked(maxItems, maxAge);
            Save();
        }
    }

    // Caller must hold _lock. Does not call Save() — callers that also
    // mutate _items (e.g. Add) save once after this returns.
    private void PruneLocked(int maxItems, TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        _items = _items
            .Where(i => i.TimestampUtc >= cutoff)
            .OrderByDescending(i => i.TimestampUtc)
            .Take(maxItems)
            .ToList();
    }

    private List<HistoryItem> Load()
    {
        if (!File.Exists(_indexFilePath))
        {
            return new List<HistoryItem>();
        }

        var json = File.ReadAllText(_indexFilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<HistoryItem>();
        }

        return JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? new List<HistoryItem>();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_indexFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_items);
        File.WriteAllText(_indexFilePath, json);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter HistoryIndexTests`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Beam.Server/Storage/HistoryItem.cs src/Beam.Server/Storage/HistoryIndex.cs tests/Beam.Server.Tests/HistoryIndexTests.cs
git commit -m "feat: add HistoryItem and JSON-backed HistoryIndex"
```

---

### Task 4: ItemStore

**Files:**
- Create: `src/Beam.Server/Storage/ItemStore.cs`
- Test: `tests/Beam.Server.Tests/ItemStoreTests.cs`

**Interfaces:**
- Produces: `class ItemStore(string dataDirectory)` with `InboxDir`, `OutboxDir` (string properties), `SaveFileAsync(string direction, string fileName, Stream content) : Task<string>` (returns full saved path), `OpenRead(string filePath) : Stream`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Beam.Server.Tests/ItemStoreTests.cs
using System.Text;
using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class ItemStoreTests
{
    private static string TempDataDir() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    [Fact]
    public void Constructor_CreatesInboxAndOutboxDirectories()
    {
        var dir = TempDataDir();
        var store = new ItemStore(dir);

        Assert.True(Directory.Exists(store.InboxDir));
        Assert.True(Directory.Exists(store.OutboxDir));
    }

    [Fact]
    public async Task SaveFileAsync_Inbound_WritesUnderInboxDir()
    {
        var store = new ItemStore(TempDataDir());
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var savedPath = await store.SaveFileAsync("inbound", "note.txt", content);

        Assert.StartsWith(store.InboxDir, savedPath);
        Assert.EndsWith("note.txt", savedPath);
        Assert.Equal("hello", await File.ReadAllTextAsync(savedPath));
    }

    [Fact]
    public async Task SaveFileAsync_Outbound_WritesUnderOutboxDir()
    {
        var store = new ItemStore(TempDataDir());
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hi"));

        var savedPath = await store.SaveFileAsync("outbound", "clip.mp4", content);

        Assert.StartsWith(store.OutboxDir, savedPath);
    }

    [Fact]
    public async Task SaveFileAsync_TwoFilesWithSameName_DoNotCollide()
    {
        var store = new ItemStore(TempDataDir());
        using var contentA = new MemoryStream(Encoding.UTF8.GetBytes("a"));
        using var contentB = new MemoryStream(Encoding.UTF8.GetBytes("b"));

        var pathA = await store.SaveFileAsync("inbound", "same.txt", contentA);
        var pathB = await store.SaveFileAsync("inbound", "same.txt", contentB);

        Assert.NotEqual(pathA, pathB);
        Assert.Equal("a", await File.ReadAllTextAsync(pathA));
        Assert.Equal("b", await File.ReadAllTextAsync(pathB));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter ItemStoreTests`
Expected: FAIL — `ItemStore` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Beam.Server/Storage/ItemStore.cs
namespace Beam.Server.Storage;

public class ItemStore
{
    private readonly string _dataDirectory;

    public ItemStore(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(InboxDir);
        Directory.CreateDirectory(OutboxDir);
    }

    public string InboxDir => Path.Combine(_dataDirectory, "Inbox");
    public string OutboxDir => Path.Combine(_dataDirectory, "Outbox");

    public async Task<string> SaveFileAsync(string direction, string fileName, Stream content)
    {
        var dir = direction == "inbound" ? InboxDir : OutboxDir;
        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var fullPath = Path.Combine(dir, safeName);

        await using var fileStream = File.Create(fullPath);
        await content.CopyToAsync(fileStream);

        return fullPath;
    }

    public Stream OpenRead(string filePath) => File.OpenRead(filePath);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter ItemStoreTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Beam.Server/Storage/ItemStore.cs tests/Beam.Server.Tests/ItemStoreTests.cs
git commit -m "feat: add ItemStore for Inbox/Outbox file persistence"
```

---

### Task 5: OutboxQueue

**Files:**
- Create: `src/Beam.Server/Storage/OutboxQueue.cs`
- Test: `tests/Beam.Server.Tests/OutboxQueueTests.cs`

**Interfaces:**
- Consumes: `HistoryIndex.Add`, `ItemStore.SaveFileAsync` (Tasks 3, 4).
- Produces: `static class OutboxQueue` with `QueueFile(HistoryIndex index, ItemStore store, string sourceFilePath) : HistoryItem` and `QueueText(HistoryIndex index, string text) : HistoryItem`. This is the single place `Beam.App` calls to send something PC→phone (used by Task 11's drag-drop and hotkey handlers).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Beam.Server.Tests/OutboxQueueTests.cs
using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class OutboxQueueTests
{
    private static string TempDataDir() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    [Fact]
    public async Task QueueFile_CopiesFileAndAddsUnreadOutboundHistoryItem()
    {
        var sourceDir = TempDataDir();
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "photo.jpg");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 1, 2, 3 });

        var index = new HistoryIndex(Path.Combine(TempDataDir(), "history.json"));
        var store = new ItemStore(TempDataDir());

        var item = OutboxQueue.QueueFile(index, store, sourceFile);

        Assert.Equal("outbound", item.Direction);
        Assert.False(item.Read);
        Assert.False(item.IsText);
        Assert.Equal("photo.jpg", item.Name);
        Assert.StartsWith(store.OutboxDir, item.FilePath);
        Assert.True(File.Exists(item.FilePath));
        Assert.Single(index.ListUnread("outbound"));
    }

    [Fact]
    public void QueueText_AddsUnreadOutboundTextHistoryItem()
    {
        var index = new HistoryIndex(Path.Combine(TempDataDir(), "history.json"));

        var item = OutboxQueue.QueueText(index, "call mom back");

        Assert.Equal("outbound", item.Direction);
        Assert.True(item.IsText);
        Assert.Equal("call mom back", item.TextContent);
        Assert.Null(item.FilePath);
        Assert.Single(index.ListUnread("outbound"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter OutboxQueueTests`
Expected: FAIL — `OutboxQueue` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Beam.Server/Storage/OutboxQueue.cs
namespace Beam.Server.Storage;

public static class OutboxQueue
{
    public static HistoryItem QueueFile(HistoryIndex index, ItemStore store, string sourceFilePath)
    {
        using var sourceStream = File.OpenRead(sourceFilePath);
        var savedPath = store
            .SaveFileAsync("outbound", Path.GetFileName(sourceFilePath), sourceStream)
            .GetAwaiter()
            .GetResult();

        var item = new HistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Direction = "outbound",
            Name = Path.GetFileName(sourceFilePath),
            ContentType = "application/octet-stream",
            SizeBytes = new FileInfo(savedPath).Length,
            TimestampUtc = DateTimeOffset.UtcNow,
            Read = false,
            IsText = false,
            FilePath = savedPath
        };

        return index.Add(item);
    }

    public static HistoryItem QueueText(HistoryIndex index, string text)
    {
        var item = new HistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Direction = "outbound",
            Name = "Text",
            ContentType = "text/plain",
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
            TimestampUtc = DateTimeOffset.UtcNow,
            Read = false,
            IsText = true,
            TextContent = text
        };

        return index.Add(item);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter OutboxQueueTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Beam.Server/Storage/OutboxQueue.cs tests/Beam.Server.Tests/OutboxQueueTests.cs
git commit -m "feat: add OutboxQueue for PC-to-phone sends"
```

---

### Task 6: POST /receive endpoint (batch files + text)

**Files:**
- Create: `src/Beam.Server/Endpoints/ReceiveEndpoints.cs`
- Create: `src/Beam.Server/BeamServerHost.cs`
- Test: `tests/Beam.Server.Tests/ReceiveEndpointsTests.cs`

**Interfaces:**
- Consumes: `BeamServerOptions`, `TokenAuthMiddleware` (Task 2); `HistoryIndex`, `ItemStore` (Tasks 3, 4).
- Produces:
  - `static class ReceiveEndpoints` with `MapReceiveEndpoints(this WebApplication app) : void`, mapping `POST /receive`.
  - `static class BeamServerHost` with `Build(BeamServerOptions options, Action<IWebHostBuilder>? configureWebHost = null) : WebApplication` — the composition root every later task (and every test from here on) builds the app through.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Beam.Server.Tests/ReceiveEndpointsTests.cs
using System.Net.Http.Headers;
using Beam.Server;
using Beam.Server.Storage;

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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter ReceiveEndpointsTests`
Expected: FAIL — `BeamServerHost` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Beam.Server/BeamServerHost.cs
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

        // Serves wwwroot/index.html (and app.js/styles.css/manifest) under
        // /app once Task 13 adds content there. UseDefaultFiles must be
        // registered before UseStaticFiles for /app/ to resolve to index.html.
        var staticFileOptions = new StaticFileOptions { RequestPath = "/app" };
        app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/app" });
        app.UseStaticFiles(staticFileOptions);

        return app;
    }
}
```

```csharp
// src/Beam.Server/Endpoints/ReceiveEndpoints.cs
using Beam.Server.Storage;

namespace Beam.Server.Endpoints;

public static class ReceiveEndpoints
{
    public static void MapReceiveEndpoints(this WebApplication app)
    {
        app.MapPost("/receive", async (HttpRequest request, HistoryIndex index, ItemStore store) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected multipart/form-data");
            }

            var form = await request.ReadFormAsync();
            var createdIds = new List<string>();

            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var savedPath = await store.SaveFileAsync("inbound", file.FileName, stream);

                var item = new HistoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Direction = "inbound",
                    Name = file.FileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.Length,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Read = false,
                    IsText = false,
                    FilePath = savedPath
                };

                index.Add(item);
                createdIds.Add(item.Id);
            }

            var text = form["text"].ToString();
            if (!string.IsNullOrEmpty(text))
            {
                var item = new HistoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Direction = "inbound",
                    Name = "Text",
                    ContentType = "text/plain",
                    SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Read = false,
                    IsText = true,
                    TextContent = text
                };

                index.Add(item);
                createdIds.Add(item.Id);
            }

            return Results.Ok(new { ids = createdIds });
        });
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter ReceiveEndpointsTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Beam.Server/BeamServerHost.cs src/Beam.Server/Endpoints/ReceiveEndpoints.cs tests/Beam.Server.Tests/ReceiveEndpointsTests.cs
git commit -m "feat: add BeamServerHost and batch-capable POST /receive"
```

---

### Task 7: Outbox endpoints (GET /outbox, GET /outbox/{id}/content, POST /outbox/{id}/ack)

**Files:**
- Create: `src/Beam.Server/Endpoints/OutboxEndpoints.cs`
- Modify: `src/Beam.Server/BeamServerHost.cs` (add `app.MapOutboxEndpoints();`)
- Test: `tests/Beam.Server.Tests/OutboxEndpointsTests.cs`

**Interfaces:**
- Consumes: `HistoryIndex`, `ItemStore` (Tasks 3, 4), `OutboxQueue` (Task 5) — tests use `OutboxQueue` to seed outbound items.
- Produces: `static class OutboxEndpoints` with `MapOutboxEndpoints(this WebApplication app) : void`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Beam.Server.Tests/OutboxEndpointsTests.cs
using System.Text.Json;
using Beam.Server;
using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class OutboxEndpointsTests
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
    public async Task GetOutbox_ReturnsOnlyUnreadOutboundItems()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var index = app.Services.GetRequiredService<HistoryIndex>();
            OutboxQueue.QueueText(index, "unread note");
            var readItem = OutboxQueue.QueueText(index, "already read");
            index.MarkRead(readItem.Id);

            var response = await client.GetAsync("/outbox");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal("unread note", doc.RootElement[0].GetProperty("textContent").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetOutboxContent_ReturnsFileBytes()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var sourceFile = Path.Combine(options.DataDirectory, "source.txt");
            Directory.CreateDirectory(options.DataDirectory);
            await File.WriteAllTextAsync(sourceFile, "file body");

            var index = app.Services.GetRequiredService<HistoryIndex>();
            var store = app.Services.GetRequiredService<ItemStore>();
            var item = OutboxQueue.QueueFile(index, store, sourceFile);

            var response = await client.GetAsync($"/outbox/{item.Id}/content");
            response.EnsureSuccessStatusCode();
            Assert.Equal("file body", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task AckOutbox_MarksItemRead()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var index = app.Services.GetRequiredService<HistoryIndex>();
            var item = OutboxQueue.QueueText(index, "note");

            var response = await client.PostAsync($"/outbox/{item.Id}/ack", content: null);
            response.EnsureSuccessStatusCode();

            Assert.Empty(index.ListUnread("outbound"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetOutboxContent_UnknownId_Returns404()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var response = await client.GetAsync("/outbox/does-not-exist/content");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter OutboxEndpointsTests`
Expected: FAIL — `/outbox` routes return 404 (not mapped yet).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Beam.Server/Endpoints/OutboxEndpoints.cs
using Beam.Server.Storage;

namespace Beam.Server.Endpoints;

public static class OutboxEndpoints
{
    public static void MapOutboxEndpoints(this WebApplication app)
    {
        app.MapGet("/outbox", (HistoryIndex index) =>
        {
            var unread = index.ListUnread("outbound").Select(i => new
            {
                id = i.Id,
                name = i.Name,
                contentType = i.ContentType,
                sizeBytes = i.SizeBytes,
                timestampUtc = i.TimestampUtc,
                isText = i.IsText,
                textContent = i.IsText ? i.TextContent : null
            });

            return Results.Ok(unread);
        });

        app.MapGet("/outbox/{id}/content", (string id, HistoryIndex index, ItemStore store) =>
        {
            var item = index.Get(id);
            if (item is null || item.IsText || item.FilePath is null)
            {
                return Results.NotFound();
            }

            var stream = store.OpenRead(item.FilePath);
            return Results.Stream(stream, item.ContentType, item.Name);
        });

        app.MapPost("/outbox/{id}/ack", (string id, HistoryIndex index) =>
        {
            return index.MarkRead(id) ? Results.Ok() : Results.NotFound();
        });
    }
}
```

Update `BeamServerHost.Build` to call the new mapping:

```csharp
// src/Beam.Server/BeamServerHost.cs — inside Build, after app.MapReceiveEndpoints();
app.MapOutboxEndpoints();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --filter OutboxEndpointsTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj`
Expected: PASS (all tests across Tasks 1-7)

- [ ] **Step 6: Commit**

```bash
git add src/Beam.Server/Endpoints/OutboxEndpoints.cs src/Beam.Server/BeamServerHost.cs tests/Beam.Server.Tests/OutboxEndpointsTests.cs
git commit -m "feat: add outbox list/content/ack endpoints"
```

This completes the fully test-driven backend. `Beam.Server` is now a working, independently testable HTTP server — everything from here on is the Windows app shell, pairing, the PWA, and the iOS Shortcut, verified manually per the spec's Testing section (GUI/visual behavior isn't practical to unit test).

---

### Task 8: Beam.App scaffolding — tray icon hosting the server

**Files:**
- Create: `src/Beam.App/Beam.App.csproj`
- Create: `src/Beam.App/App.xaml`
- Create: `src/Beam.App/App.xaml.cs`
- Create: `src/Beam.App/TrayIconService.cs`
- Modify: `Beam.sln` (add Beam.App)

**Interfaces:**
- Consumes: `BeamServerOptions`, `BeamServerHost.Build`, `PairingToken.LoadOrCreate` (Tasks 1, 2, 6).
- Produces: a running WPF application with no visible main window, a tray icon, and the embedded server started on launch. Later tasks (9-13) add windows this app creates.

- [ ] **Step 1: Create the WPF project**

```bash
cd "D:/PROJECTS/Beam"
dotnet new wpf -n Beam.App -o src/Beam.App -f net8.0-windows
dotnet sln add src/Beam.App/Beam.App.csproj
dotnet add src/Beam.App/Beam.App.csproj reference src/Beam.Server/Beam.Server.csproj
dotnet add src/Beam.App/Beam.App.csproj package Hardcodet.NotifyIcon.Wpf --version 2.0.1
```

Delete the generated `MainWindow.xaml`/`MainWindow.xaml.cs` — the app has no conventional main window; the tray icon and pill window (Task 9) are the UI.

- [ ] **Step 2: Configure App.xaml with no StartupUri**

```xml
<!-- src/Beam.App/App.xaml -->
<Application x:Class="Beam.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
</Application>
```

(`ShutdownMode="OnExplicitShutdown"` keeps the app alive with only a tray icon and no windows open.)

- [ ] **Step 3: Wire startup: load token, build data dir, start server, show tray icon**

```csharp
// src/Beam.App/App.xaml.cs
using System.Windows;
using Beam.Server;
using Beam.Server.Security;

namespace Beam.App;

public partial class App : Application
{
    public static WebApplication ServerApp { get; private set; } = null!;
    public static BeamServerOptions Options { get; private set; } = null!;

    private TrayIconService? _trayIconService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Beam");
        var token = PairingToken.LoadOrCreate(dataDirectory);
        Options = new BeamServerOptions(dataDirectory, token, Port: 8787);

        ServerApp = BeamServerHost.Build(Options);
        await ServerApp.StartAsync();

        _trayIconService = new TrayIconService();
        _trayIconService.Initialize();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        await ServerApp.StopAsync();
        base.OnExit(e);
    }
}
```

```csharp
// src/Beam.App/TrayIconService.cs
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace Beam.App;

public class TrayIconService : IDisposable
{
    private TaskbarIcon? _taskbarIcon;

    public void Initialize()
    {
        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Beam",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = BuildContextMenu()
        };

        _taskbarIcon.TrayLeftMouseUp += (_, _) => TogglePill();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void TogglePill()
    {
        // Task 9 replaces this with real PillWindow show/hide logic.
    }

    public void Dispose()
    {
        _taskbarIcon?.Dispose();
    }
}
```

- [ ] **Step 4: Build and manually verify**

Run: `dotnet build Beam.sln`
Expected: builds with no errors.

Run: `dotnet run --project src/Beam.App/Beam.App.csproj`
Expected: no window appears, but a Beam icon appears in the system tray; right-click → Exit closes the app cleanly. Confirm `%UserProfile%\Documents\Beam\token.txt` was created.

- [ ] **Step 5: Commit**

```bash
git add Beam.sln src/Beam.App
git commit -m "feat: scaffold Beam.App tray host for the embedded server"
```

---

### Task 9: Floating pill widget (expand/collapse)

**Files:**
- Create: `src/Beam.App/PillWindow.xaml`
- Create: `src/Beam.App/PillWindow.xaml.cs`
- Modify: `src/Beam.App/TrayIconService.cs` (wire `TogglePill` to a real `PillWindow`)

**Interfaces:**
- Consumes: nothing from the server yet — this task is pure UI shell. Task 10 fills the expanded content.
- Produces: `class PillWindow : Window` with `public void ToggleExpanded()`.

- [ ] **Step 1: Define the pill window XAML**

```xml
<!-- src/Beam.App/PillWindow.xaml -->
<Window x:Class="Beam.App.PillWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Beam" Height="56" Width="56"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize">
    <Window.Resources>
        <Storyboard x:Key="ExpandStoryboard">
            <DoubleAnimation Storyboard.TargetProperty="Width" To="320" Duration="0:0:0.18"/>
            <DoubleAnimation Storyboard.TargetProperty="Height" To="420" Duration="0:0:0.18"/>
        </Storyboard>
        <Storyboard x:Key="CollapseStoryboard">
            <DoubleAnimation Storyboard.TargetProperty="Width" To="56" Duration="0:0:0.15"/>
            <DoubleAnimation Storyboard.TargetProperty="Height" To="56" Duration="0:0:0.15"/>
        </Storyboard>
    </Window.Resources>
    <Border x:Name="RootBorder" CornerRadius="16" Background="#FF1F1F1F"
            BorderThickness="2"
            MouseLeftButtonDown="RootBorder_MouseLeftButtonDown">
        <Border.BorderBrush>
            <!-- Named explicitly (not a StaticResource) so Task 11 can animate
                 its Color directly without hitting WPF's frozen-brush pitfall. -->
            <SolidColorBrush x:Name="RootBorderBrush" Color="#FF1F1F1F"/>
        </Border.BorderBrush>
        <Grid x:Name="ContentHost"/>
    </Border>
</Window>
```

- [ ] **Step 2: Implement drag + expand/collapse behavior**

```csharp
// src/Beam.App/PillWindow.xaml.cs
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Beam.App;

public partial class PillWindow : Window
{
    private bool _isExpanded;

    public PillWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
    }

    public void ToggleExpanded()
    {
        var storyboard = (Storyboard)FindResource(_isExpanded ? "CollapseStoryboard" : "ExpandStoryboard");
        _isExpanded = !_isExpanded;
        storyboard.Begin(this);
        // ContentHost stays empty until Task 10 introduces ExpandedPanel and
        // wires it in here — this task only proves the resize/expand shell.
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
}
```

- [ ] **Step 3: Wire the tray icon to create/toggle the single PillWindow instance**

```csharp
// src/Beam.App/TrayIconService.cs — replace the body of TogglePill and add a field
private PillWindow? _pillWindow;

private void TogglePill()
{
    _pillWindow ??= new PillWindow();

    if (!_pillWindow.IsVisible)
    {
        _pillWindow.Show();
    }

    _pillWindow.ToggleExpanded();
}
```

- [ ] **Step 4: Build and manually verify**

Run: `dotnet run --project src/Beam.App/Beam.App.csproj`
Expected: clicking the tray icon shows a small dark rounded pill near the bottom-right corner; clicking it again smoothly grows into a 320x420 panel (still empty — Task 10 fills it) and back down when toggled again. The pill can be dragged by its left mouse button.

- [ ] **Step 5: Commit**

```bash
git add src/Beam.App/PillWindow.xaml src/Beam.App/PillWindow.xaml.cs src/Beam.App/TrayIconService.cs
git commit -m "feat: add floating pill widget with expand/collapse animation"
```

---

### Task 10: History list + drag-and-drop send (ExpandedPanel)

**Files:**
- Create: `src/Beam.App/HistoryViewModel.cs`
- Create: `src/Beam.App/ExpandedPanel.xaml`
- Create: `src/Beam.App/ExpandedPanel.xaml.cs`
- Modify: `src/Beam.App/PillWindow.xaml.cs` (populate `ContentHost` with `ExpandedPanel` now that it exists)

**Interfaces:**
- Consumes: `HistoryIndex`, `ItemStore`, `OutboxQueue` (Tasks 3-5) via `App.ServerApp.Services`.
- Produces: `class HistoryEntryViewModel { Id, Direction, Name, IsText, TextContent, TimestampUtc, DisplayTime }`; `class HistoryViewModel : INotifyPropertyChanged` with `ObservableCollection<HistoryEntryViewModel> Entries` and `void Refresh()`.

- [ ] **Step 1: Implement the view model**

```csharp
// src/Beam.App/HistoryViewModel.cs
using System.Collections.ObjectModel;
using System.ComponentModel;
using Beam.Server.Storage;

namespace Beam.App;

public class HistoryEntryViewModel
{
    public required string Id { get; init; }
    public required string Direction { get; init; }
    public required string Name { get; init; }
    public required bool IsText { get; init; }
    public string? TextContent { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public string DisplayTime => TimestampUtc.ToLocalTime().ToString("t");
}

public class HistoryViewModel : INotifyPropertyChanged
{
    private readonly HistoryIndex _index;

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryViewModel(HistoryIndex index)
    {
        _index = index;
        Refresh();
    }

    public void Refresh()
    {
        Entries.Clear();
        foreach (var item in _index.List())
        {
            Entries.Add(new HistoryEntryViewModel
            {
                Id = item.Id,
                Direction = item.Direction,
                Name = item.Name,
                IsText = item.IsText,
                TextContent = item.TextContent,
                TimestampUtc = item.TimestampUtc
            });
        }
    }
}
```

- [ ] **Step 2: Build the panel UI (drop zone + list)**

```xml
<!-- src/Beam.App/ExpandedPanel.xaml -->
<UserControl x:Class="Beam.App.ExpandedPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             AllowDrop="True" Drop="ExpandedPanel_Drop">
    <DockPanel Margin="12">
        <Border DockPanel.Dock="Top" Height="80" CornerRadius="10" Background="#FF2A2A2A" Margin="0,0,0,8">
            <TextBlock Text="Drop files here to send to phone"
                       Foreground="White" HorizontalAlignment="Center" VerticalAlignment="Center"
                       TextWrapping="Wrap" TextAlignment="Center" Margin="8"/>
        </Border>
        <ListBox x:Name="HistoryList" Background="Transparent" BorderThickness="0"
                 MouseDoubleClick="HistoryList_MouseDoubleClick">
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <StackPanel Orientation="Horizontal" Margin="4">
                        <TextBlock Text="{Binding Direction}" Foreground="Gray" Width="60"/>
                        <TextBlock Text="{Binding Name}" Foreground="White" Width="160"/>
                        <TextBlock Text="{Binding DisplayTime}" Foreground="Gray"/>
                    </StackPanel>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </DockPanel>
</UserControl>
```

- [ ] **Step 3: Wire drag-drop-to-send and double-click-to-open/copy behavior**

```csharp
// src/Beam.App/ExpandedPanel.xaml.cs
using System.Windows;
using System.Windows.Controls;
using Beam.Server.Storage;

namespace Beam.App;

public partial class ExpandedPanel : UserControl
{
    private readonly HistoryViewModel _viewModel;

    public ExpandedPanel()
    {
        InitializeComponent();

        var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        _viewModel = new HistoryViewModel(index);
        HistoryList.ItemsSource = _viewModel.Entries;
    }

    private void ExpandedPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        var store = App.ServerApp.Services.GetRequiredService<ItemStore>();

        foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop)!)
        {
            OutboxQueue.QueueFile(index, store, path);
        }

        _viewModel.Refresh();
    }

    private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntryViewModel entry)
        {
            return;
        }

        if (entry.IsText && entry.TextContent is not null)
        {
            Clipboard.SetText(entry.TextContent);
        }
        else
        {
            var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
            var item = index.Get(entry.Id);
            if (item?.FilePath is not null)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
            }
        }
    }
}
```

Add `using Microsoft.Extensions.DependencyInjection;` to `ExpandedPanel.xaml.cs` (needed for `GetRequiredService`).

- [ ] **Step 4: Wire ExpandedPanel into the pill's expand/collapse**

Now that `ExpandedPanel` exists, update `PillWindow.xaml.cs`'s `ToggleExpanded()` to populate and clear it:

```csharp
// src/Beam.App/PillWindow.xaml.cs — replace ToggleExpanded()
public void ToggleExpanded()
{
    var storyboard = (Storyboard)FindResource(_isExpanded ? "CollapseStoryboard" : "ExpandStoryboard");
    _isExpanded = !_isExpanded;

    if (_isExpanded)
    {
        ContentHost.Children.Clear();
        ContentHost.Children.Add(new ExpandedPanel());
    }

    storyboard.Begin(this);

    if (!_isExpanded)
    {
        ContentHost.Children.Clear();
    }
}
```

- [ ] **Step 5: Manually verify**

Run: `dotnet run --project src/Beam.App/Beam.App.csproj`
Expected: expand the pill, drag a file from Explorer onto the drop zone — it appears in the history list labeled "outbound" and a copy lands in `Documents\Beam\Outbox`. Double-clicking a file entry opens Explorer to it; double-clicking a text entry (add one via a quick manual `OutboxQueue.QueueText` test call, or wait for Task 12's hotkey) copies it to the clipboard.

- [ ] **Step 6: Commit**

```bash
git add src/Beam.App/HistoryViewModel.cs src/Beam.App/ExpandedPanel.xaml src/Beam.App/ExpandedPanel.xaml.cs src/Beam.App/PillWindow.xaml.cs
git commit -m "feat: add history list and drag-and-drop send to ExpandedPanel"
```

---

### Task 11: Receive notifications + pill animation on inbound items

**Files:**
- Modify: `src/Beam.App/PillWindow.xaml` (add a glow-pulse storyboard)
- Modify: `src/Beam.App/PillWindow.xaml.cs` (poll/react to new inbound items)
- Modify: `src/Beam.App/Beam.App.csproj` (add toast notification package)

**Interfaces:**
- Consumes: `HistoryIndex.List()` (Task 3).
- Produces: `public void NotifyInboundReceived(HistoryItem item)` on `PillWindow`, called by a lightweight poller.

- [ ] **Step 1: Add the notification package**

```bash
dotnet add src/Beam.App/Beam.App.csproj package Microsoft.Toolkit.Uwp.Notifications --version 7.1.3
```

- [ ] **Step 2: Add a glow-pulse storyboard to the pill**

```xml
<!-- src/Beam.App/PillWindow.xaml — add inside Window.Resources -->
<Storyboard x:Key="GlowPulseStoryboard">
    <ColorAnimation Storyboard.TargetName="RootBorderBrush"
                     Storyboard.TargetProperty="Color"
                     From="#FF1F1F1F" To="#FF4FC3F7" Duration="0:0:0.25" AutoReverse="True"/>
</Storyboard>
```

(Targets the named `RootBorderBrush` resource from Task 9 directly — animating a brush reached via a property path off a XAML-declared `Border.BorderBrush` risks WPF's frozen-brush exception; a named, non-static-resource `SolidColorBrush` avoids that.)

- [ ] **Step 3: Poll HistoryIndex for new inbound items and react**

```csharp
// src/Beam.App/PillWindow.xaml.cs — add fields and a timer
using System.Windows.Threading;
using Beam.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;

// inside PillWindow class:
private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
private readonly HashSet<string> _seenInboundIds = new();

// call this from the constructor, after InitializeComponent():
private void StartPolling()
{
    var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
    foreach (var item in index.List().Where(i => i.Direction == "inbound"))
    {
        _seenInboundIds.Add(item.Id); // don't notify for items that existed before this run
    }

    _pollTimer.Tick += (_, _) =>
    {
        var newInbound = index.List()
            .Where(i => i.Direction == "inbound" && !_seenInboundIds.Contains(i.Id))
            .ToList();

        foreach (var item in newInbound)
        {
            _seenInboundIds.Add(item.Id);
            NotifyInboundReceived(item);
        }
    };
    _pollTimer.Start();
}

public void NotifyInboundReceived(HistoryItem item)
{
    var storyboard = (Storyboard)FindResource("GlowPulseStoryboard");
    storyboard.Begin(this);

    new ToastContentBuilder()
        .AddText("Beam")
        .AddText(item.IsText ? "New text received" : $"Received {item.Name}")
        .Show();

    if (ContentHost.Children.Count > 0 && ContentHost.Children[0] is ExpandedPanel panel)
    {
        panel.RefreshHistory();
    }
}
```

Call `StartPolling();` at the end of the `PillWindow()` constructor.

- [ ] **Step 4: Expose `RefreshHistory` on ExpandedPanel**

```csharp
// src/Beam.App/ExpandedPanel.xaml.cs — add a public method
public void RefreshHistory() => _viewModel.Refresh();
```

- [ ] **Step 5: Manually verify**

Run: `dotnet run --project src/Beam.App/Beam.App.csproj`. From another terminal, simulate a phone send:

```bash
curl -X POST http://127.0.0.1:8787/receive -H "X-Beam-Token: <token from Documents\Beam\token.txt>" -F "text=test from curl"
```

Expected: within 2 seconds, the pill flashes blue and a Windows toast reading "Received" appears; opening the panel shows the new "inbound" entry.

- [ ] **Step 6: Commit**

```bash
git add src/Beam.App
git commit -m "feat: animate pill and show toast on inbound receive"
```

---

### Task 12: Pairing screen — QR code + mDNS advertisement

**Files:**
- Create: `src/Beam.App/PairingWindow.xaml`
- Create: `src/Beam.App/PairingWindow.xaml.cs`
- Create: `src/Beam.App/MdnsAdvertiser.cs`
- Modify: `src/Beam.App/App.xaml.cs` (start advertiser, show pairing window on first run)

**Interfaces:**
- Consumes: `BeamServerOptions` (Task 2, via `App.Options`).
- Produces: `class MdnsAdvertiser` with `void Start(int port)` / `void Stop()`; `PairingWindow` displays a QR code encoding the PWA's URL (`http://<hostname>:<port>/app/`) plus the host/port/token as plain text below it for manual Shortcut setup.

- [ ] **Step 1: Add QRCoder and Makaretu.Dns packages**

```bash
dotnet add src/Beam.App/Beam.App.csproj package QRCoder --version 1.4.3
dotnet add src/Beam.App/Beam.App.csproj package Makaretu.Dns.Multicast --version 0.27.0
```

- [ ] **Step 2: Implement the mDNS advertiser**

```csharp
// src/Beam.App/MdnsAdvertiser.cs
using Makaretu.Dns;

namespace Beam.App;

public class MdnsAdvertiser
{
    private ServiceDiscovery? _discovery;

    public void Start(int port)
    {
        var service = new ServiceProfile("beam-pc", "_beam._tcp", (ushort)port);
        _discovery = new ServiceDiscovery();
        _discovery.Advertise(service);
    }

    public void Stop()
    {
        _discovery?.Dispose();
        _discovery = null;
    }
}
```

- [ ] **Step 3: Build the pairing window with QR code**

```csharp
// src/Beam.App/PairingWindow.xaml.cs
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Beam.App;

public partial class PairingWindow : Window
{
    public PairingWindow(string hostname, int port, string token)
    {
        InitializeComponent();

        // Encodes a real, openable URL (the PWA's setup page) rather than a
        // custom scheme — iOS has no handler registered for "beam://", so a
        // QR code pointing at one would fail to open when scanned.
        var payload = $"http://{hostname}:{port}/app/";
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrData);
        var pngBytes = qrCode.GetGraphic(10);

        var bitmap = new BitmapImage();
        using (var stream = new System.IO.MemoryStream(pngBytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        QrImage.Source = bitmap;
        TokenText.Text = $"Host: {hostname}   Port: {port}   Token: {token}";
    }
}
```

```xml
<!-- src/Beam.App/PairingWindow.xaml -->
<Window x:Class="Beam.App.PairingWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Pair Beam" Height="420" Width="360" Background="#FF1F1F1F">
    <StackPanel Margin="16" HorizontalAlignment="Center">
        <TextBlock Text="Scan to open Beam on your iPhone, then use the details below to set up the Send to Beam Shortcut" Foreground="White"
                   FontSize="14" Margin="0,0,0,12" TextWrapping="Wrap"/>
        <Image x:Name="QrImage" Width="240" Height="240"/>
        <TextBlock x:Name="TokenText" Foreground="Gray" FontSize="11"
                   Margin="0,12,0,0" TextWrapping="Wrap" TextAlignment="Center"/>
    </StackPanel>
</Window>
```

- [ ] **Step 4: Start advertising and offer pairing from the tray menu**

```csharp
// src/Beam.App/App.xaml.cs — add a field and start it in OnStartup, after ServerApp.StartAsync()
private readonly MdnsAdvertiser _advertiser = new();

// after `await ServerApp.StartAsync();`
_advertiser.Start(Options.Port);
```

```csharp
// src/Beam.App/App.xaml.cs — stop it in OnExit, before `await ServerApp.StopAsync();`
_advertiser.Stop();
```

```csharp
// src/Beam.App/TrayIconService.cs — add a menu item in BuildContextMenu, before the Exit item
var pairItem = new MenuItem { Header = "Show Pairing QR" };
pairItem.Click += (_, _) =>
{
    var window = new PairingWindow("beam-pc.local", App.Options.Port, App.Options.ExpectedToken);
    window.Show();
};
menu.Items.Add(pairItem);
```

- [ ] **Step 5: Manually verify**

Run: `dotnet run --project src/Beam.App/Beam.App.csproj`, right-click tray icon → "Show Pairing QR". Expected: a window shows a scannable QR code and the host/port/token as text. Scanning it with the iPhone's Camera app should prompt to open Safari at `http://beam-pc.local:8787/app/` (Task 13's PWA — a 404 is expected until that task exists, but the URL should attempt to open, proving the QR payload is valid). From another device on the same WiFi, `ping beam-pc.local` should resolve.

- [ ] **Step 6: Commit**

```bash
git add src/Beam.App
git commit -m "feat: add pairing QR window and mDNS advertisement"
```

---

### Task 13: PWA — outbox viewer served at /app

**Files:**
- Create: `src/Beam.Server/wwwroot/index.html`
- Create: `src/Beam.Server/wwwroot/app.js`
- Create: `src/Beam.Server/wwwroot/styles.css`
- Create: `src/Beam.Server/wwwroot/manifest.webmanifest`
- Modify: `src/Beam.Server/Beam.Server.csproj` (ensure `wwwroot` content is copied to output)

**Interfaces:**
- Consumes: `GET /outbox`, `GET /outbox/{id}/content`, `POST /outbox/{id}/ack` (Task 7).
- Produces: a static site reachable at `http://<host>:8787/app/` once the user pastes in the host/token shown on the pairing screen.

- [ ] **Step 1: Ensure static content is published**

```xml
<!-- src/Beam.Server/Beam.Server.csproj — add inside <Project> -->
<ItemGroup>
  <Content Include="wwwroot/**/*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Build the page shell**

```html
<!-- src/Beam.Server/wwwroot/index.html -->
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <link rel="manifest" href="manifest.webmanifest" />
  <link rel="stylesheet" href="styles.css" />
  <title>Beam</title>
</head>
<body>
  <header>
    <h1>Beam</h1>
  </header>
  <div id="setup" hidden>
    <label>PC token</label>
    <input id="tokenInput" type="text" placeholder="Paste token from pairing screen" />
    <button id="saveTokenButton">Save</button>
  </div>
  <ul id="items"></ul>
  <script src="app.js"></script>
</body>
</html>
```

- [ ] **Step 3: Implement outbox polling, download, and clipboard-copy**

```javascript
// src/Beam.Server/wwwroot/app.js
const tokenKey = "beam-token";

function getToken() {
  return localStorage.getItem(tokenKey);
}

function showSetupIfNeeded() {
  if (!getToken()) {
    document.getElementById("setup").hidden = false;
  }
}

document.getElementById("saveTokenButton").addEventListener("click", () => {
  const value = document.getElementById("tokenInput").value.trim();
  if (value) {
    localStorage.setItem(tokenKey, value);
    document.getElementById("setup").hidden = true;
    loadOutbox();
  }
});

async function loadOutbox() {
  const token = getToken();
  if (!token) {
    return;
  }

  const response = await fetch("/outbox", { headers: { "X-Beam-Token": token } });
  if (!response.ok) {
    return;
  }

  const items = await response.json();
  renderItems(items);
}

function renderItems(items) {
  const list = document.getElementById("items");
  list.innerHTML = "";

  for (const item of items) {
    const li = document.createElement("li");
    li.className = "item item-enter";

    if (item.isText) {
      const button = document.createElement("button");
      button.textContent = `Copy: ${item.textContent}`;
      button.addEventListener("click", () => copyText(item));
      li.appendChild(button);
    } else {
      const link = document.createElement("a");
      link.textContent = `Download ${item.name}`;
      link.href = `/outbox/${item.id}/content`;
      link.addEventListener("click", () => ack(item.id));
      li.appendChild(link);
    }

    list.appendChild(li);
  }
}

async function copyText(item) {
  await navigator.clipboard.writeText(item.textContent);
  await ack(item.id);
  loadOutbox();
}

async function ack(id) {
  const token = getToken();
  await fetch(`/outbox/${id}/ack`, { method: "POST", headers: { "X-Beam-Token": token } });
}

showSetupIfNeeded();
loadOutbox();
setInterval(loadOutbox, 4000);
```

- [ ] **Step 4: Style it and add the entrance animation**

```css
/* src/Beam.Server/wwwroot/styles.css */
:root { color-scheme: light dark; }

body {
  font-family: -apple-system, system-ui, sans-serif;
  margin: 0;
  padding: 16px;
  background: #111;
  color: #eee;
}

header h1 {
  font-size: 20px;
  margin: 0 0 16px;
}

#setup {
  display: flex;
  gap: 8px;
  margin-bottom: 16px;
}

#items {
  list-style: none;
  margin: 0;
  padding: 0;
}

.item {
  background: #1f1f1f;
  border-radius: 10px;
  padding: 12px;
  margin-bottom: 8px;
}

.item a, .item button {
  color: #4fc3f7;
  background: none;
  border: none;
  font-size: 15px;
  text-align: left;
  width: 100%;
  cursor: pointer;
}

.item-enter {
  animation: slide-in 0.2s ease-out;
}

@keyframes slide-in {
  from { transform: translateY(8px); opacity: 0; }
  to { transform: translateY(0); opacity: 1; }
}
```

- [ ] **Step 5: Add the install manifest**

```json
// src/Beam.Server/wwwroot/manifest.webmanifest
{
  "name": "Beam",
  "short_name": "Beam",
  "start_url": "/app/",
  "display": "standalone",
  "background_color": "#111111",
  "theme_color": "#111111"
}
```

- [ ] **Step 6: Manually verify**

Run: `dotnet run --project src/Beam.App/Beam.App.csproj`, then on the PC open `http://localhost:8787/app/`. Paste the token from the pairing screen. Drag a file onto the pill (Task 10) to queue an outbound item, and confirm it appears in the page within ~4 seconds with a slide-in animation, and downloading/copying works. Then repeat from the iPhone's Safari at `http://beam-pc.local:8787/app/`, and use Safari's Share button → "Add to Home Screen".

- [ ] **Step 7: Commit**

```bash
git add src/Beam.Server/wwwroot src/Beam.Server/Beam.Server.csproj
git commit -m "feat: add outbox PWA with download and clipboard-copy actions"
```

---

### Task 14: iOS Shortcut setup (phone → PC)

**Files:**
- Create: `docs/setup/ios-shortcut-setup.md`

**Interfaces:**
- Consumes: the host/port/token shown by `PairingWindow` (Task 12) and the `/receive` contract (Task 6). This task is configuration, not code — the Shortcuts app has no importable source file for a solo hardcoded shortcut, so it's built by hand on the device following the steps below.

- [ ] **Step 1: Write the exact build steps into the doc**

```markdown
<!-- docs/setup/ios-shortcut-setup.md -->
# Setting up "Send to Beam" on iPhone

Do this once, using the host/port/token shown by the PC app's
"Show Pairing QR" window.

1. Open the **Shortcuts** app → **+** (new shortcut).
2. Tap the shortcut's name at the top, rename it to **Send to Beam**.
3. Tap the settings icon (ⓘ) → enable **Show in Share Sheet** →
   under "Share Sheet Types" select **Files, Images, Text, Videos, URLs**.
4. Add action **Get Contents of URL**:
   - URL: `http://beam-pc.local:8787/receive` (use the host/port from
     the pairing screen if different).
   - Method: **POST**.
   - Request Body: **Form**.
   - Add a form field named `files`, type **File**, value: **Shortcut Input**.
   - Add a second form field named `text`, type **Text**, value:
     **Shortcut Input** (leave blank/skip when sharing files — Shortcuts
     only sends fields with a value).
   - Under **Headers**, add `X-Beam-Token` with the token value from the
     pairing screen.
5. Tap **Show More** on the Get Contents of URL action and enable
   **Allow Insecure HTTP Requests** (required since Beam uses plain HTTP
   on the LAN, per the spec's transport decision).
6. Done. From any app's Share Sheet, selecting one or more photos/files
   and tapping **Send to Beam** now POSTs them to your PC in one request.

## Sending plain text

To send typed text (not from another app's share sheet): open the
Shortcuts app → run **Send to Beam** directly → when prompted for input,
choose **Text** and type your note. (Shortcuts passes whatever input
type you give it straight through as Shortcut Input.)

## If it stops working

- "Could not connect": the PC app isn't running, or you're on a
  different network — check the tray icon is present on the PC.
- 401 error: the token changed (e.g. `token.txt` was deleted) — reopen
  "Show Pairing QR" on the PC and update the header value in step 4.
- Can't resolve `beam-pc.local`: some routers block mDNS — use the PC's
  local IP address (shown on the pairing screen) instead of the hostname.
```

- [ ] **Step 2: Build the Shortcut on-device and verify end-to-end**

With the PC app running: from Photos, select a photo → Share → **Send to Beam**. Expected: within a couple seconds, the PC's pill glows and a toast shows "Received"; the photo appears in `Documents\Beam\Inbox`. Repeat selecting 3 photos at once and confirm all 3 arrive as one batch (Task 6's batch behavior). Run the shortcut directly with typed text input and confirm it arrives as a text history entry.

- [ ] **Step 3: Commit**

```bash
git add docs/setup/ios-shortcut-setup.md
git commit -m "docs: add iOS Shortcut setup instructions for Send to Beam"
```

---

### Task 15: End-to-end verification pass

**Files:** none (verification only, against the finished system).

- [ ] **Step 1: Run the full automated suite one more time**

Run: `dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj`
Expected: all tests from Tasks 1-7 pass.

- [ ] **Step 2: Walk every flow in the spec's Data Flow section**

With the PC app running and the iPhone Shortcut + PWA set up:
- [ ] Share a single photo from Photos → arrives in PC history within seconds, pill animates, toast shows.
- [ ] Share 3 photos at once → all 3 arrive as one batch with one toast.
- [ ] Run the Shortcut with typed text → arrives as a text entry; double-click in the PC panel copies it to the PC clipboard.
- [ ] Drag a file onto the PC pill → appears in the PWA's list within ~4 seconds with the slide-in animation; tapping downloads it on the phone.
- [ ] Queue a text item from the PC (e.g. via the pill's history, once any text-send affordance exists) → the PWA shows a "Copy" button that puts it on the phone's clipboard.
- [ ] Turn off WiFi on the phone and try sharing → Shortcut shows its native "couldn't connect" error (no crash, no silent failure).
- [ ] Edit the token in the Shortcut to a wrong value and try sharing → PC server logs/returns 401, nothing is saved.
- [ ] Restart the PC app → tray icon and pill still work, previously received/sent history is still visible (proves `history.json` persistence).
- [ ] Confirm Windows Firewall's first-run prompt appeared once and allowing it made the server reachable from the phone.

- [ ] **Step 3: Commit any fixes found during verification**

If any step above fails, fix the relevant task's code, re-run its unit tests if applicable, and commit with a message describing the fix (e.g. `fix: handle empty text field in /receive`).
