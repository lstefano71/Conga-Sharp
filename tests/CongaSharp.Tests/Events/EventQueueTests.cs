namespace CongaSharp.Tests.Events;

using CongaSharp.Events;
using Xunit;

public class EventQueueTests
{
    [Fact]
    public void Enqueue_Dequeue_SingleEvent()
    {
        using var queue = new EventQueue();
        var evt = new CongaEvent { ObjectName = "S1.CON0001", Type = EventType.Receive, Payload = [1, 2, 3] };
        queue.Enqueue(evt);

        var result = queue.Wait(null, 1000);
        Assert.Equal(EventType.Receive, result.Type);
        Assert.Equal("S1.CON0001", result.ObjectName);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Payload);
    }

    [Fact]
    public void Wait_TimesOut_WhenEmpty()
    {
        using var queue = new EventQueue();
        var result = queue.Wait(null, 50);
        Assert.Equal(EventType.Timeout, result.Type);
    }

    [Fact]
    public void Wait_FiltersByObjectName_ExactMatch()
    {
        using var queue = new EventQueue();
        queue.Enqueue(new CongaEvent { ObjectName = "C1", Type = EventType.Receive });
        queue.Enqueue(new CongaEvent { ObjectName = "S1", Type = EventType.Connect });

        var result = queue.Wait("S1", 1000);
        Assert.Equal(EventType.Connect, result.Type);
        Assert.Equal("S1", result.ObjectName);

        // C1 event should still be in the queue
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Wait_FiltersByObjectName_PrefixMatch()
    {
        using var queue = new EventQueue();
        queue.Enqueue(new CongaEvent { ObjectName = "S1.CON0001", Type = EventType.Receive });
        queue.Enqueue(new CongaEvent { ObjectName = "C1", Type = EventType.Connect });

        // Filtering on "S1" should match "S1.CON0001"
        var result = queue.Wait("S1", 1000);
        Assert.Equal(EventType.Receive, result.Type);
        Assert.Equal("S1.CON0001", result.ObjectName);
    }

    [Fact]
    public void Wait_FiltersByObjectName_PrefixDoesNotMatchPartialName()
    {
        using var queue = new EventQueue();
        queue.Enqueue(new CongaEvent { ObjectName = "S10", Type = EventType.Receive });

        // "S1" should NOT match "S10" (not a dot boundary)
        var result = queue.Wait("S1", 50);
        Assert.Equal(EventType.Timeout, result.Type);
    }

    [Fact]
    public void Wait_MatchesDeepChildren()
    {
        using var queue = new EventQueue();
        queue.Enqueue(new CongaEvent { ObjectName = "S1.CON0001.MyCmd", Type = EventType.Progress });

        var result = queue.Wait("S1", 1000);
        Assert.Equal(EventType.Progress, result.Type);
        Assert.Equal("S1.CON0001.MyCmd", result.ObjectName);
    }

    [Fact]
    public void Wait_NullOrDotFilter_MatchesAll()
    {
        using var queue = new EventQueue();
        queue.Enqueue(new CongaEvent { ObjectName = "C1", Type = EventType.Connect });

        var result1 = queue.Wait(null, 1000);
        Assert.Equal(EventType.Connect, result1.Type);

        queue.Enqueue(new CongaEvent { ObjectName = "S1", Type = EventType.Connect });
        var result2 = queue.Wait(".", 1000);
        Assert.Equal(EventType.Connect, result2.Type);
    }

    [Fact]
    public void Wait_BlocksUntilEventEnqueued()
    {
        using var queue = new EventQueue();
        CongaEvent? result = null;

        var waiter = Task.Run(() => result = queue.Wait(null, 5000));

        Thread.Sleep(100);
        queue.Enqueue(new CongaEvent { ObjectName = "S1", Type = EventType.Connect });

        waiter.Wait(2000);
        Assert.NotNull(result);
        Assert.Equal(EventType.Connect, result!.Type);
    }

    [Fact]
    public void ConcurrentWaiters_EachGetsOwnEvent()
    {
        using var queue = new EventQueue();
        CongaEvent? result1 = null;
        CongaEvent? result2 = null;

        var waiter1 = Task.Run(() => result1 = queue.Wait("S1", 5000));
        var waiter2 = Task.Run(() => result2 = queue.Wait("C1", 5000));

        Thread.Sleep(100);
        queue.Enqueue(new CongaEvent { ObjectName = "C1", Type = EventType.Connect });
        queue.Enqueue(new CongaEvent { ObjectName = "S1.CON0001", Type = EventType.Receive });

        Task.WaitAll(waiter1, waiter2);

        Assert.NotNull(result1);
        Assert.Equal("S1.CON0001", result1!.ObjectName);

        Assert.NotNull(result2);
        Assert.Equal("C1", result2!.ObjectName);
    }

    [Fact]
    public void Shutdown_UnblocksWaiters()
    {
        using var queue = new EventQueue();
        CongaEvent? result = null;

        var waiter = Task.Run(() => result = queue.Wait(null, 30000));

        Thread.Sleep(100);
        queue.SignalShutdown();

        waiter.Wait(2000);
        Assert.NotNull(result);
        Assert.Equal(EventType.Error, result!.Type);
    }

    [Fact]
    public void Shutdown_FutureWaitsReturnImmediately()
    {
        using var queue = new EventQueue();
        queue.SignalShutdown();

        var result = queue.Wait(null, 30000);
        Assert.Equal(EventType.Error, result.Type);
    }

    [Fact]
    public void Enqueue_AfterShutdown_IsIgnored()
    {
        using var queue = new EventQueue();
        queue.SignalShutdown();

        queue.Enqueue(new CongaEvent { ObjectName = "S1", Type = EventType.Connect });
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CancellationToken_UnblocksWait()
    {
        using var queue = new EventQueue();
        using var cts = new CancellationTokenSource();

        var waiter = Task.Run(() => queue.Wait(null, 30000, cts.Token));

        Thread.Sleep(100);
        cts.Cancel();

        var result = waiter.Result;
        Assert.Equal(EventType.Error, result.Type);
    }

    [Fact]
    public void FIFO_Order_Preserved()
    {
        using var queue = new EventQueue();
        queue.Enqueue(new CongaEvent { ObjectName = "A", Type = EventType.Connect });
        queue.Enqueue(new CongaEvent { ObjectName = "B", Type = EventType.Receive });
        queue.Enqueue(new CongaEvent { ObjectName = "C", Type = EventType.Closed });

        Assert.Equal("A", queue.Wait(null, 100).ObjectName);
        Assert.Equal("B", queue.Wait(null, 100).ObjectName);
        Assert.Equal("C", queue.Wait(null, 100).ObjectName);
    }

    [Fact]
    public void Count_ReflectsQueueSize()
    {
        using var queue = new EventQueue();
        Assert.Equal(0, queue.Count);

        queue.Enqueue(new CongaEvent { ObjectName = "S1", Type = EventType.Connect });
        queue.Enqueue(new CongaEvent { ObjectName = "S1", Type = EventType.Receive });
        Assert.Equal(2, queue.Count);

        queue.Wait(null, 100);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void EventName_And_EventCode_Properties()
    {
        var evt = new CongaEvent { ObjectName = "S1", Type = EventType.Progress };
        Assert.Equal("Progress", evt.EventName);
        Assert.Equal(5, evt.EventCode);
    }
}
