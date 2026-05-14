using System.Diagnostics;

namespace CongaSharp.ExternalBenchmark;

/// <summary>
/// Runs a single benchmark scenario: spins up a server thread and a client
/// thread, each with its own conga_init handle, communicating through the
/// C ABI exactly as external consumers would.
/// </summary>
internal static class BenchmarkRunner
{
    private static readonly HashSet<string> DataEvents = ["Receive", "Block", "BlockLast"];
    private const string TextEomJson = "[[13,10]]";
    private static readonly byte[] TextEomBytes = [13, 10];

    public static BenchmarkResult RunPair(Scenario scenario, int timeoutMs)
    {
        int bufferSize = Math.Max(16384, scenario.PayloadSize + 1024);
        bool textMode = scenario.Mode == "Text";
        int iterations = scenario.Warmup + scenario.Measured;

        byte[] requestPayload = MakePayload(scenario.PayloadSize, scenario.Pattern, textMode);
        byte[] responsePayload = MakePayload(scenario.PayloadSize, "Compressible", textMode);
        byte[] progressPayload = textMode ? "progress-step\r\n"u8.ToArray() : "progress-step"u8.ToArray();

        // Shared state between threads
        int serverPort = 0;
        string serverName = "";
        string clientName = "";
        Exception? serverError = null;
        Exception? clientError = null;
        var serverReady = new ManualResetEventSlim(false);
        double[] latencies = new double[scenario.Measured];
        int progressEvents = 0;

        // ─── Server thread ────────────────────────────────────────
        var serverThread = new Thread(() =>
        {
            CongaApi? api = null;
            string srvName = "";
            try
            {
                api = new CongaApi();
                api.Init();
                srvName = api.CreateServer("", "", 0, scenario.Mode, bufferSize);

                if (scenario.Mode == "Text")
                    api.SetProp(srvName, "EOM", TextEomJson);

                api.StartServer(srvName);
                int localPort = int.Parse(api.GetProp(srvName, "LocalPort"));

                serverName = srvName;
                serverPort = localPort;
                serverReady.Set();

                // Wait for client connection
                var connectEvt = WaitForEvent(api, srvName, timeoutMs);
                if (connectEvt.EventName != "Connect")
                    throw new InvalidOperationException($"Expected Connect, got '{connectEvt.EventName}'");
                string connectionName = connectEvt.ObjectName;

                for (int i = 0; i < iterations; i++)
                {
                    var evt = WaitForEvent(api, srvName, timeoutMs);

                    if (scenario.Mode == "Command")
                    {
                        if (evt.EventName != "Receive")
                            throw new InvalidOperationException($"Expected Command Receive, got '{evt.EventName}'");
                        if (!evt.ObjectName.StartsWith(connectionName + ".",StringComparison.Ordinal))
                            throw new InvalidOperationException($"Unexpected command object '{evt.ObjectName}'");
                        if (!PayloadMatches(scenario.Mode, requestPayload, evt.Payload))
                            throw new InvalidOperationException("Server received unexpected command payload");

                        if (progressPayload.Length > 0 && scenario.Mode == "Command")
                            api.Progress(evt.ObjectName, progressPayload,
                                scenario.Compression, scenario.CompressionLevel);

                        api.Respond(evt.ObjectName, responsePayload,
                            scenario.Compression, scenario.CompressionLevel);
                    }
                    else
                    {
                        if (!DataEvents.Contains(evt.EventName))
                            throw new InvalidOperationException($"Expected data event, got '{evt.EventName}'");
                        if (evt.ObjectName != connectionName)
                            throw new InvalidOperationException($"Expected '{connectionName}', got '{evt.ObjectName}'");
                        if (!PayloadMatches(scenario.Mode, requestPayload, evt.Payload))
                            throw new InvalidOperationException("Server received unexpected payload");

                        api.Send(connectionName, responsePayload,
                            compression: scenario.Compression,
                            compressionLevel: scenario.CompressionLevel);
                    }
                }
            }
            catch (Exception ex)
            {
                serverError = ex;
                serverReady.Set(); // unblock client if server fails during startup
            }
            finally
            {
                if (api != null)
                {
                    try { if (srvName.Length > 0) api.Close(srvName); } catch { }
                    try { api.Shutdown(); } catch { }
                }
            }
        })
        { IsBackground = true, Name = "BenchServer" };

        // ─── Client thread ────────────────────────────────────────
        var clientThread = new Thread(() =>
        {
            CongaApi? api = null;
            string cltName = "";
            try
            {
                // Wait for server to be ready
                if (!serverReady.Wait(TimeSpan.FromMilliseconds(timeoutMs + 20_000)))
                    throw new TimeoutException("Server did not become ready");
                if (serverError != null)
                    throw new InvalidOperationException("Server failed during startup", serverError);

                api = new CongaApi();
                api.Init();
                cltName = api.CreateClient("", "127.0.0.1", serverPort, scenario.Mode, bufferSize);

                if (scenario.Mode == "Text")
                    api.SetProp(cltName, "EOM", TextEomJson);

                api.ConnectClient(cltName, timeoutMs);
                clientName = cltName;

                int progEvts = 0;

                for (int i = 0; i < iterations; i++)
                {
                    string commandTarget = scenario.Mode == "Command"
                        ? $"{cltName}.BenchCmd"
                        : cltName;

                    var sw = Stopwatch.StartNew();
                    api.Send(commandTarget, requestPayload,
                        compression: scenario.Compression,
                        compressionLevel: scenario.CompressionLevel);

                    bool gotFinal = false;
                    while (!gotFinal)
                    {
                        var evt = WaitForEvent(api, cltName, timeoutMs);

                        if (scenario.Mode != "Command" && !DataEvents.Contains(evt.EventName))
                            continue;

                        if (scenario.Mode == "Command")
                        {
                            if (evt.EventName == "Progress")
                            {
                                progEvts++;
                                continue;
                            }
                            if (evt.EventName != "Receive")
                                continue;
                            if (evt.ObjectName != commandTarget)
                                throw new InvalidOperationException(
                                    $"Expected '{commandTarget}', got '{evt.ObjectName}'");
                            if (!PayloadMatches(scenario.Mode, responsePayload, evt.Payload))
                                throw new InvalidOperationException("Client received unexpected response");
                            gotFinal = true;
                        }
                        else
                        {
                            if (!PayloadMatches(scenario.Mode, responsePayload, evt.Payload))
                                throw new InvalidOperationException("Client received unexpected response");
                            gotFinal = true;
                        }
                    }

                    sw.Stop();
                    if (i >= scenario.Warmup)
                        latencies[i - scenario.Warmup] = sw.Elapsed.TotalMilliseconds;
                }

                progressEvents = progEvts;
            }
            catch (Exception ex)
            {
                clientError = ex;
            }
            finally
            {
                if (api != null)
                {
                    try { if (cltName.Length > 0) api.Close(cltName); } catch { }
                    try { api.Shutdown(); } catch { }
                }
            }
        })
        { IsBackground = true, Name = "BenchClient" };

        // ─── Run both ─────────────────────────────────────────────
        serverThread.Start();
        clientThread.Start();

        int joinTimeout = timeoutMs + 30_000;
        clientThread.Join(joinTimeout);
        serverThread.Join(joinTimeout);

        if (serverError != null)
            throw new InvalidOperationException($"Server failed: {serverError.Message}", serverError);
        if (clientError != null)
            throw new InvalidOperationException($"Client failed: {clientError.Message}", clientError);

        // ─── Compute statistics ───────────────────────────────────
        double p50 = Percentile(latencies, 0.50);
        double p95 = Percentile(latencies, 0.95);

        return new BenchmarkResult
        {
            Mode = scenario.Mode,
            PayloadSize = scenario.PayloadSize,
            Pattern = scenario.Pattern,
            Compression = scenario.Compression,
            CompressionName = Scenario.CompressionNames[scenario.Compression],
            CompressionLevel = scenario.CompressionLevel,
            WarmupIterations = scenario.Warmup,
            MeasuredIterations = scenario.Measured,
            LatencyP50Ms = p50,
            LatencyP95Ms = p95,
            ThroughputP50MBs = ThroughputMBs(requestPayload.Length, p50),
            ThroughputP95MBs = ThroughputMBs(requestPayload.Length, p95),
            MessagesP50S = 1000.0 / p50,
            MessagesP95S = 1000.0 / p95,
            ClientName = clientName,
            ServerName = serverName,
            ProgressEvents = progressEvents,
        };
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static WaitResult WaitForEvent(CongaApi api, string filter, int timeoutMs)
    {
        var evt = api.Wait(filter, timeoutMs);
        return evt ?? throw new TimeoutException($"Timed out waiting for event on '{filter}'");
    }

    private static bool PayloadMatches(string mode, byte[] expected, byte[] received)
    {
        if (mode == "Text")
            return received.AsSpan().SequenceEqual(expected) ||
                   received.AsSpan().SequenceEqual([.. expected, .. TextEomBytes]);
        return received.AsSpan().SequenceEqual(expected);
    }

    private static byte[] MakePayload(int size, string pattern, bool textMode)
    {
        if (textMode)
        {
            byte[] data;
            if (pattern == "Compressible")
            {
                var baseData = new byte[256];
                Array.Fill(baseData, (byte)'A');
                data = new byte[size];
                for (int i = 0; i < size; i++)
                    data[i] = baseData[i % 256];
            }
            else
            {
                const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
                var rng = new Random(42 + size);
                data = new byte[size];
                for (int i = 0; i < size; i++)
                    data[i] = (byte)alphabet[rng.Next(alphabet.Length)];
            }
            // Append \r\n EOM
            var result = new byte[size + 2];
            data.CopyTo(result, 0);
            result[size] = (byte)'\r';
            result[size + 1] = (byte)'\n';
            return result;
        }

        if (pattern == "Compressible")
        {
            var baseData = new byte[256];
            Array.Fill(baseData, (byte)'A');
            var data = new byte[size];
            for (int i = 0; i < size; i++)
                data[i] = baseData[i % 256];
            return data;
        }
        else
        {
            var rng = new Random(42 + size);
            var data = new byte[size];
            rng.NextBytes(data);
            return data;
        }
    }

    private static double Percentile(double[] values, double fraction)
    {
        if (values.Length == 0) throw new InvalidOperationException("No values for percentile");
        var sorted = values.OrderBy(v => v).ToArray();
        int idx = Math.Max(0, Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1));
        return sorted[idx];
    }

    private static double ThroughputMBs(int sizeBytes, double latencyMs)
        => (sizeBytes / (1024.0 * 1024.0)) / (latencyMs / 1000.0);
}
