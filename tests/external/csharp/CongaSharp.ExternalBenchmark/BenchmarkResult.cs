namespace CongaSharp.ExternalBenchmark;

/// <summary>
/// Result of a single benchmark scenario run.
/// </summary>
internal sealed record BenchmarkResult
{
    public required string Mode { get; init; }
    public required int PayloadSize { get; init; }
    public required string Pattern { get; init; }
    public required int Compression { get; init; }
    public required string CompressionName { get; init; }
    public required int CompressionLevel { get; init; }
    public required int WarmupIterations { get; init; }
    public required int MeasuredIterations { get; init; }
    public required double LatencyP50Ms { get; init; }
    public required double LatencyP95Ms { get; init; }
    public required double ThroughputP50MBs { get; init; }
    public required double ThroughputP95MBs { get; init; }
    public required double MessagesP50S { get; init; }
    public required double MessagesP95S { get; init; }
    public string? ClientName { get; init; }
    public string? ServerName { get; init; }
    public int ProgressEvents { get; init; }
}
