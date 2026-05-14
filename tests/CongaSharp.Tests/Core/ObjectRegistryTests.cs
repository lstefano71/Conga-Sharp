namespace CongaSharp.Tests.Core;

using CongaSharp.Core;

using Xunit;

public class ObjectRegistryTests
{
  private readonly ObjectRegistry _registry = new();

  [Fact]
  public void GenerateServerName_Increments()
  {
    Assert.Equal("SRV00000000", _registry.GenerateServerName());
    Assert.Equal("SRV00000001", _registry.GenerateServerName());
  }

  [Fact]
  public void GenerateClientName_Increments()
  {
    Assert.Equal("CLT00000000", _registry.GenerateClientName());
    Assert.Equal("CLT00000001", _registry.GenerateClientName());
  }

  [Fact]
  public void GenerateConnectionName_IncludesParent()
  {
    var name = _registry.GenerateConnectionName("SRV00000000");
    Assert.StartsWith("SRV00000000.CON", name);
  }

  [Fact]
  public void GenerateConnectionName_Increments()
  {
    var name1 = _registry.GenerateConnectionName("SRV00000000");
    var name2 = _registry.GenerateConnectionName("SRV00000000");
    Assert.Equal("SRV00000000.CON00000000", name1);
    Assert.Equal("SRV00000000.CON00000001", name2);
  }

  [Fact]
  public void GenerateConnectionName_GrowsPastMinimumWidth()
  {
    var counters = GetPrivateField<System.Collections.Concurrent.ConcurrentDictionary<string, int>>(
        _registry, "_nextConnection");
    counters["SRV00000000"] = 9999;

    Assert.Equal("SRV00000000.CON00010000", _registry.GenerateConnectionName("SRV00000000"));
  }

  [Fact]
  public void GenerateAutoName_GrowsPastMinimumWidth()
  {
    var counters = GetPrivateField<System.Collections.Concurrent.ConcurrentDictionary<string, int>>(
        _registry, "_nextAuto");
    counters["CLT00000000"] = 99_999_999;

    Assert.Equal("CLT00000000.Auto100000000", _registry.GenerateAutoName("CLT00000000"));
  }

  [Fact]
  public void TryAdd_And_Lookup()
  {
    var srv = new ServerObject("SRV00000000", "", 5000, "Command", 16384);
    Assert.True(_registry.TryAdd(srv));
    Assert.Same(srv, _registry.Lookup("SRV00000000"));
  }

  [Fact]
  public void TryAdd_Duplicate_ReturnsFalse()
  {
    var s1 = new ServerObject("SRV00000000", "", 5000, "Command", 16384);
    var s1dup = new ServerObject("SRV00000000", "", 6000, "Raw", 8192);
    Assert.True(_registry.TryAdd(s1));
    Assert.False(_registry.TryAdd(s1dup));
  }

  [Fact]
  public void Lookup_CaseInsensitive()
  {
    var srv = new ServerObject("SRV00000000", "", 5000, "Command", 16384);
    _registry.TryAdd(srv);
    Assert.Same(srv, _registry.Lookup("srv00000000"));
  }

  [Fact]
  public void Lookup_NotFound_ReturnsNull()
  {
    Assert.Null(_registry.Lookup("NOPE"));
  }

  [Fact]
  public void GetChildNames_Root()
  {
    _registry.TryAdd(new ServerObject("SRV00000000", "", 5000, "Command", 16384));
    _registry.TryAdd(new ClientObject("CLT00000000", "localhost", 5000, "Command", 16384));
    _registry.TryAdd(new ConnectionObject("SRV00000000.CON00000000", new ServerObject("SRV00000000", "", 5000, "Command", 16384)));

    var rootChildren = _registry.GetChildNames(".");
    Assert.Contains("SRV00000000", rootChildren);
    Assert.Contains("CLT00000000", rootChildren);
    Assert.DoesNotContain("SRV00000000.CON00000000", rootChildren);
  }

  [Fact]
  public void GetChildNames_Server()
  {
    _registry.TryAdd(new ServerObject("SRV00000000", "", 5000, "Command", 16384));
    _registry.TryAdd(new ConnectionObject("SRV00000000.CON00000000", new ServerObject("SRV00000000", "", 5000, "Command", 16384)));
    _registry.TryAdd(new ConnectionObject("SRV00000000.CON00000001", new ServerObject("SRV00000000", "", 5000, "Command", 16384)));

    var children = _registry.GetChildNames("SRV00000000");
    Assert.Equal(2, children.Count);
    Assert.Contains("SRV00000000.CON00000000", children);
    Assert.Contains("SRV00000000.CON00000001", children);
  }

  [Fact]
  public void RemoveTree_RemovesObjectAndDescendants()
  {
    _registry.TryAdd(new ServerObject("SRV00000000", "", 5000, "Command", 16384));
    _registry.TryAdd(new ConnectionObject("SRV00000000.CON00000000", new ServerObject("SRV00000000", "", 5000, "Command", 16384)));
    _registry.TryAdd(new CommandObject("SRV00000000.CON00000000.Cmd1", new ConnectionObject("SRV00000000.CON00000000", null!)));
    _registry.TryAdd(new ClientObject("CLT00000000", "localhost", 5000, "Command", 16384));

    var removed = _registry.RemoveTree("SRV00000000");
    Assert.Equal(3, removed.Count);
    Assert.Null(_registry.Lookup("SRV00000000"));
    Assert.Null(_registry.Lookup("SRV00000000.CON00000000"));
    Assert.Null(_registry.Lookup("SRV00000000.CON00000000.Cmd1"));
    Assert.NotNull(_registry.Lookup("CLT00000000"));
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

  private static T GetPrivateField<T>(object target, string fieldName)
  {
    var field = target.GetType().GetField(
        fieldName,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Assert.NotNull(field);
    return Assert.IsType<T>(field!.GetValue(target));
  }
}
