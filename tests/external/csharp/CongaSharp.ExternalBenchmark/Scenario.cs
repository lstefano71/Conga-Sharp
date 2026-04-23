namespace CongaSharp.ExternalBenchmark;

/// <summary>
/// A single benchmark or functional test scenario (mirrors orchestrator.py's Scenario).
/// </summary>
internal sealed record Scenario(
    string Mode,
    int PayloadSize,
    string Pattern,
    int Compression,
    int CompressionLevel,
    int Warmup,
    int Measured)
{
    private static readonly Dictionary<int, int[]> CompressionLevels = new()
    {
        [0] = [0],            // None
        [1] = [1, 2, 3],     // Deflate
        [2] = [0, 6, 12],    // LZ4
        [3] = [1, 3, 10, 22] // Zstd
    };

    public static readonly Dictionary<int, string> CompressionNames = new()
    {
        [0] = "None",
        [1] = "Deflate",
        [2] = "LZ4",
        [3] = "Zstd"
    };

    public static List<Scenario> Functional() =>
    [
        new("Raw",     4096,       "Random",       0, 0, 0, 1),
        new("Text",    2048,       "Compressible", 0, 0, 0, 1),
        new("BlkRaw",  64 * 1024,  "Random",       2, 6, 0, 1),
        new("BlkText", 64 * 1024,  "Compressible", 3, 3, 0, 1),
        new("Command", 4096,       "Compressible", 1, 2, 0, 1),
    ];

    public static List<Scenario> Benchmark(string depth, bool fullCompression)
    {
        int[] sizes;
        int warmup, measured;

        switch (depth)
        {
            case "deep":
                sizes = [1024, 1024 * 100, 1024 * 1024];
                warmup = 5; measured = 30;
                break;
            case "balanced":
                sizes = [1024, 1024 * 100];
                warmup = 5; measured = 30;
                break;
            default: // smoke
                sizes = [1024, 1024 * 16];
                warmup = 5; measured = 30;
                break;
        }

        string[] modes = ["Raw", "Text", "BlkRaw", "BlkText", "Command"];
        string[] patterns = ["Random", "Compressible"];
        var scenarios = new List<Scenario>();

        foreach (var mode in modes)
        {
            bool isFramed = mode is "BlkRaw" or "BlkText" or "Command";
            foreach (var size in sizes)
            {
                foreach (var pattern in patterns)
                {
                    if (!isFramed)
                    {
                        scenarios.Add(new Scenario(mode, size, pattern, 0, 0, warmup, measured));
                        continue;
                    }

                    var levelsMap = fullCompression
                        ? CompressionLevels
                        : new Dictionary<int, int[]> { [0] = [0], [2] = [6] };

                    foreach (var (compression, levels) in levelsMap)
                    {
                        foreach (var level in levels)
                        {
                            scenarios.Add(new Scenario(mode, size, pattern, compression, level, warmup, measured));
                        }
                    }
                }
            }
        }

        return scenarios;
    }
}
