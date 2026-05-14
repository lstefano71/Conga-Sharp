namespace CongaSharp.Tests.Integration;

using CongaSharp.Core;
using CongaSharp.Events;
using CongaSharp.Modes;
using CongaSharp.Protocol;

using System.Diagnostics;

using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Full integration tests: server ↔ client roundtrips across all modes,
/// payload sizes, compression algorithms, and compression levels.
/// </summary>
public class FullIntegrationTests : IAsyncLifetime
{
  private readonly ITestOutputHelper _output;
  private Root _root = null!;

  public FullIntegrationTests(ITestOutputHelper output) => _output = output;

  public Task InitializeAsync()
  {
    _root = new Root();
    return Task.CompletedTask;
  }

  public async Task DisposeAsync()
  {
    foreach (var obj in _root.Registry.GetAllObjects()) {
      if (obj is ServerObject server)
        await server.DisposeAsync();
      else if (obj is ClientObject client)
        await client.DisposeAsync();
      else if (obj is ConnectionObject conn && conn.Pipeline != null)
        await conn.Pipeline.DisposeAsync();
    }
    _root.Dispose();
  }

  #region Tier 1A: SendReceive_AllModes

  public static IEnumerable<object[]> ModeAndSizeCombinations()
  {
    var modes = new[] { "Raw", "Text", "BlkRaw", "Command" };
    foreach (var mode in modes)
      foreach (var size in IntegrationTestHelper.SmallPayloadSizes)
        yield return [mode, size];
  }

  [Theory]
  [MemberData(nameof(ModeAndSizeCombinations))]
  public async Task SendReceive_AllModes(string mode, int payloadSize)
  {
    _output.WriteLine($"Mode={mode}, Size={IntegrationTestHelper.FormatSize(payloadSize)}");

    await using var tc = await IntegrationTestHelper.SetupConnection(_root, mode);

    if (mode == "Text") {
      tc.Server.Properties.Set("EOM", "[[13,10]]");
      tc.Client.Properties.Set("EOM", "[[13,10]]");
    }

    var payload = mode == "Text"
        ? IntegrationTestHelper.GenerateTextPayload(payloadSize, IntegrationTestHelper.CrlfEom)
        : IntegrationTestHelper.GenerateRandomPayload(payloadSize);

    // --- Client → Server ---
    var sw = Stopwatch.StartNew();
    await SendPayload(tc.Client.Pipeline!, mode, payload, "CLT00000000", isCommand: mode == "Command");
    var received = await ReceivePayload(_root, mode, tc.ConnName, payloadSize);
    sw.Stop();

    Assert.Equal(payload, received);
    IntegrationTestHelper.LogThroughput(_output, "Client→Server", payloadSize, sw.Elapsed);

    // --- Server → Client (bidirectional) ---
    var replyPayload = mode == "Text"
        ? IntegrationTestHelper.GenerateTextPayload(payloadSize, IntegrationTestHelper.CrlfEom)
        : IntegrationTestHelper.GenerateRandomPayload(payloadSize, seed: 99);

    sw.Restart();
    await SendReply(tc.ConnObj.Pipeline!, mode, replyPayload, tc.ConnName);
    var clientReceived = await ReceivePayload(_root, mode, "CLT00000000", payloadSize);
    sw.Stop();

    Assert.Equal(replyPayload, clientReceived);
    IntegrationTestHelper.LogThroughput(_output, "Server→Client", payloadSize, sw.Elapsed);
  }

  [Theory]
  [InlineData(100)]
  [InlineData(10_240)]
  public async Task SendReceive_BlkText_Smoke(int payloadSize)
  {
    _output.WriteLine($"Mode=BlkText, Size={IntegrationTestHelper.FormatSize(payloadSize)}");

    await using var tc = await IntegrationTestHelper.SetupConnection(_root, "BlkText");

    var payload = IntegrationTestHelper.GenerateRandomPayload(payloadSize);
    var modeObj = new BlkTextMode();
    var outMsg = modeObj.PrepareOutbound("CLT00000000", payload, null, PostSendAction.None, null);
    await tc.Client.Pipeline!.SendAsync(outMsg);

    var recvEvt = _root.Events.Wait(tc.ConnName, 10_000, _root.ShutdownToken);
    Assert.Equal(EventType.Block, recvEvt.Type);
    Assert.Equal(payload, recvEvt.Payload);
  }

  #endregion

  #region Tier 1B: Compression_BasicRoundtrip

  public static IEnumerable<object[]> CompressionSmokeCombinations()
  {
    var algos = new[] { CompressionAlgorithm.Deflate, CompressionAlgorithm.LZ4, CompressionAlgorithm.Zstd };
    var sizes = new[] { 1_024, 102_400 };
    foreach (var algo in algos)
      foreach (var size in sizes)
        yield return [algo, size];
  }

  [Theory]
  [MemberData(nameof(CompressionSmokeCombinations))]
  public async Task Compression_BasicRoundtrip(CompressionAlgorithm algo, int payloadSize)
  {
    _output.WriteLine($"Algo={algo}, Size={IntegrationTestHelper.FormatSize(payloadSize)}");

    await using var tc = await IntegrationTestHelper.SetupConnection(_root, "BlkRaw");

    var payload = IntegrationTestHelper.GenerateCompressiblePayload(payloadSize);

    // Send with compression
    var modeObj = new BlkRawMode();
    var outMsg = modeObj.PrepareOutbound("CLT00000000", payload, null, PostSendAction.None, null);
    outMsg.Compression = algo;
    await tc.Client.Pipeline!.SendAsync(outMsg);

    // Receive — pipeline decompresses transparently
    var recvEvt = _root.Events.Wait(tc.ConnName, 10_000, _root.ShutdownToken);
    Assert.Equal(EventType.Block, recvEvt.Type);
    Assert.Equal(payload, recvEvt.Payload);

    // Log compression ratio
    var compressed = Compression.Compress(algo, payload);
    IntegrationTestHelper.LogCompressionRatio(_output, $"{algo}", payloadSize, compressed.Length);
  }

  #endregion

  #region Tier 1C: CompressionLevel_Roundtrip

  public static IEnumerable<object[]> CompressionLevelCombinations()
  {
    yield return [CompressionAlgorithm.Deflate, 1];
    yield return [CompressionAlgorithm.Deflate, 2];
    yield return [CompressionAlgorithm.Deflate, 3];
    yield return [CompressionAlgorithm.LZ4, 0];
    yield return [CompressionAlgorithm.LZ4, 6];
    yield return [CompressionAlgorithm.LZ4, 12];
    yield return [CompressionAlgorithm.Zstd, 1];
    yield return [CompressionAlgorithm.Zstd, 3];
    yield return [CompressionAlgorithm.Zstd, 10];
    yield return [CompressionAlgorithm.Zstd, 22];
  }

  [Theory]
  [MemberData(nameof(CompressionLevelCombinations))]
  public async Task CompressionLevel_Roundtrip(CompressionAlgorithm algo, int level)
  {
    const int payloadSize = 102_400;
    _output.WriteLine($"Algo={algo}, Level={level}, Size={IntegrationTestHelper.FormatSize(payloadSize)}");

    await using var tc = await IntegrationTestHelper.SetupConnection(_root, "BlkRaw");

    var payload = IntegrationTestHelper.GenerateCompressiblePayload(payloadSize);

    var modeObj = new BlkRawMode();
    var outMsg = modeObj.PrepareOutbound("CLT00000000", payload, null, PostSendAction.None, null);
    outMsg.Compression = algo;
    outMsg.CompressionLevel = level;
    await tc.Client.Pipeline!.SendAsync(outMsg);

    var recvEvt = _root.Events.Wait(tc.ConnName, 10_000, _root.ShutdownToken);
    Assert.Equal(EventType.Block, recvEvt.Type);
    Assert.Equal(payload, recvEvt.Payload);

    var compressed = Compression.Compress(algo, payload, level);
    IntegrationTestHelper.LogCompressionRatio(_output, $"{algo} level={level}", payloadSize, compressed.Length);
  }

  #endregion

  #region Tier 2: Large payload tests

  public static IEnumerable<object[]> LargePayloadCombinations()
  {
    var modes = new[] { "Raw", "BlkRaw", "Command" };
    foreach (var mode in modes)
      foreach (var size in IntegrationTestHelper.LargePayloadSizes)
        yield return [mode, size];
  }

  [Theory]
  [Trait("Category", "LargePayload")]
  [MemberData(nameof(LargePayloadCombinations))]
  public async Task SendReceive_LargePayloads(string mode, int payloadSize)
  {
    _output.WriteLine($"Mode={mode}, Size={IntegrationTestHelper.FormatSize(payloadSize)}");

    await using var tc = await IntegrationTestHelper.SetupConnection(_root, mode);

    var payload = IntegrationTestHelper.GenerateRandomPayload(payloadSize);

    var sw = Stopwatch.StartNew();
    await SendPayload(tc.Client.Pipeline!, mode, payload, "CLT00000000", isCommand: mode == "Command");
    var received = await ReceivePayload(_root, mode, tc.ConnName, payloadSize);
    sw.Stop();

    Assert.Equal(payload, received);
    IntegrationTestHelper.LogThroughput(_output, $"{mode} {IntegrationTestHelper.FormatSize(payloadSize)}", payloadSize, sw.Elapsed);
  }

  public static IEnumerable<object[]> CompressionLargePayloadCombinations()
  {
    var algos = new[] { CompressionAlgorithm.None, CompressionAlgorithm.Deflate, CompressionAlgorithm.LZ4, CompressionAlgorithm.Zstd };
    var patterns = new[] { "Random", "Compressible" };
    foreach (var algo in algos)
      foreach (var size in IntegrationTestHelper.LargePayloadSizes)
        foreach (var pattern in patterns)
          yield return [algo, size, pattern];
  }

  [Theory]
  [Trait("Category", "LargePayload")]
  [MemberData(nameof(CompressionLargePayloadCombinations))]
  public async Task Compression_LargePayloads(CompressionAlgorithm algo, int payloadSize, string pattern)
  {
    _output.WriteLine($"Algo={algo}, Size={IntegrationTestHelper.FormatSize(payloadSize)}, Pattern={pattern}");

    await using var tc = await IntegrationTestHelper.SetupConnection(_root, "BlkRaw");

    var payload = pattern == "Compressible"
        ? IntegrationTestHelper.GenerateCompressiblePayload(payloadSize)
        : IntegrationTestHelper.GenerateRandomPayload(payloadSize);

    var modeObj = new BlkRawMode();
    var outMsg = modeObj.PrepareOutbound("CLT00000000", payload, null, PostSendAction.None, null);
    outMsg.Compression = algo;

    var sw = Stopwatch.StartNew();
    await tc.Client.Pipeline!.SendAsync(outMsg);

    var recvEvt = _root.Events.Wait(tc.ConnName, 60_000, _root.ShutdownToken);
    sw.Stop();

    Assert.Equal(EventType.Block, recvEvt.Type);
    Assert.Equal(payload, recvEvt.Payload);

    IntegrationTestHelper.LogThroughput(_output, "Transfer", payloadSize, sw.Elapsed);
    if (algo != CompressionAlgorithm.None) {
      var compressed = Compression.Compress(algo, payload);
      IntegrationTestHelper.LogCompressionRatio(_output, $"{algo} ({pattern})", payloadSize, compressed.Length);
    }
  }

  #endregion

  #region Tier 3: Benchmark summary

  [Fact]
  [Trait("Category", "PerfSmoke")]
  public async Task CompressionBenchmarkSummary()
  {
    const int warmupIterations = 5;
    const int measuredIterations = 50;
    var algos = new[] { CompressionAlgorithm.None, CompressionAlgorithm.Deflate, CompressionAlgorithm.LZ4, CompressionAlgorithm.Zstd };
    var sizes = new[] { 1_024, 102_400, 1_048_576, 10_485_760 };
    var patterns = new[] { "Random", "Compressible" };

    _output.WriteLine($"Warmup iterations: {warmupIterations}, measured iterations: {measuredIterations} (reported as p50/p95)");
    _output.WriteLine($"{"Compression",-10} | {"Size",8} | {"Pattern",-13} | {"Thrpt p50",9} | {"Thrpt p95",9} | {"Msg/s p50",9} | {"Time p50",9} | {"Time p95",9} | {"Ratio",6}");
    _output.WriteLine(new string('-', 124));

    foreach (var algo in algos) {
      foreach (var size in sizes) {
        foreach (var pattern in patterns) {
          // Fresh root for each iteration to avoid event queue pollution
          using var root = new Root();
          var tc = await IntegrationTestHelper.SetupConnection(root, "BlkRaw");
          try {
            var payload = pattern == "Compressible"
                ? IntegrationTestHelper.GenerateCompressiblePayload(size)
                : IntegrationTestHelper.GenerateRandomPayload(size);

            var modeObj = new BlkRawMode();
            var outMsg = modeObj.PrepareOutbound("CLT00000000", payload, null, PostSendAction.None, null);
            outMsg.Compression = algo;
            var elapsedMs = new List<double>(measuredIterations);

            for (int i = 0; i < warmupIterations + measuredIterations; i++) {
              var sw = Stopwatch.StartNew();
              await tc.Client.Pipeline!.SendAsync(outMsg);
              var recvEvt = root.Events.Wait(tc.ConnName, 60_000, root.ShutdownToken);
              sw.Stop();

              Assert.Equal(EventType.Block, recvEvt.Type);
              Assert.Equal(payload.Length, recvEvt.Payload.Length);

              if (i >= warmupIterations)
                elapsedMs.Add(sw.Elapsed.TotalMilliseconds);
            }

            var p50Ms = Percentile(elapsedMs, 0.50);
            var p95Ms = Percentile(elapsedMs, 0.95);
            var p50Throughput = ThroughputMbPerSec(size, p50Ms);
            var p95Throughput = ThroughputMbPerSec(size, p95Ms);
            var p50MessagesPerSec = 1000.0 / p50Ms;
            var ratio = algo != CompressionAlgorithm.None
                ? (double)Compression.Compress(algo, payload).Length / size
                : 1.0;

            _output.WriteLine(
                $"{algo,-10} | {IntegrationTestHelper.FormatSize(size),8} | {pattern,-13} | {p50Throughput,8:F1} | {p95Throughput,8:F1} | {p50MessagesPerSec,8:F1} | {p50Ms,8:F1} ms | {p95Ms,8:F1} ms | {ratio,5:F3}");
          } finally {
            await tc.DisposeAsync();
            // Dispose remaining objects
            foreach (var obj in root.Registry.GetAllObjects()) {
              if (obj is ServerObject server) await server.DisposeAsync();
              else if (obj is ClientObject client) await client.DisposeAsync();
              else if (obj is ConnectionObject conn && conn.Pipeline != null)
                await conn.Pipeline.DisposeAsync();
            }
          }
        }
      }
    }
  }

  #endregion

  #region Helpers

  private static async Task SendPayload(
      CongaSharp.Networking.SocketPipeline pipeline, string mode, byte[] payload, string connName,
      bool isCommand = false)
  {
    IConnectionMode modeObj = mode switch {
      "Raw" => new RawMode(),
      "Text" => new TextMode(),
      "BlkRaw" => new BlkRawMode(),
      "BlkText" => new BlkTextMode(),
      "Command" => new CommandMode(),
      _ => throw new ArgumentException($"Unknown mode: {mode}")
    };

    byte[] sendData = payload;
    if (mode == "Text") {
      // Append CRLF EOM
      sendData = new byte[payload.Length + IntegrationTestHelper.CrlfEom.Length];
      payload.CopyTo(sendData, 0);
      IntegrationTestHelper.CrlfEom.CopyTo(sendData, payload.Length);
    }

    var cmdName = isCommand ? "TestCmd" : null;
    var outMsg = modeObj.PrepareOutbound(connName, sendData, null, PostSendAction.None, cmdName);
    await pipeline.SendAsync(outMsg);
  }

  private Task<byte[]> ReceivePayload(Root root, string mode, string filter, int payloadSize)
  {
    switch (mode) {
      case "Raw":
        return Task.FromResult(
            IntegrationTestHelper.AccumulateRawEvents(root, filter, payloadSize));

      case "Text": {
          // Text mode: accumulate until we get a message with the EOM appended
          var totalExpected = payloadSize + IntegrationTestHelper.CrlfEom.Length;
          var data = IntegrationTestHelper.AccumulateRawEvents(root, filter, totalExpected);
          // Strip the EOM suffix before comparing
          return Task.FromResult(data[..payloadSize]);
        }

      case "BlkRaw":
      case "BlkText": {
          var evt = root.Events.Wait(filter, 30_000, root.ShutdownToken);
          Assert.Equal(EventType.Block, evt.Type);
          return Task.FromResult(evt.Payload.ToArray());
        }

      case "Command": {
          // Command mode: event arrives on filter.TestCmd
          var cmdFilter = filter.Contains('.') ? filter : filter;
          var evt = root.Events.Wait(cmdFilter, 30_000, root.ShutdownToken);
          Assert.Equal(EventType.Receive, evt.Type);
          return Task.FromResult(evt.Payload.ToArray());
        }

      default:
        throw new ArgumentException($"Unknown mode: {mode}");
    }
  }

  private static async Task SendReply(
      CongaSharp.Networking.SocketPipeline pipeline, string mode, byte[] payload, string connName)
  {
    byte[] sendData = payload;
    if (mode == "Text") {
      sendData = new byte[payload.Length + IntegrationTestHelper.CrlfEom.Length];
      payload.CopyTo(sendData, 0);
      IntegrationTestHelper.CrlfEom.CopyTo(sendData, payload.Length);
    }

    if (mode == "Command") {
      // Use the pipeline's own CommandMode which has the correlation maps from received frames
      var cmdMode = (CommandMode)pipeline.Mode;
      // Find the first active command to respond to
      var cmdName = GetFirstActiveCommand(cmdMode, connName);
      var msg = cmdMode.TryPrepareRespond(connName, sendData, cmdName);
      Assert.NotNull(msg);
      await pipeline.SendAsync(msg);
    } else {
      IConnectionMode modeObj = mode switch {
        "Raw" => new RawMode(),
        "Text" => new TextMode(),
        "BlkRaw" => new BlkRawMode(),
        "BlkText" => new BlkTextMode(),
        _ => throw new ArgumentException($"Unknown mode: {mode}")
      };
      var msg = modeObj.PrepareOutbound(connName, sendData, null, PostSendAction.None, null);
      await pipeline.SendAsync(msg);
    }
  }

  /// <summary>
  /// Finds the first active command name for a connection via reflection on CommandMode's internal state.
  /// Used by integration tests to discover server-side auto-generated command names.
  /// </summary>
  private static string GetFirstActiveCommand(CommandMode cmdMode, string connName)
  {
    // Use IsCommandActive with known patterns — the receiver generates Cmd00000000 names
    for (int i = 0; i < 100; i++) {
      var name = $"Cmd{i:D8}";
      if (cmdMode.IsCommandActive(connName, name))
        return name;
    }
    throw new InvalidOperationException($"No active command found for {connName}");
  }

  private static double ThroughputMbPerSec(int bytes, double elapsedMs)
  {
    var elapsedSeconds = elapsedMs / 1000.0;
    return bytes / 1_048_576.0 / elapsedSeconds;
  }

  private static double Percentile(List<double> samples, double percentile)
  {
    if (samples.Count == 0)
      throw new ArgumentException("No samples to calculate percentile.", nameof(samples));

    var ordered = samples.OrderBy(x => x).ToArray();
    var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
    index = Math.Clamp(index, 0, ordered.Length - 1);
    return ordered[index];
  }

  #endregion
}
