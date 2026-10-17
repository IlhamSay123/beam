using System.Collections.Concurrent;
using Beam.Server.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beam.Server.Tests;

public class PairingLoggingTests
{
    [Fact]
    public async Task ScanningOriginalAndRotatedQr_DoesNotLogEitherSecret()
    {
        var root = Path.Combine(Path.GetTempPath(), "beam-logging-" + Guid.NewGuid().ToString("N"));
        var original = PairingToken.LoadOrCreate(root);
        using var capture = new CapturedLogs();
        await using var app = BeamServerHost.Build(new(root, original, 0), host =>
        {
            host.UseUrls("http://127.0.0.1:0");
            host.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        });
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var firstQr = await client.GetAsync("/app/?token=" + original);
            firstQr.EnsureSuccessStatusCode();
            var replacement = await app.Services.GetRequiredService<PairingTokenStore>().RotateAsync();
            using var newQr = await client.GetAsync("/app/?token=" + replacement);
            newQr.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Add("X-Beam-Token", original);
            using var rejected = await client.GetAsync("/capabilities");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
            await app.StopAsync();
            var messages = string.Join('\n', capture.Messages);
            Assert.Contains("Authentication rejected", messages); // Useful diagnostics remain enabled.
            Assert.DoesNotContain(original, messages);
            Assert.DoesNotContain(replacement, messages);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
