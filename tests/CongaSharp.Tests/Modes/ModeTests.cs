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
        var events = mode.OnBytesReceived("S1.CON0001", new byte[] { 1, 2, 3 });
        Assert.Single(events);
        Assert.Equal(EventType.Receive, events[0].Type);
        Assert.Equal("S1.CON0001", events[0].ObjectName);
        Assert.Equal(new byte[] { 1, 2, 3 }, events[0].Payload);
    }

    [Fact]
    public void RawMode_OnBytesReceived_EmptyData_NoEvents()
    {
        var mode = new RawMode();
        var events = mode.OnBytesReceived("S1.CON0001", ReadOnlySpan<byte>.Empty);
        Assert.Empty(events);
    }

    [Fact]
    public void RawMode_InvalidCloseFlags()
    {
        var mode = new RawMode();
        var msg = mode.PrepareOutbound("S1.CON0001", [1, 2], null, PostSendAction.CloseCommand, null);
        Assert.NotEqual(0, msg.ErrorCode);

        msg = mode.PrepareOutbound("S1.CON0001", [1, 2], null, PostSendAction.EmitSentEvent, null);
        Assert.NotEqual(0, msg.ErrorCode);
    }

    [Fact]
    public void RawMode_ValidSend()
    {
        var mode = new RawMode();
        var msg = mode.PrepareOutbound("S1.CON0001", [1, 2, 3], null, PostSendAction.None, null);
        Assert.Equal(0, msg.ErrorCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, msg.Payload);
    }

    [Fact]
    public void RawMode_OnDisconnected()
    {
        var mode = new RawMode();
        var events = mode.OnDisconnected("S1.CON0001");
        Assert.Single(events);
        Assert.Equal(EventType.Closed, events[0].Type);
    }

    [Fact]
    public void RawMode_ThrowsOnFrameReceived()
    {
        var mode = new RawMode();
        Assert.Throws<InvalidOperationException>(() => mode.OnFrameReceived("S1.CON0001",
            new FrameData { MsgType = MsgType.Data, CmdName = "", Payload = [], UserHeaders = new() }));
    }

    // --- TextMode ---

    [Fact]
    public void TextMode_NoEOM_BehavesLikeRaw()
    {
        var mode = new TextMode();
        var events = mode.OnBytesReceived("CON", [72, 101, 108, 108, 111]);
        Assert.Single(events);
        Assert.Equal(EventType.Receive, events[0].Type);
        Assert.Equal(new byte[] { 72, 101, 108, 108, 111 }, events[0].Payload);
    }

    [Fact]
    public void TextMode_WithEOM_SplitsMessages()
    {
        var mode = new TextMode();
        mode.EomPatterns.Add([13, 10]); // CRLF

        // "Hello\r\nWorld\r\n"
        var data = new byte[] { 72, 101, 108, 108, 111, 13, 10, 87, 111, 114, 108, 100, 13, 10 };
        var events = mode.OnBytesReceived("CON", data);

        Assert.Equal(2, events.Count);
        Assert.Equal(new byte[] { 72, 101, 108, 108, 111, 13, 10 }, events[0].Payload);
        Assert.Equal(new byte[] { 87, 111, 114, 108, 100, 13, 10 }, events[1].Payload);
    }

    [Fact]
    public void TextMode_WithEOM_AccumulatesPartial()
    {
        var mode = new TextMode();
        mode.EomPatterns.Add([13, 10]);

        // Partial: "Hel"
        var events1 = mode.OnBytesReceived("CON", new byte[] { 72, 101, 108 });
        Assert.Empty(events1);

        // Complete: "lo\r\n"
        var events2 = mode.OnBytesReceived("CON", new byte[] { 108, 111, 13, 10 });
        Assert.Single(events2);
        Assert.Equal(new byte[] { 72, 101, 108, 108, 111, 13, 10 }, events2[0].Payload);
    }

    [Fact]
    public void TextMode_OnDisconnected_FlushesBuffer()
    {
        var mode = new TextMode();
        mode.EomPatterns.Add([13, 10]);

        mode.OnBytesReceived("CON", new byte[] { 72, 101, 108 });
        var events = mode.OnDisconnected("CON");

        Assert.Equal(2, events.Count);
        Assert.Equal(EventType.Receive, events[0].Type);
        Assert.Equal(new byte[] { 72, 101, 108 }, events[0].Payload);
        Assert.Equal(EventType.Closed, events[1].Type);
    }

    [Fact]
    public void TextMode_ConfigureEomFromJson()
    {
        var mode = new TextMode();
        mode.ConfigureEomFromJson("[[13,10],[10]]");
        Assert.Equal(2, mode.EomPatterns.Count);
        Assert.Equal(new byte[] { 13, 10 }, mode.EomPatterns[0]);
        Assert.Equal(new byte[] { 10 }, mode.EomPatterns[1]);
    }

    [Fact]
    public void TextMode_ConfigureEomFromJson_Empty()
    {
        var mode = new TextMode();
        mode.ConfigureEomFromJson("[]");
        Assert.Empty(mode.EomPatterns);

        mode.ConfigureEomFromJson("");
        Assert.Empty(mode.EomPatterns);
    }

    [Fact]
    public void TextMode_ResetState_ClearsBuffer()
    {
        var mode = new TextMode();
        mode.EomPatterns.Add([13, 10]);
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
        var frame = new FrameData { MsgType = MsgType.Data, CmdName = "", Payload = [1, 2, 3], UserHeaders = new() };
        var events = mode.OnFrameReceived("CON", frame);
        Assert.Single(events);
        Assert.Equal(EventType.Block, events[0].Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, events[0].Payload);
    }

    [Fact]
    public void BlkRawMode_InvalidCloseCommand()
    {
        var mode = new BlkRawMode();
        var msg = mode.PrepareOutbound("CON", [1, 2], null, PostSendAction.CloseCommand, null);
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
        var frame = new FrameData { MsgType = MsgType.Data, CmdName = "Echo", Payload = [42], UserHeaders = new() };
        var events = mode.OnFrameReceived("CON", frame);

        Assert.Single(events);
        Assert.Equal(EventType.Receive, events[0].Type);
        Assert.Equal("CON.Echo", events[0].ObjectName);
    }

    [Fact]
    public void CommandMode_OnFrameReceived_ProgressCreatesProgressEvent()
    {
        var mode = new CommandMode();
        mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "Job", Payload = [], UserHeaders = new() });

        var frame = new FrameData { MsgType = MsgType.Progress, CmdName = "Job", Payload = [50], UserHeaders = new() };
        var events = mode.OnFrameReceived("CON", frame);

        Assert.Single(events);
        Assert.Equal(EventType.Progress, events[0].Type);
        Assert.Equal("CON.Job", events[0].ObjectName);
    }

    [Fact]
    public void CommandMode_OnFrameReceived_RespondClosesCommand()
    {
        var mode = new CommandMode();
        mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "Echo", Payload = [], UserHeaders = new() });
        Assert.True(mode.IsCommandActive("CON", "Echo"));

        var frame = new FrameData { MsgType = MsgType.Respond, CmdName = "Echo", Payload = [99], UserHeaders = new() };
        var events = mode.OnFrameReceived("CON", frame);

        Assert.Single(events);
        Assert.Equal(EventType.Receive, events[0].Type);
        Assert.False(mode.IsCommandActive("CON", "Echo"));
    }

    [Fact]
    public void CommandMode_PrepareRespond()
    {
        var mode = new CommandMode();
        var msg = mode.PrepareRespond("CON", [1, 2], "Echo");
        Assert.Equal(MsgType.Respond, msg.MsgType);
        Assert.Equal("Echo", msg.CmdName);
        Assert.Equal(PostSendAction.CloseCommand, msg.PostAction);
    }

    [Fact]
    public void CommandMode_PrepareProgress()
    {
        var mode = new CommandMode();
        var msg = mode.PrepareProgress("CON", [50], "Job");
        Assert.Equal(MsgType.Progress, msg.MsgType);
        Assert.Equal("Job", msg.CmdName);
        Assert.Equal(PostSendAction.None, msg.PostAction);
    }

    [Fact]
    public void CommandMode_OnDisconnected_ClosesAllCommands()
    {
        var mode = new CommandMode();
        mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "A", Payload = [], UserHeaders = new() });
        mode.OnFrameReceived("CON", new FrameData { MsgType = MsgType.Data, CmdName = "B", Payload = [], UserHeaders = new() });

        var events = mode.OnDisconnected("CON");

        // Should have Closed events for each command + the connection itself
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(EventType.Closed, e.Type));
    }
}
