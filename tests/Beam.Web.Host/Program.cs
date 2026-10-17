using Beam.Server;
using Beam.Server.Storage;
using Beam.Server.Security;

// Isolated integration fixture: no desktop, real user data, or production token.
var directory = args.Single();
await using var app = BeamServerHost.Build(new(directory, "browser-test-token", 0), host => host.UseUrls("http://127.0.0.1:0"));
var index = app.Services.GetRequiredService<HistoryIndex>();
var store = app.Services.GetRequiredService<ItemStore>();
var sample = Path.Combine(directory, "from-pc.txt");
Directory.CreateDirectory(directory);
await File.WriteAllTextAsync(sample, "Bytes from the PC");
OutboxQueue.QueueFile(index, store, sample);
OutboxQueue.QueueText(index, "Note from the PC");
await app.StartAsync();
Console.WriteLine("BEAM_TEST_URL=" + app.Urls.Single());
while (await Console.In.ReadLineAsync() is { } command && command != "stop")
{
    if (command == "rotate")
    {
        // Private test-process control, never an HTTP route or production token.
        var token = await app.Services.GetRequiredService<PairingTokenStore>().RotateAsync();
        Console.WriteLine("BEAM_TEST_ROTATED=" + token);
    }
}
await app.StopAsync();
