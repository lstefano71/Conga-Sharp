using System.Text.Json;

namespace CongaSharp.ExternalBenchmark;

internal static class Program
{
    static int Main(string[] args)
    {
        var options = ParseArgs(args);
        if (options == null) return 1;

        if (!File.Exists(options.DllPath))
        {
            Console.Error.WriteLine($"DLL not found: {options.DllPath}");
            return 1;
        }

        CongaNative.RegisterResolver(options.DllPath);

        try
        {
            return options.Command switch
            {
                "functional" => RunFunctional(options),
                "benchmark" => RunBenchmark(options),
                "run-all" => RunAll(options),
                _ => ShowUsage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal: {ex}");
            return 1;
        }
    }

    private static int RunFunctional(Options options)
    {
        var scenarios = Scenario.Functional();
        var results = new List<BenchmarkResult>();

        for (int i = 0; i < scenarios.Count; i++)
        {
            var s = scenarios[i];
            Console.WriteLine($"[{i + 1}/{scenarios.Count}] FUNCTIONAL mode={s.Mode} size={s.PayloadSize} pattern={s.Pattern}");
            results.Add(BenchmarkRunner.RunPair(s, options.TimeoutMs));
        }

        var summary = new { kind = "functional", count = results.Count, results };
        if (!string.IsNullOrEmpty(options.FunctionalOut))
            File.WriteAllText(options.FunctionalOut, JsonSerializer.Serialize(summary, JsonOptions));

        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        Console.WriteLine($"\nFunctional: {results.Count}/{scenarios.Count} passed");
        return 0;
    }

    private static int RunBenchmark(Options options)
    {
        var scenarios = Scenario.Benchmark(options.Depth, options.FullCompression);
        var results = new List<BenchmarkResult>();

        for (int i = 0; i < scenarios.Count; i++)
        {
            var s = scenarios[i];
            Console.WriteLine(
                $"[{i + 1}/{scenarios.Count}] mode={s.Mode} size={s.PayloadSize} " +
                $"pattern={s.Pattern} compression={s.Compression} level={s.CompressionLevel}");
            results.Add(BenchmarkRunner.RunPair(s, options.TimeoutMs));
        }

        if (!string.IsNullOrEmpty(options.BenchmarkMd))
        {
            var md = MarkdownRenderer.Render(results, "External C-API Benchmarks (C# Harness)");
            File.WriteAllText(options.BenchmarkMd, md);
            Console.WriteLine($"Wrote {options.BenchmarkMd}");
        }

        if (!string.IsNullOrEmpty(options.BenchmarkOut))
        {
            var summary = new
            {
                kind = "benchmark",
                depth = options.Depth,
                full_compression = options.FullCompression,
                scenario_count = results.Count,
                results,
            };
            File.WriteAllText(options.BenchmarkOut, JsonSerializer.Serialize(summary, JsonOptions));
        }

        return 0;
    }

    private static int RunAll(Options options)
    {
        int rc = RunFunctional(options);
        if (rc != 0) return rc;
        return RunBenchmark(options);
    }

    private static int ShowUsage()
    {
        Console.WriteLine("""
            Usage: CongaSharp.ExternalBenchmark --dll <path> <command> [options]

            Commands:
              functional       Run functional coverage (one iteration per mode)
              benchmark        Run benchmark matrix with latency measurement
              run-all          Run functional then benchmark

            Options:
              --dll <path>           Path to published congasharp.dll (required)
              --timeout-ms <ms>      Timeout per operation (default: 60000)
              --depth <level>        smoke | balanced | deep (default: deep)
              --full-compression     Enable all compression combos (default: true)
              --benchmark-md <path>  Output markdown file
              --benchmark-out <path> Output JSON file (benchmark)
              --functional-out <path> Output JSON file (functional)
            """);
        return 1;
    }

    private static Options? ParseArgs(string[] args)
    {
        var options = new Options();
        int i = 0;

        // Find command (first non-option argument)
        var argsList = new List<string>(args);
        while (i < argsList.Count)
        {
            switch (argsList[i])
            {
                case "--dll":
                    options.DllPath = argsList[++i];
                    break;
                case "--timeout-ms":
                    options.TimeoutMs = int.Parse(argsList[++i]);
                    break;
                case "--depth":
                    options.Depth = argsList[++i];
                    break;
                case "--full-compression":
                    options.FullCompression = true;
                    break;
                case "--benchmark-md":
                    options.BenchmarkMd = argsList[++i];
                    break;
                case "--benchmark-out":
                    options.BenchmarkOut = argsList[++i];
                    break;
                case "--functional-out":
                    options.FunctionalOut = argsList[++i];
                    break;
                default:
                    if (argsList[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {argsList[i]}");
                        ShowUsage();
                        return null;
                    }
                    options.Command = argsList[i];
                    break;
            }
            i++;
        }

        if (string.IsNullOrEmpty(options.DllPath))
        {
            Console.Error.WriteLine("--dll is required");
            ShowUsage();
            return null;
        }
        if (string.IsNullOrEmpty(options.Command))
        {
            Console.Error.WriteLine("Command is required (functional | benchmark | run-all)");
            ShowUsage();
            return null;
        }

        return options;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

internal sealed class Options
{
    public string DllPath { get; set; } = "";
    public string Command { get; set; } = "";
    public int TimeoutMs { get; set; } = 60_000;
    public string Depth { get; set; } = "deep";
    public bool FullCompression { get; set; } = true;
    public string BenchmarkMd { get; set; } = "";
    public string BenchmarkOut { get; set; } = "";
    public string FunctionalOut { get; set; } = "";
}
