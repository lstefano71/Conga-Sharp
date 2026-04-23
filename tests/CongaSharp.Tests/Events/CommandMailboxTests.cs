namespace CongaSharp.Tests.Events;

using CongaSharp.Events;

using Xunit;

public class CommandMailboxTests
{
  [Fact]
  public void PostAndReceive_BasicRoundtrip()
  {
    using var mailbox = new CommandMailbox();
    var evt = new CongaEvent { ObjectName = "C1.Auto0", Type = EventType.Receive, Payload = new byte[] { 1, 2, 3 } };

    Assert.True(mailbox.Post(evt));
    var result = mailbox.TryReceive(1000);

    Assert.NotNull(result);
    Assert.Equal(EventType.Receive, result!.Type);
    Assert.Equal("C1.Auto0", result.ObjectName);
    Assert.Equal(new byte[] { 1, 2, 3 }, result.Payload.ToArray());
  }

  [Fact]
  public void TryReceive_ReturnsNull_OnTimeout()
  {
    using var mailbox = new CommandMailbox();
    var result = mailbox.TryReceive(50);
    Assert.Null(result);
  }

  [Fact]
  public void TryReceive_BlocksUntilPost()
  {
    using var mailbox = new CommandMailbox();
    CongaEvent? result = null;

    var waiter = Task.Run(() => result = mailbox.TryReceive(5000));

    Thread.Sleep(100);
    mailbox.Post(new CongaEvent { ObjectName = "C1.Cmd", Type = EventType.Progress });

    waiter.Wait(2000);
    Assert.NotNull(result);
    Assert.Equal(EventType.Progress, result!.Type);
  }

  [Fact]
  public void Complete_AllowsRemainingReads()
  {
    using var mailbox = new CommandMailbox();
    mailbox.Post(new CongaEvent { ObjectName = "C1.Cmd", Type = EventType.Progress });
    mailbox.Post(new CongaEvent { ObjectName = "C1.Cmd", Type = EventType.Receive, IsTerminal = true });
    mailbox.Complete();

    var r1 = mailbox.TryReceive(100);
    Assert.NotNull(r1);
    Assert.Equal(EventType.Progress, r1!.Type);
    Assert.False(mailbox.IsCompleted); // still has events

    var r2 = mailbox.TryReceive(100);
    Assert.NotNull(r2);
    Assert.Equal(EventType.Receive, r2!.Type);
    Assert.True(r2.IsTerminal);
    Assert.True(mailbox.IsCompleted); // empty + complete
  }

  [Fact]
  public void Complete_UnblocksWaiter()
  {
    using var mailbox = new CommandMailbox();
    CongaEvent? result = null;

    var waiter = Task.Run(() => result = mailbox.TryReceive(5000));
    Thread.Sleep(100);
    mailbox.Complete();

    waiter.Wait(2000);
    Assert.Null(result);
  }

  [Fact]
  public void Count_ReflectsBufferedEvents()
  {
    using var mailbox = new CommandMailbox();
    Assert.Equal(0, mailbox.Count);

    mailbox.Post(new CongaEvent { ObjectName = "X", Type = EventType.Receive });
    Assert.Equal(1, mailbox.Count);

    mailbox.Post(new CongaEvent { ObjectName = "X", Type = EventType.Progress });
    Assert.Equal(2, mailbox.Count);

    mailbox.TryReceive(100);
    Assert.Equal(1, mailbox.Count);
  }

  [Fact]
  public void Cancellation_UnblocksReceive()
  {
    using var mailbox = new CommandMailbox();
    using var cts = new CancellationTokenSource();

    var waiter = Task.Run(() => mailbox.TryReceive(30000, cts.Token));
    Thread.Sleep(100);
    cts.Cancel();

    var result = waiter.Result;
    Assert.Null(result);
  }

  [Fact]
  public void FIFO_OrderPreserved()
  {
    using var mailbox = new CommandMailbox();
    mailbox.Post(new CongaEvent { ObjectName = "A", Type = EventType.Progress });
    mailbox.Post(new CongaEvent { ObjectName = "B", Type = EventType.Receive });

    Assert.Equal("A", mailbox.TryReceive(100)!.ObjectName);
    Assert.Equal("B", mailbox.TryReceive(100)!.ObjectName);
  }

  [Fact]
  public void Requeue_AfterComplete_PreservesEvent()
  {
    using var mailbox = new CommandMailbox();
    mailbox.Post(new CongaEvent { ObjectName = "C1.Cmd", Type = EventType.Receive, IsTerminal = true });
    mailbox.Complete();

    var terminal = mailbox.TryReceive(100);
    Assert.NotNull(terminal);
    Assert.True(mailbox.IsCompleted);

    mailbox.Requeue(terminal!);
    var replay = mailbox.TryReceive(100);
    Assert.NotNull(replay);
    Assert.Equal(EventType.Receive, replay!.Type);
  }
}
