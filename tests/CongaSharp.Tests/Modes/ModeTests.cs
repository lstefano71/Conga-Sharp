namespace CongaSharp.Tests.Modes;

using CongaSharp.Events;
using CongaSharp.Modes;
using CongaSharp.Protocol;

using Xunit;

public class ModeTests
{
  // --- ModeKind ---

  [Theory]
  [InlineData("Raw", ModeKind.Raw)]
  [InlineData("TEXT", ModeKind.Text)]
  [InlineData("BlkRaw", ModeKind.BlkRaw)]
  [InlineData("blktext", ModeKind.BlkText)]
  [InlineData("Command", ModeKind.Command)]
  public void ModeKind_TryParse_ValidModes(string input, ModeKind expected)
  {
    Assert.Equal(expected, ModeKindExtensions.TryParse(input));
  }

  [Theory]
  [InlineData("")]
  [InlineData(null)]
  [InlineData("HTTP")]
  [InlineData("invalid")]
  public void ModeKind_TryParse_InvalidModes(string? input)
  {
    Assert.Null(ModeKindExtensions.TryParse(input));
  }

  [Theory]
  [InlineData(ModeKind.Raw, false)]
  [InlineData(ModeKind.Text, false)]
  [InlineData(ModeKind.BlkRaw, true)]
  [InlineData(ModeKind.BlkText, true)]
  [InlineData(ModeKind.Command, true)]
  public void ModeKind_IsFramed(ModeKind kind, bool expected)
  {
    Assert.Equal(expected, kind.IsFramed());
  }

  // --- ModeFactory ---

  [Theory]
  [InlineData(ModeKind.Raw, typeof(RawMode))]
  [InlineData(ModeKind.Text, typeof(TextMode))]
  [InlineData(ModeKind.BlkRaw, typeof(BlkRawMode))]
  [InlineData(ModeKind.BlkText, typeof(BlkTextMode))]
  [InlineData(ModeKind.Command, typeof(CommandMode))]
  public void ModeFactory_CreatesCorrectType(ModeKind kind, Type expectedType)
  {
    var mode = ModeFactory.Create(kind);
    Assert.IsType(expectedType, mode);
  }

  // --- RawMode ---

  [Fact]
  public void RawMode_OnBytesReceived_ProducesReceiveEvent()
  {
    var mode = new RawMode();
    var events = mode.OnBytesReceived("SRV00000000.CON00000000", new byte[] { 1, 2, 3 });
    Assert.Single(events);
    Assert.Equal(EventType.Receive, events[0].Type);
    Assert.Equal("SRV00000000.CON00000000", events[0].ObjectName);
    Assert.Equal(new byte[] { 1, 2, 3 }, events[0].Payload.ToArray());
  }

  [Fact]
  public void RawMode_OnBytesReceived_EmptyData_NoEvents()
  {
    var mode = new RawMode();
    var events = mode.OnBytesReceived("SRV00000000.CON00000000", ReadOnlySpan<byte>.Empty);
    Assert.Empty(events);
  }

  [Fact]
  public void RawMode_InvalidCloseFlags()
  {
    var mode = new RawMode();
    var msg = mode.PrepareOutbound("SRV00000000.CON00000000", new byte[] { 1, 2 }, null, PostSendAction.CloseCommand, null);
    Assert.NotEqual(0, msg.ErrorCode);

    msg = mode.PrepareOutbound("SRV00000000.CON00000000", new byte[] { 1, 2 }, null, PostSendAction.EmitSentEvent, null);
    Assert.NotEqual(0, msg.ErrorCode);
  }

  [Fact]
  public void RawMode_ValidSend()
  {
    var mode = new RawMode();
    var msg = mode.PrepareOutbound("SRV00000000.CON00000000", new byte[] { 1, 2, 3 }, null, PostSendAction.None, null);
    Assert.Equal(0, msg.ErrorCode);
    Assert.Equal(new byte[] { 1, 2, 3 }, msg.Payload.ToArray());
  }

  [Fact]
  public void RawMode_OnDisconnected()
  {
    var mode = new RawMode();
    var events = mode.OnDisconnected("SRV00000000.CON00000000");
    Assert.Single(events);
    Assert.Equal(EventType.Closed, events[0].Type);
  }

  [Fact]
  public void RawMode_ThrowsOnFrameReceived()
  {
    var mode = new RawMode();
    Assert.Throws<InvalidOperationException>(() => mode.OnFrameReceived("SRV00000000.CON00000000",
        new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = Guid.Empty, Payload = default, RawUserHeaders = default }));
  }

  // --- TextMode ---

  [Fact]
  public void TextMode_NoEOM_BehavesLikeRaw()
  {
    var mode = new TextMode();
    var events = mode.OnBytesReceived("CON", new byte[] { 72, 101, 108, 108, 111 });
    Assert.Single(events);
    Assert.Equal(EventType.Receive, events[0].Type);
    Assert.Equal(new byte[] { 72, 101, 108, 108, 111 }, events[0].Payload.ToArray());
  }

  [Fact]
  public void TextMode_WithEOM_SplitsMessages()
  {
    var mode = new TextMode();
    mode.ConfigureEomFromJson("[[13,10]]"); // CRLF

    // "Hello\r\nWorld\r\n"
    var data = new byte[] { 72, 101, 108, 108, 111, 13, 10, 87, 111, 114, 108, 100, 13, 10 };
    var events = mode.OnBytesReceived("CON", data);

    Assert.Equal(2, events.Count);
    Assert.Equal(new byte[] { 72, 101, 108, 108, 111, 13, 10 }, events[0].Payload.ToArray());
    Assert.Equal(new byte[] { 87, 111, 114, 108, 100, 13, 10 }, events[1].Payload.ToArray());
  }

  [Fact]
  public void TextMode_WithEOM_AccumulatesPartial()
  {
    var mode = new TextMode();
    mode.ConfigureEomFromJson("[[13,10]]");

    // Partial: "Hel"
    var events1 = mode.OnBytesReceived("CON", new byte[] { 72, 101, 108 });
    Assert.Empty(events1);

    // Complete: "lo\r\n"
    var events2 = mode.OnBytesReceived("CON", new byte[] { 108, 111, 13, 10 });
    Assert.Single(events2);
    Assert.Equal(new byte[] { 72, 101, 108, 108, 111, 13, 10 }, events2[0].Payload.ToArray());
  }

  [Fact]
  public void TextMode_OnDisconnected_FlushesBuffer()
  {
    var mode = new TextMode();
    mode.ConfigureEomFromJson("[[13,10]]");

    mode.OnBytesReceived("CON", new byte[] { 72, 101, 108 });
    var events = mode.OnDisconnected("CON");

    Assert.Equal(2, events.Count);
    Assert.Equal(EventType.Receive, events[0].Type);
    Assert.Equal(new byte[] { 72, 101, 108 }, events[0].Payload.ToArray());
    Assert.Equal(EventType.Closed, events[1].Type);
  }

  [Fact]
  public void TextMode_ConfigureEomFromJson()
  {
    var mode = new TextMode();
    mode.ConfigureEomFromJson("[[13,10],[10]]");
    Assert.Equal(2, mode.EomPatterns.Length);
    Assert.Equal(new byte[] { 13, 10 }, mode.EomPatterns[0]);
    Assert.Equal(new byte[] { 10 }, mode.EomPatterns[1]);
  }

  [Fact]
  public void TextMode_ConfigureEomFromJson_Empty()
  {
    var mode = new TextMode();
    mode.ConfigureEomFromJson("[]");
    Assert.Equal(0, mode.EomPatterns.Length);

    mode.ConfigureEomFromJson("");
    Assert.Equal(0, mode.EomPatterns.Length);
  }

  [Fact]
  public void TextMode_ResetState_ClearsBuffer()
  {
    var mode = new TextMode();
    mode.ConfigureEomFromJson("[[13,10]]");
    mode.OnBytesReceived("CON", new byte[] { 72, 101, 108 });
    mode.ResetState("CON");

    // After reset, no buffered data
    var events = mode.OnDisconnected("CON");
    Assert.Single(events); // Just Closed, no Receive
    Assert.Equal(EventType.Closed, events[0].Type);
  }

  // --- BlkRawMode ---

  [Fact]
  public void BlkRawMode_OnFrameReceived_DataProducesBlockEvent()
  {
    var mode = new BlkRawMode();
    var frame = new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = Guid.Empty, Payload = new byte[] { 1, 2, 3 }, RawUserHeaders = default };
    var events = mode.OnFrameReceived("CON", frame);
    Assert.Single(events);
    Assert.Equal(EventType.Block, events[0].Type);
    Assert.Equal(new byte[] { 1, 2, 3 }, events[0].Payload.ToArray());
  }

  [Fact]
  public void BlkRawMode_InvalidCloseCommand()
  {
    var mode = new BlkRawMode();
    var msg = mode.PrepareOutbound("CON", new byte[] { 1, 2 }, null, PostSendAction.CloseCommand, null);
    Assert.NotEqual(0, msg.ErrorCode);
  }

  [Fact]
  public void BlkRawMode_ThrowsOnBytesReceived()
  {
    var mode = new BlkRawMode();
    Assert.Throws<InvalidOperationException>(() => mode.OnBytesReceived("CON", new byte[] { 1 }));
  }

  // --- CommandMode ---

  [Fact]
  public void CommandMode_OnFrameReceived_DataCreatesReceive()
  {
    var mode = new CommandMode();
    var corrId = Guid.NewGuid();
    var frame = new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = corrId, Payload = new byte[] { 42 }, RawUserHeaders = default };
    var events = mode.OnFrameReceived("CON", frame);

    Assert.Single(events);
    Assert.Equal(EventType.Receive, events[0].Type);
    // CmdName is auto-generated from the Guid's first 8 hex chars
    Assert.StartsWith("CON.", events[0].ObjectName);
  }

  [Fact]
  public void CommandMode_OnFrameReceived_ProgressCreatesProgressEvent()
  {
    var mode = new CommandMode();
    var corrId = Guid.NewGuid();
    // First send Data to establish the correlation
    mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = corrId, Payload = default, RawUserHeaders = default });

    var frame = new FrameData { MsgType = MsgType.Progress, CmdName = "", CorrelationId = corrId, Payload = new byte[] { 50 }, RawUserHeaders = default };
    var events = mode.OnFrameReceived("CON", frame);

    Assert.Single(events);
    Assert.Equal(EventType.Progress, events[0].Type);
    Assert.StartsWith("CON.", events[0].ObjectName);
  }

  [Fact]
  public void CommandMode_OnFrameReceived_RespondClosesCommand()
  {
    var mode = new CommandMode();
    var corrId = Guid.NewGuid();
    var dataEvents = mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = corrId, Payload = default, RawUserHeaders = default });
    // Extract the auto-generated cmdName suffix
    var cmdName = dataEvents[0].ObjectName.Split('.')[1];
    Assert.True(mode.IsCommandActive("CON", cmdName));

    var frame = new FrameData { MsgType = MsgType.Respond, CmdName = "", CorrelationId = corrId, Payload = new byte[] { 99 }, RawUserHeaders = default };
    var events = mode.OnFrameReceived("CON", frame);

    Assert.Single(events);
    Assert.Equal(EventType.Receive, events[0].Type);
    Assert.False(mode.IsCommandActive("CON", cmdName));
  }

  [Fact]
  public void CommandMode_PrepareOutbound_GeneratesCorrelationId()
  {
    var mode = new CommandMode();
    var msg = mode.PrepareOutbound("CON", new byte[] { 1, 2 }, null, PostSendAction.None, "Echo");
    Assert.Equal(MsgType.Data, msg.MsgType);
    Assert.Equal("Echo", msg.CmdName);
    Assert.NotEqual(Guid.Empty, msg.CorrelationId);
  }

  [Fact]
  public void CommandMode_PrepareRespond()
  {
    var mode = new CommandMode();
    // First receive a command to establish the correlation
    var corrId = Guid.NewGuid();
    var dataEvents = mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = corrId, Payload = default, RawUserHeaders = default });
    var cmdName = dataEvents[0].ObjectName.Split('.')[1];

    var msg = mode.TryPrepareRespond("CON", new byte[] { 1, 2 }, cmdName);
    Assert.NotNull(msg);
    Assert.Equal(MsgType.Respond, msg.MsgType);
    Assert.Equal(cmdName, msg.CmdName);
    Assert.Equal(PostSendAction.CloseCommand, msg.PostAction);
    Assert.Equal(corrId, msg.CorrelationId);
  }

  [Fact]
  public void CommandMode_PrepareProgress()
  {
    var mode = new CommandMode();
    // First receive a command to establish the correlation
    var corrId = Guid.NewGuid();
    var dataEvents = mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = corrId, Payload = default, RawUserHeaders = default });
    var cmdName = dataEvents[0].ObjectName.Split('.')[1];

    var msg = mode.TryPrepareProgress("CON", new byte[] { 50 }, cmdName);
    Assert.NotNull(msg);
    Assert.Equal(MsgType.Progress, msg.MsgType);
    Assert.Equal(cmdName, msg.CmdName);
    Assert.Equal(PostSendAction.None, msg.PostAction);
    Assert.Equal(corrId, msg.CorrelationId);
  }

  [Fact]
  public void CommandMode_OnDisconnected_ClosesAllCommands()
  {
    var mode = new CommandMode();
    var corrA = Guid.NewGuid();
    var corrB = Guid.NewGuid();
    mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = corrA, Payload = default, RawUserHeaders = default });
    mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "", CorrelationId = corrB, Payload = default, RawUserHeaders = default });

    var events = mode.OnDisconnected("CON");

    // Should have Closed events for each command + the connection itself
    Assert.Equal(3, events.Count);
    Assert.All(events, e => Assert.Equal(EventType.Closed, e.Type));
  }

  [Fact]
  public void CommandMode_ReceiverNames_AreValidAplNames()
  {
    var mode = new CommandMode();
    // Simulate receiving several commands
    for (int i = 0; i < 20; i++) {
      var corrId = Guid.NewGuid();
      var events = mode.OnFrameReceived("CON", new FrameData {
        MsgType = MsgType.Data,
        CmdName = "",
        CorrelationId = corrId,
        Payload = default,
        RawUserHeaders = default
      });
      var name = events[0].ObjectName;
      var suffix = name[(name.LastIndexOf('.') + 1)..];
      // Must start with a letter (valid APL variable name)
      Assert.Matches(@"^[A-Za-z]", suffix);
      // Must match the Cmd######## pattern
      Assert.Matches(@"^Cmd\d{8}$", suffix);
    }
  }

  [Fact]
  public void CommandMode_ReceiverNames_GrowPastMinimumWidth()
  {
    var mode = new CommandMode();
    var counters = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string, int>>(
        typeof(CommandMode)
            .GetField("_nextRecvCmd", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(mode));
    counters["CON"] = 99_999_999;

    var events = mode.OnFrameReceived("CON", new FrameData {
      MsgType = MsgType.Data,
      CmdName = "",
      CorrelationId = Guid.NewGuid(),
      Payload = default,
      RawUserHeaders = default
    });

    Assert.EndsWith(".Cmd100000000", events[0].ObjectName);
  }

  [Fact]
  public void CommandMode_DoubleRespond_ReturnsNull()
  {
    var mode = new CommandMode();
    var corrId = Guid.NewGuid();
    mode.OnFrameReceived("CON", new FrameData {
      MsgType = MsgType.Data,
      CmdName = "",
      CorrelationId = corrId,
      Payload = default,
      RawUserHeaders = default
    });
    var cmdName = "Cmd00000000";

    // First respond should succeed
    var msg1 = mode.TryPrepareRespond("CON", new byte[] { 1 }, cmdName);
    Assert.NotNull(msg1);
    Assert.Equal(corrId, msg1.CorrelationId);

    // Second respond to same command should return null
    var msg2 = mode.TryPrepareRespond("CON", new byte[] { 2 }, cmdName);
    Assert.Null(msg2);
  }

  [Fact]
  public void CommandMode_ProgressAfterRespond_ReturnsNull()
  {
    var mode = new CommandMode();
    var corrId = Guid.NewGuid();
    mode.OnFrameReceived("CON", new FrameData {
      MsgType = MsgType.Data,
      CmdName = "",
      CorrelationId = corrId,
      Payload = default,
      RawUserHeaders = default
    });
    var cmdName = "Cmd00000000";

    // Respond first
    var respond = mode.TryPrepareRespond("CON", new byte[] { 1 }, cmdName);
    Assert.NotNull(respond);

    // Progress after respond should return null
    var progress = mode.TryPrepareProgress("CON", new byte[] { 2 }, cmdName);
    Assert.Null(progress);
  }

  [Fact]
  public void CommandMode_NoNamespaceCollision_AutoAndCmd()
  {
    var mode = new CommandMode();
    // Client-side: send with an "Auto00000000" command name (simulating ObjectRegistry auto-names)
    var outMsg = mode.PrepareOutbound("CON", new byte[] { 1 }, null, PostSendAction.None, "Auto00000000");
    Assert.NotEqual(Guid.Empty, outMsg.CorrelationId);

    // Server-side: receive a different command — should get Cmd00000000, not Auto00000000
    var corrId = Guid.NewGuid();
    var events = mode.OnFrameReceived("CON", new FrameData {
      MsgType = MsgType.Data,
      CmdName = "",
      CorrelationId = corrId,
      Payload = default,
      RawUserHeaders = default
    });
    var recvName = events[0].ObjectName;
    Assert.EndsWith(".Cmd00000000", recvName);

    // The received command is tracked as active
    Assert.True(mode.IsCommandActive("CON", "Cmd00000000"));

    // Responding to the outbound "Auto00000000" should work (correlation exists)
    var respondMsg = mode.TryPrepareRespond("CON", new byte[] { 2 }, "Auto00000000");
    Assert.NotNull(respondMsg);
    Assert.Equal(outMsg.CorrelationId, respondMsg.CorrelationId);

    // Responding to the received "Cmd00000000" should also work
    var respond2 = mode.TryPrepareRespond("CON", new byte[] { 3 }, "Cmd00000000");
    Assert.NotNull(respond2);
    Assert.Equal(corrId, respond2.CorrelationId);
  }
}
