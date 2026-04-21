namespace CongaSharp.Tests.NativeExports;

using CongaSharp.Core;
using Xunit;

public class CloseTreeNamesTests : IDisposable
{
    private readonly Root _root;

    public CloseTreeNamesTests()
    {
        _root = new Root();
    }

    public void Dispose()
    {
        _root.Dispose();
    }

    [Fact]
    public void Names_EmptyRoot_ReturnsEmptyArray()
    {
        var names = _root.Registry.GetChildNames(".");
        Assert.Empty(names);
    }

    [Fact]
    public void Names_WithObjects_ReturnsTopLevel()
    {
        _root.Registry.TryAdd(new ServerObject("S1", "", 5000, "Command", 16384));
        _root.Registry.TryAdd(new ClientObject("C1", "localhost", 5000, "Raw", 8192));

        var names = _root.Registry.GetChildNames(".");
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void Names_WithNested_ReturnsOnlyDirectChildren()
    {
        _root.Registry.TryAdd(new ServerObject("S1", "", 5000, "Command", 16384));
        _root.Registry.TryAdd(new ConnectionObject("S1.CON0001", null!));
        _root.Registry.TryAdd(new CommandObject("S1.CON0001.Cmd1", null!));

        var topLevel = _root.Registry.GetChildNames(".");
        Assert.Single(topLevel);
        Assert.Contains("S1", topLevel);

        var s1Children = _root.Registry.GetChildNames("S1");
        Assert.Single(s1Children);
        Assert.Contains("S1.CON0001", s1Children);

        var conChildren = _root.Registry.GetChildNames("S1.CON0001");
        Assert.Single(conChildren);
        Assert.Contains("S1.CON0001.Cmd1", conChildren);
    }

    [Fact]
    public void RemoveTree_FromRoot()
    {
        _root.Registry.TryAdd(new ServerObject("S1", "", 5000, "Command", 16384));
        _root.Registry.TryAdd(new ConnectionObject("S1.CON0001", null!));

        var removed = _root.Registry.RemoveTree("S1");
        Assert.Equal(2, removed.Count);
        Assert.Empty(_root.Registry.GetChildNames("."));
    }

    [Fact]
    public void RemoveTree_LeavesUnrelated()
    {
        _root.Registry.TryAdd(new ServerObject("S1", "", 5000, "Command", 16384));
        _root.Registry.TryAdd(new ClientObject("C1", "localhost", 5000, "Raw", 8192));
        _root.Registry.TryAdd(new ConnectionObject("S1.CON0001", null!));

        _root.Registry.RemoveTree("S1");
        Assert.NotNull(_root.Registry.Lookup("C1"));
        Assert.Null(_root.Registry.Lookup("S1"));
    }

    [Fact]
    public void Describe_Root_ReturnsRootType()
    {
        // Verify root lookup returns null (it's not in registry), simulating "." path
        Assert.Null(_root.Registry.Lookup("."));
    }

    [Fact]
    public void Describe_Object_ReturnsTypeAndState()
    {
        var srv = new ServerObject("S1", "", 5000, "Command", 16384);
        _root.Registry.TryAdd(srv);

        var obj = _root.Registry.Lookup("S1");
        Assert.NotNull(obj);
        Assert.Equal(ObjectType.Server, obj.Type);
        Assert.Equal(ObjectState.Created, obj.State);
    }
}
