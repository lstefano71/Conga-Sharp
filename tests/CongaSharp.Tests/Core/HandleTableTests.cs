namespace CongaSharp.Tests.Core;

using CongaSharp.Core;
using Xunit;

public class HandleTableTests : IDisposable
{
    public HandleTableTests() => HandleTable.Reset();
    public void Dispose() => HandleTable.Reset();

    [Fact]
    public void Allocate_ReturnsNonZeroHandle()
    {
        var root = new Root();
        var handle = HandleTable.Allocate(root);
        Assert.NotEqual(nint.Zero, handle);
    }

    [Fact]
    public void Lookup_ReturnsAllocatedRoot()
    {
        var root = new Root();
        var handle = HandleTable.Allocate(root);
        Assert.Same(root, HandleTable.Lookup(handle));
    }

    [Fact]
    public void Lookup_ReturnsNull_ForUnknownHandle()
    {
        Assert.Null(HandleTable.Lookup((nint)99999));
    }

    [Fact]
    public void Free_RemovesHandle()
    {
        var root = new Root();
        var handle = HandleTable.Allocate(root);
        Assert.True(HandleTable.Free(handle));
        Assert.Null(HandleTable.Lookup(handle));
    }

    [Fact]
    public void Free_ReturnsFalse_ForUnknownHandle()
    {
        Assert.False(HandleTable.Free((nint)99999));
    }

    [Fact]
    public void MultipleRoots_GetUniqueHandles()
    {
        var r1 = new Root();
        var r2 = new Root();
        var h1 = HandleTable.Allocate(r1);
        var h2 = HandleTable.Allocate(r2);
        Assert.NotEqual(h1, h2);
        Assert.Same(r1, HandleTable.Lookup(h1));
        Assert.Same(r2, HandleTable.Lookup(h2));
    }

    [Fact]
    public void ConcurrentAllocate_AllSucceed()
    {
        const int count = 100;
        var roots = Enumerable.Range(0, count).Select(_ => new Root()).ToArray();
        var handles = new nint[count];

        Parallel.For(0, count, i =>
        {
            handles[i] = HandleTable.Allocate(roots[i]);
        });

        // All handles unique and non-zero
        var uniqueHandles = handles.Distinct().ToArray();
        Assert.Equal(count, uniqueHandles.Length);
        Assert.DoesNotContain(nint.Zero, uniqueHandles);
    }
}
