using System.Text;

namespace CongaSharp.ExternalBenchmark;

/// <summary>
/// Renders benchmark results as a markdown table matching the Python harness output.
/// </summary>
internal static class MarkdownRenderer
{
    public static string Render(List<BenchmarkResult> results, string title)
    {
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"Run timestamp: `{now}`");
        sb.AppendLine();
        sb.AppendLine("| Mode | Size | Pattern | Compression | Level | Thrpt p50 (MB/s) | Thrpt p95 (MB/s) | Msg/s p50 | Lat p50 (ms) | Lat p95 (ms) |");
        sb.AppendLine("|------|------|---------|-------------|-------|------------------:|------------------:|----------:|-------------:|-------------:|");

        foreach (var r in results)
        {
            var sizeLabel = FormatSize(r.PayloadSize);
            sb.AppendLine(
                $"| {r.Mode} | {sizeLabel} | {r.Pattern} | {r.CompressionName} | {r.CompressionLevel} | " +
                $"{r.ThroughputP50MBs:F1} | {r.ThroughputP95MBs:F1} | " +
                $"{r.MessagesP50S:F1} | {r.LatencyP50Ms:F2} | {r.LatencyP95Ms:F2} |");
        }

        return sb.ToString();
    }

    public static string FormatSize(int size) => size switch
    {
        >= 1024 * 1024 => $"{size / (1024 * 1024)} MB",
        >= 1024 => $"{size / 1024} KB",
        _ => $"{size} B"
    };
}
