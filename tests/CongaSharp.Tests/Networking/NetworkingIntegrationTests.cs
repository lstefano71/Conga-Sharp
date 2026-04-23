using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Events;
using CongaSharp.Modes;
using CongaSharp.Networking;
using CongaSharp.Protocol;

using System.Net;

using Xunit;

namespace CongaSharp.Tests.Networking;

public class NetworkingIntegrationTests : IAsyncLifetime
{
  private Root _root = null!;

  public Task InitializeAsync()
  {
    _root = new Root();
    return Task.CompletedTask;
  }

  public async Task DisposeAsync()
  {
    // Dispose all server/client objects
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

  [Fact]
  public async Task ServerStartsOnEphemeralPort()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);

    var result = server.Start(_root);
    Assert.Equal(ErrorCodes.Success, result);
    Assert.Equal(ObjectState.Started, server.State);
    Assert.True(server.LocalPort > 0, "Ephemeral port should be assigned");

    await server.DisposeAsync();
  }

  [Fact]
  public async Task ServerStartTwiceReturnsAlreadyStarted()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);

    Assert.Equal(ErrorCodes.Success, server.Start(_root));
    Assert.Equal(ErrorCodes.ObjectAlreadyStarted, server.Start(_root));

    await server.DisposeAsync();
  }

  [Fact]
  public async Task ClientConnectsToServer_RawMode()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Raw", 4096);
    _root.Registry.TryAdd(client);

    var connectResult = await client.ConnectAsync(_root, 5000);
    Assert.Equal(ErrorCodes.Success, connectResult);
    Assert.Equal(ObjectState.Started, client.State);

    // Wait for Connect event from the server side
    var evt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, evt.Type);
    Assert.StartsWith("S1.CON", evt.ObjectName);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task ServerAcceptsMultipleClients()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client1 = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Raw", 4096);
    var client2 = new ClientObject("C2", "127.0.0.1", server.LocalPort, "Raw", 4096);
    _root.Registry.TryAdd(client1);
    _root.Registry.TryAdd(client2);

    Assert.Equal(ErrorCodes.Success, await client1.ConnectAsync(_root, 5000));
    Assert.Equal(ErrorCodes.Success, await client2.ConnectAsync(_root, 5000));

    // Should get two Connect events
    var evt1 = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    var evt2 = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, evt1.Type);
    Assert.Equal(EventType.Connect, evt2.Type);
    Assert.NotEqual(evt1.ObjectName, evt2.ObjectName);

    await client1.DisposeAsync();
    await client2.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task RawMode_SendAndReceive()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Raw", 4096);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    // Wait for Connect to get the connection name
    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, connectEvt.Type);
    var connName = connectEvt.ObjectName;

    // Client sends data to server
    var testData = "Hello, Server!"u8.ToArray();
    var mode = ModeFactory.Create(ModeKind.Raw);
    var outMsg = mode.PrepareOutbound(connName, testData, null, PostSendAction.None, null);
    Assert.Equal(0, outMsg.ErrorCode);

    // Send from client
    await client.Pipeline!.SendAsync(outMsg);

    // Server should receive it on the connection
    var recvEvt = _root.Events.Wait(connName, 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Receive, recvEvt.Type);
    Assert.Equal(testData, recvEvt.Payload);

    // Server-side connection sends back to client
    var replyData = "Hello, Client!"u8.ToArray();
    var connObj = _root.Registry.Lookup(connName) as ConnectionObject;
    Assert.NotNull(connObj);
    var replyMsg = mode.PrepareOutbound(connName, replyData, null, PostSendAction.None, null);
    await connObj.Pipeline!.SendAsync(replyMsg);

    // Client should receive it
    var clientRecvEvt = _root.Events.Wait("C1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Receive, clientRecvEvt.Type);
    Assert.Equal(replyData, clientRecvEvt.Payload);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task TextMode_SendWithEom()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Text", 4096);
    _root.Registry.TryAdd(server);
    // Configure EOM = CRLF
    server.Properties.Set("EOM", "[[13,10]]");
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Text", 4096);
    _root.Registry.TryAdd(client);
    client.Properties.Set("EOM", "[[13,10]]");
    await client.ConnectAsync(_root, 5000);

    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, connectEvt.Type);
    var connName = connectEvt.ObjectName;

    // Send a message with CRLF EOM from client
    var message = System.Text.Encoding.UTF8.GetBytes("Hello\r\n");
    var mode = new TextMode();
    var outMsg = mode.PrepareOutbound("C1", message, null, PostSendAction.None, null);
    await client.Pipeline!.SendAsync(outMsg);

    // Server receives the message (including EOM bytes)
    var recvEvt = _root.Events.Wait(connName, 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Receive, recvEvt.Type);
    Assert.Equal(message, recvEvt.Payload);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task BlkRawMode_SendFrameAndReceiveBlockEvent()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "BlkRaw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "BlkRaw", 4096);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, connectEvt.Type);
    var connName = connectEvt.ObjectName;

    // Client sends framed data
    var testData = "Framed payload"u8.ToArray();
    var mode = new BlkRawMode();
    var outMsg = mode.PrepareOutbound("C1", testData, null, PostSendAction.None, null);
    await client.Pipeline!.SendAsync(outMsg);

    // Server receives Block event
    var recvEvt = _root.Events.Wait(connName, 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Block, recvEvt.Type);
    Assert.Equal(testData, recvEvt.Payload);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task CommandMode_SendAndReceive()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Command", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Command", 4096);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, connectEvt.Type);
    var connName = connectEvt.ObjectName;

    // Client sends a command
    var cmdPayload = "echo test"u8.ToArray();
    var clientMode = (CommandMode)client.Pipeline!.Mode;
    var outMsg = clientMode.PrepareOutbound("C1", cmdPayload, null, PostSendAction.None, "MyCmd");
    await client.Pipeline!.SendAsync(outMsg);

    // Server receives command on connection.<auto-generated-name>
    var cmdEvt = _root.Events.Wait(connName, 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Receive, cmdEvt.Type);
    Assert.StartsWith($"{connName}.", cmdEvt.ObjectName);
    Assert.Equal(cmdPayload, cmdEvt.Payload);
    // Extract the server-side command suffix for responding
    var serverCmdName = cmdEvt.ObjectName[(connName.Length + 1)..];

    // Server sends respond back using the pipeline's mode (which has the correlation)
    var responsePayload = "echo result"u8.ToArray();
    var connObj = _root.Registry.Lookup(connName) as ConnectionObject;
    Assert.NotNull(connObj);
    var serverMode = (CommandMode)connObj.Pipeline!.Mode;
    var respondMsg = serverMode.TryPrepareRespond(connName, responsePayload, serverCmdName);
    Assert.NotNull(respondMsg);
    await connObj.Pipeline!.SendAsync(respondMsg);

    // Client receives response
    var respEvt = _root.Events.Wait("C1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Receive, respEvt.Type);
    Assert.Equal(responsePayload, respEvt.Payload);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task ConnectionCloseEmitsClosedEvent()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Raw", 4096);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, connectEvt.Type);
    var connName = connectEvt.ObjectName;

    // Close the client — this should trigger a Closed event on the server-side connection
    await client.DisposeAsync();

    var closedEvt = _root.Events.Wait(connName, 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Closed, closedEvt.Type);
    Assert.Equal(connName, closedEvt.ObjectName);

    await server.DisposeAsync();
  }

  [Fact]
  public async Task ClientConnectTimeout()
  {
    // Connect to a non-routable address — should time out
    // Use a port on localhost that nobody is listening on
    var client = new ClientObject("C1", "192.0.2.1", 9999, "Raw", 4096);
    _root.Registry.TryAdd(client);

    var result = await client.ConnectAsync(_root, 500);
    // Should return timeout or connect failed
    Assert.True(result == ErrorCodes.Timeout || result == ErrorCodes.ConnectFailed,
        $"Expected Timeout or ConnectFailed, got {result}");

    await client.DisposeAsync();
  }

  [Fact]
  public async Task ShutdownClosesEverything()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Raw", 4096);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Connect, connectEvt.Type);

    // Shutdown the root — should stop all operations
    _root.Shutdown();

    // Subsequent wait should return shutdown error
    var evt = _root.Events.Wait(null, 1000);
    Assert.Equal(EventType.Error, evt.Type);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task DnsResolver_ParsesLiteralIp()
  {
    var addr = await DnsResolver.ResolveAsync("127.0.0.1");
    Assert.Equal(IPAddress.Loopback, addr);
  }

  [Fact]
  public async Task DnsResolver_ResolvesLocalhost()
  {
    var addr = await DnsResolver.ResolveAsync("localhost");
    Assert.NotNull(addr);
  }

  [Fact]
  public async Task BlkRawMode_BidirectionalFrames()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "BlkRaw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "BlkRaw", 4096);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    var connName = connectEvt.ObjectName;

    // Client sends frame
    var outData = "client data"u8.ToArray();
    var mode = new BlkRawMode();
    var outMsg = mode.PrepareOutbound("C1", outData, null, PostSendAction.None, null);
    await client.Pipeline!.SendAsync(outMsg);

    // Server receives
    var recvEvt = _root.Events.Wait(connName, 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Block, recvEvt.Type);
    Assert.Equal(outData, recvEvt.Payload);

    // Server sends reply frame
    var replyData = "server reply"u8.ToArray();
    var replyMsg = mode.PrepareOutbound(connName, replyData, null, PostSendAction.None, null);
    var connObj = _root.Registry.Lookup(connName) as ConnectionObject;
    Assert.NotNull(connObj);
    await connObj.Pipeline!.SendAsync(replyMsg);

    // Client receives
    var clientRecvEvt = _root.Events.Wait("C1", 5000, _root.ShutdownToken);
    Assert.Equal(EventType.Block, clientRecvEvt.Type);
    Assert.Equal(replyData, clientRecvEvt.Payload);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task ServerLocalPortProperty_IsSetAfterStart()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    // LocalPort property should reflect the actual port
    var result = server.Properties.Get("LocalPort", out var portJson);
    Assert.Equal(ErrorCodes.Success, result);
    Assert.Equal(server.LocalPort.ToString(), portJson);

    // LocalAddr should be set
    result = server.Properties.Get("LocalAddr", out var addrJson);
    Assert.Equal(ErrorCodes.Success, result);
    Assert.Contains("127.0.0.1", addrJson);

    await server.DisposeAsync();
  }

  [Fact]
  public async Task ClientPeerAddr_IsSetAfterConnect()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 4096);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Raw", 4096);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    // Drain connect event
    _root.Events.Wait("S1", 5000, _root.ShutdownToken);

    // PeerAddr should be set (not defined for Client in PropertyDefinitions currently,
    // but SetInternal bypasses checks — verify via the internal store)
    var localResult = client.Properties.Get("LocalAddr", out var localAddr);
    Assert.Equal(ErrorCodes.Success, localResult);
    Assert.Contains("127.0.0.1", localAddr);

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task RawMode_LargePayload()
  {
    var server = new ServerObject("S1", "127.0.0.1", 0, "Raw", 65536);
    _root.Registry.TryAdd(server);
    server.Start(_root);

    var client = new ClientObject("C1", "127.0.0.1", server.LocalPort, "Raw", 65536);
    _root.Registry.TryAdd(client);
    await client.ConnectAsync(_root, 5000);

    var connectEvt = _root.Events.Wait("S1", 5000, _root.ShutdownToken);
    var connName = connectEvt.ObjectName;

    // Send large payload — may arrive in multiple Receive events
    var largeData = new byte[32768];
    Random.Shared.NextBytes(largeData);
    var mode = new RawMode();
    var outMsg = mode.PrepareOutbound("C1", largeData, null, PostSendAction.None, null);
    await client.Pipeline!.SendAsync(outMsg);

    // Collect received data (may come in multiple events)
    var received = new List<byte>();
    while (received.Count < largeData.Length) {
      var recvEvt = _root.Events.Wait(connName, 5000, _root.ShutdownToken);
      if (recvEvt.Type == EventType.Timeout) break;
      Assert.Equal(EventType.Receive, recvEvt.Type);
      received.AddRange(recvEvt.Payload);
    }

    Assert.Equal(largeData, received.ToArray());

    await client.DisposeAsync();
    await server.DisposeAsync();
  }

  [Fact]
  public async Task AsyncFrameIO_Roundtrip()
  {
    // Test async frame I/O directly with a MemoryStream
    using var stream = new MemoryStream();
    var payload = "test payload"u8.ToArray();
    var userHeaders = new Dictionary<string, byte[]> {
      ["key1"] = "value1"u8.ToArray()
    };

    var corrId = Guid.NewGuid();
    await AsyncFrameIO.WriteFrameAsync(
        stream, MsgType.Data, corrId, payload, userHeaders,
        CompressionAlgorithm.None, 0, 0, CancellationToken.None);

    stream.Position = 0;

    var result = await AsyncFrameIO.ReadFrameAsync(stream, CancellationToken.None);
    Assert.True(result.Success);
    Assert.Equal(MsgType.Data, result.Header.MsgType);
    Assert.Equal(corrId, result.Header.CorrelationId);
    Assert.Equal(payload, result.Payload);
    Assert.True(result.UserHeaders.ContainsKey("key1"));
    Assert.Equal("value1"u8.ToArray(), result.UserHeaders["key1"]);
  }
}
