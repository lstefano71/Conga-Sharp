namespace CongaSharp.Tests.Core;

using CongaSharp.Core;

using Xunit;

public class ObjectRegistryTests
{
  private readonly ObjectRegistry _registry = new();

  [Fact]
  public void GenerateServerName_Increments()
  {
    Assert.Equal("S1", _registry.GenerateServerName());
    Assert.Equal("S2", _registry.GenerateServerName());
  }

  [Fact]
  public void GenerateClientName_Increments()
  {
    Assert.Equal("C1", _registry.GenerateClientName());
    Assert.Equal("C2", _registry.GenerateClientName());
  }

  [Fact]
  public void GenerateConnectionName_IncludesParent()
  {
    var name = _registry.GenerateConnectionName("S1");
    Assert.StartsWith("S1.CON", name);
  }

  [Fact]
  public void GenerateConnectionName_Increments()
  {
    var name1 = _registry.GenerateConnectionName("S1");
    var name2 = _registry.GenerateConnectionName("S1");
    Assert.Equal("S1.CON0001", name1);
    Assert.Equal("S1.CON0002", name2);
  }

  [Fact]
  public void TryAdd_And_Lookup()
  {
    var srv = new ServerObject("S1", "", 5000, "Command", 16384);
    Assert.True(_registry.TryAdd(srv));
    Assert.Same(srv, _registry.Lookup("S1"));
  }

  [Fact]
  public void TryAdd_Duplicate_ReturnsFalse()
  {
    var s1 = new ServerObject("S1", "", 5000, "Command", 16384);
    var s1dup = new ServerObject("S1", "", 6000, "Raw", 8192);
    Assert.True(_registry.TryAdd(s1));
    Assert.False(_registry.TryAdd(s1dup));
  }

  [Fact]
  public void Lookup_CaseInsensitive()
  {
    var srv = new ServerObject("S1", "", 5000, "Command", 16384);
    _registry.TryAdd(srv);
    Assert.Same(srv, _registry.Lookup("s1"));
  }

  [Fact]
  public void Lookup_NotFound_ReturnsNull()
  {
    Assert.Null(_registry.Lookup("NOPE"));
  }

  [Fact]
  public void GetChildNames_Root()
  {
    _registry.TryAdd(new ServerObject("S1", "", 5000, "Command", 16384));
    _registry.TryAdd(new ClientObject("C1", "localhost", 5000, "Command", 16384));
    _registry.TryAdd(new ConnectionObject("S1.CON0001", new ServerObject("S1", "", 5000, "Command", 16384)));

    var rootChildren = _registry.GetChildNames(".");
    Assert.Contains("S1", rootChildren);
    Assert.Contains("C1", rootChildren);
    Assert.DoesNotContain("S1.CON0001", rootChildren);
  }

  [Fact]
  public void GetChildNames_Server()
  {
    _registry.TryAdd(new ServerObject("S1", "", 5000, "Command", 16384));
    _registry.TryAdd(new ConnectionObject("S1.CON0001", new ServerObject("S1", "", 5000, "Command", 16384)));
    _registry.TryAdd(new ConnectionObject("S1.CON0002", new ServerObject("S1", "", 5000, "Command", 16384)));

    var children = _registry.GetChildNames("S1");
    Assert.Equal(2, children.Count);
    Assert.Contains("S1.CON0001", children);
    Assert.Contains("S1.CON0002", children);
  }

  [Fact]
  public void RemoveTree_RemovesObjectAndDescendants()
  {
    _registry.TryAdd(new ServerObject("S1", "", 5000, "Command", 16384));
    _registry.TryAdd(new ConnectionObject("S1.CON0001", new ServerObject("S1", "", 5000, "Command", 16384)));
    _registry.TryAdd(new CommandObject("S1.CON0001.Cmd1", new ConnectionObject("S1.CON0001", null!)));
    _registry.TryAdd(new ClientObject("C1", "localhost", 5000, "Command", 16384));

    var removed = _registry.RemoveTree("S1");
    Assert.Equal(3, removed.Count);
    Assert.Null(_registry.Lookup("S1"));
    Assert.Null(_registry.Lookup("S1.CON0001"));
    Assert.Null(_registry.Lookup("S1.CON0001.Cmd1"));
    Assert.NotNull(_registry.Lookup("C1"));
  }

  [Fact]
  public void RemoveTree_NonExistent_ReturnsEmpty()
  {
    var removed = _registry.RemoveTree("NOPE");
    Assert.Empty(removed);
  }

  [Fact]
  public void ConcurrentAdd_AllSucceed()
  {
    const int count = 50;
    var results = new bool[count];
    Parallel.For(0, count, i => {
      var name = $"OBJ{i}";
      var obj = new ServerObject(name, "", 5000 + i, "Command", 16384);
      results[i] = _registry.TryAdd(obj);
    });
    Assert.All(results, r => Assert.True(r));
    Assert.Equal(count, _registry.GetAllObjects().Count);
  }
}
