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
    public string? TransferId { get; init; }
    public string? TransferFingerprint { get; init; }
}
