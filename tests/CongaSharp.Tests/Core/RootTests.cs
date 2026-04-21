namespace CongaSharp.Tests.Core;

using CongaSharp.Core;
using Xunit;

public class RootTests
{
    [Fact]
    public void NewRoot_IsNotShuttingDown()
    {
        using var root = new Root();
        Assert.False(root.IsShuttingDown);
    }

    [Fact]
    public void Shutdown_SetsFlag()
    {
        using var root = new Root();
        root.Shutdown();
        Assert.True(root.IsShuttingDown);
    }

    [Fact]
    public void Shutdown_CancelsToken()
    {
        using var root = new Root();
        var token = root.ShutdownToken;
        Assert.False(token.IsCancellationRequested);
        root.Shutdown();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void DoubleShutdown_DoesNotThrow()
    {
        using var root = new Root();
        root.Shutdown();
        root.Shutdown(); // should not throw
        Assert.True(root.IsShuttingDown);
    }
}
