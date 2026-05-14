namespace CongaSharp.Tests.Integration;

using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Events;

using System.Diagnostics;

using Xunit.Abstractions;

/// <summary>
/// Shared utilities for integration tests: payload generation, connection setup,
/// event accumulation, and throughput measurement.
/// </summary>
internal static class IntegrationTestHelper
{
  public static readonly int[] SmallPayloadSizes = [100, 1_024, 102_400];
  public static readonly int[] LargePayloadSizes = [1_048_576, 10_485_760];
  public static readonly byte[] CrlfEom = [13, 10];

  /// <summary>
  /// Generates deterministic random-looking bytes (incompressible).
  /// </summary>
  public static byte[] GenerateRandomPayload(int size, int seed = 42)
  {
    var rng = new Random(seed);
    var data = new byte[size];
    rng.NextBytes(data);
    return data;
  }

  /// <summary>
  /// Generates a highly-compressible payload (repeating ASCII pattern).
  /// Compression ratios of 10:1+ are typical.
  /// </summary>
  public static byte[] GenerateCompressiblePayload(int size)
  {
    const string pattern = "ABCDEFGHIJ0123456789abcdefghij";
    var data = new byte[size];
    for (int i = 0; i < size; i++)
      data[i] = (byte)pattern[i % pattern.Length];
    return data;
  }

  /// <summary>
  /// Generates a printable ASCII payload that is guaranteed not to contain the EOM sequence.
  /// Uses characters in the range 0x20-0x7E, excluding bytes present in the EOM.
  /// </summary>
  public static byte[] GenerateTextPayload(int size, byte[] eom)
  {
    var forbidden = new HashSet<byte>(eom);
    var allowed = new List<byte>();
    for (byte b = 0x20; b <= 0x7E; b++) {
      if (!forbidden.Contains(b))
        allowed.Add(b);
    }

    var rng = new Random(42);
    var data = new byte[size];
    for (int i = 0; i < size; i++)
      data[i] = allowed[rng.Next(allowed.Count)];
    return data;
  }

  /// <summary>
  /// Sets up a server, client, and accepted connection in the given mode.
  /// Returns a <see cref="TestConnection"/> with all the pieces.
  /// </summary>
  public static async Task<TestConnection> SetupConnection(
      Root root, string mode, int bufferSize = 65536)
  {
    var server = new ServerObject("SRV00000000", "127.0.0.1", 0, mode, bufferSize);
    root.Registry.TryAdd(server);
    var startRc = server.Start(root);
    if (startRc != ErrorCodes.Success)
      throw new InvalidOperationException($"Server start failed: {startRc}");

    var client = new ClientObject("CLT00000000", "127.0.0.1", server.LocalPort, mode, bufferSize);
    root.Registry.TryAdd(client);
    var connectRc = await client.ConnectAsync(root, 10_000);
    if (connectRc != ErrorCodes.Success)
      throw new InvalidOperationException($"Client connect failed: {connectRc}");

    // Wait for Connect event to get the server-side connection name
    var connectEvt = root.Events.Wait("SRV00000000", 10_000, root.ShutdownToken);
    if (connectEvt.Type != EventType.Connect)
      throw new InvalidOperationException($"Expected Connect event, got {connectEvt.Type}");

    var connName = connectEvt.ObjectName;
    var connObj = root.Registry.Lookup(connName) as ConnectionObject
        ?? throw new InvalidOperationException($"Connection object {connName} not found");

    return new TestConnection(server, client, connName, connObj);
  }

  /// <summary>
  /// Accumulates raw-mode Receive events until the expected byte count is reached.
  /// </summary>
  public static byte[] AccumulateRawEvents(Root root, string filter, int expectedLen, int timeoutMs = 30_000)
  {
    var received = new List<byte>(expectedLen);
    var sw = Stopwatch.StartNew();
    while (received.Count < expectedLen && sw.ElapsedMilliseconds < timeoutMs) {
      var remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
      if (remaining <= 0) break;
      var evt = root.Events.Wait(filter, remaining, root.ShutdownToken);
      if (evt.Type == EventType.Timeout) break;
      if (evt.Type == EventType.Receive)
        received.AddRange(evt.Payload.ToArray());
    }
    return received.ToArray();
  }

  /// <summary>
  /// Logs throughput measurement to test output.
  /// </summary>
  public static void LogThroughput(ITestOutputHelper output, string label, long bytes, TimeSpan elapsed)
  {
    var mbPerSec = bytes / 1_048_576.0 / elapsed.TotalSeconds;
    output.WriteLine($"  {label}: {bytes:N0} bytes in {elapsed.TotalMilliseconds:F1} ms ({mbPerSec:F1} MB/s)");
  }

  /// <summary>
  /// Logs compression ratio measurement.
  /// </summary>
  public static void LogCompressionRatio(ITestOutputHelper output, string label, int originalSize, int compressedSize)
  {
    var ratio = (double)compressedSize / originalSize;
    output.WriteLine($"  {label}: {originalSize:N0} → {compressedSize:N0} bytes (ratio: {ratio:F3})");
  }

  /// <summary>
  /// Formats a human-readable size string.
  /// </summary>
  public static string FormatSize(int bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1_048_576 => $"{bytes / 1024} KB",
    _ => $"{bytes / 1_048_576} MB"
  };
}

/// <summary>
/// Holds all objects for a test connection.
/// </summary>
internal sealed class TestConnection : IAsyncDisposable
{
  public ServerObject Server { get; }
  public ClientObject Client { get; }
  public string ConnName { get; }
  public ConnectionObject ConnObj { get; }

  public TestConnection(ServerObject server, ClientObject client, string connName, ConnectionObject connObj)
  {
    Server = server;
    Client = client;
    ConnName = connName;
    ConnObj = connObj;
  }

  public async ValueTask DisposeAsync()
  {
    try { await Client.DisposeAsync(); } catch { }
    if (ConnObj.Pipeline != null)
      try { await ConnObj.Pipeline.DisposeAsync(); } catch { }
    try { await Server.DisposeAsync(); } catch { }
  }
}
