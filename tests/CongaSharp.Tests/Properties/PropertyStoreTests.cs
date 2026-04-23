namespace CongaSharp.Tests.Properties;

using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Properties;

using Xunit;

public class PropertyStoreTests
{
  private readonly PropertyStore _serverStore = new(ObjectType.Server);
  private readonly PropertyStore _rootStore = new(ObjectType.Root);

  [Fact]
  public void Get_ReturnsDefault_WhenNotSet()
  {
    var rc = _serverStore.Get("Protocol", out var val);
    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Equal("\"IPv4\"", val);
  }

  [Fact]
  public void Set_And_Get_Roundtrip()
  {
    var rc = _serverStore.Set("Protocol", "\"IPv6\"");
    Assert.Equal(ErrorCodes.Success, rc);

    rc = _serverStore.Get("Protocol", out var val);
    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Equal("\"IPv6\"", val);
  }

  [Fact]
  public void Set_UnknownProperty_ReturnsError()
  {
    var rc = _serverStore.Set("NoSuchProp", "42");
    Assert.Equal(ErrorCodes.InvalidProperty, rc);
  }

  [Fact]
  public void Set_ReadOnlyProperty_ReturnsError()
  {
    var rc = _serverStore.Set("LocalAddr", "[\"127.0.0.1\",5000]");
    Assert.Equal(ErrorCodes.PropertyReadOnly, rc);
  }

  [Fact]
  public void Set_WrongObjectType_ReturnsError()
  {
    // Trace is only for Root, not Server
    var rc = _serverStore.Set("Trace", "2");
    Assert.Equal(ErrorCodes.InvalidProperty, rc);
  }

  [Fact]
  public void Get_PropList_ReturnsApplicableNames()
  {
    var rc = _serverStore.Get("PropList", out var val);
    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Contains("Protocol", val);
    Assert.Contains("KeepAlive", val);
    Assert.Contains("BufferSize", val);
  }

  [Fact]
  public void Get_UnknownProperty_ReturnsError()
  {
    var rc = _serverStore.Get("NoSuchProp", out _);
    Assert.Equal(ErrorCodes.InvalidProperty, rc);
  }

  [Fact]
  public void SetInternal_OverridesReadOnly()
  {
    _serverStore.SetInternal("LocalAddr", "[\"0.0.0.0\",5000]");
    var rc = _serverStore.Get("LocalAddr", out var val);
    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Equal("[\"0.0.0.0\",5000]", val);
  }

  [Fact]
  public void Set_EmptyValue_ReturnsInvalidJson()
  {
    var rc = _serverStore.Set("Protocol", "");
    Assert.Equal(ErrorCodes.InvalidJson, rc);
  }

  [Fact]
  public void Set_WhitespaceValue_ReturnsInvalidJson()
  {
    var rc = _serverStore.Set("Protocol", "   ");
    Assert.Equal(ErrorCodes.InvalidJson, rc);
  }

  [Fact]
  public void ToJson_ReturnsAllProperties()
  {
    var json = _serverStore.ToJson();
    Assert.Contains("\"Protocol\"", json);
    Assert.Contains("\"KeepAlive\"", json);
    Assert.StartsWith("{", json);
    Assert.EndsWith("}", json);
  }

  [Fact]
  public void Set_KeepAlive_ArrayValue()
  {
    var rc = _serverStore.Set("KeepAlive", "[1000,2000]");
    Assert.Equal(ErrorCodes.Success, rc);
    _serverStore.Get("KeepAlive", out var val);
    Assert.Equal("[1000,2000]", val);
  }

  [Fact]
  public void Set_EOM_NestedArrayValue()
  {
    var rc = _serverStore.Set("EOM", "[[13,10]]");
    Assert.Equal(ErrorCodes.Success, rc);
    _serverStore.Get("EOM", out var val);
    Assert.Equal("[[13,10]]", val);
  }

  [Fact]
  public void Root_TraceProperty()
  {
    var rc = _rootStore.Set("Trace", "3");
    Assert.Equal(ErrorCodes.Success, rc);
    _rootStore.Get("Trace", out var val);
    Assert.Equal("3", val);
  }

  [Fact]
  public void Root_TraceFileProperty()
  {
    var rc = _rootStore.Set("TraceFile", "\"C:\\\\logs\\\\conga.log\"");
    Assert.Equal(ErrorCodes.Success, rc);
    _rootStore.Get("TraceFile", out var val);
    Assert.Equal("\"C:\\\\logs\\\\conga.log\"", val);
  }
}
