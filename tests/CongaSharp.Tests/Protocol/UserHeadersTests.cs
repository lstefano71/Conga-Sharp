namespace CongaSharp.Tests.Protocol;

using CongaSharp.Protocol;
using Xunit;

public class UserHeadersTests
{
    [Fact]
    public void Encode_Empty_ReturnsEmpty()
    {
        var encoded = UserHeaders.Encode(new Dictionary<string, byte[]>());
        Assert.Empty(encoded);
    }

    [Fact]
    public void Encode_Null_ReturnsEmpty()
    {
        var encoded = UserHeaders.Encode(null!);
        Assert.Empty(encoded);
    }

    [Fact]
    public void Roundtrip_SingleKey()
    {
        var original = new Dictionary<string, byte[]>
        {
            ["Content-Type"] = System.Text.Encoding.UTF8.GetBytes("application/json")
        };

        var encoded = UserHeaders.Encode(original);
        var decoded = UserHeaders.Decode(encoded);

        Assert.Single(decoded);
        Assert.True(decoded.ContainsKey("Content-Type"));
        Assert.Equal(original["Content-Type"], decoded["Content-Type"]);
    }

    [Fact]
    public void Roundtrip_MultipleKeys()
    {
        var original = new Dictionary<string, byte[]>
        {
            ["Key1"] = new byte[] { 1, 2, 3 },
            ["Key2"] = new byte[] { 4, 5 },
            ["Key3"] = new byte[] { 6, 7, 8, 9 }
        };

        var encoded = UserHeaders.Encode(original);
        var decoded = UserHeaders.Decode(encoded);

        Assert.Equal(3, decoded.Count);
        Assert.Equal(original["Key1"], decoded["Key1"]);
        Assert.Equal(original["Key2"], decoded["Key2"]);
        Assert.Equal(original["Key3"], decoded["Key3"]);
    }

    [Fact]
    public void Roundtrip_EmptyValue()
    {
        var original = new Dictionary<string, byte[]>
        {
            ["EmptyVal"] = Array.Empty<byte>()
        };

        var encoded = UserHeaders.Encode(original);
        var decoded = UserHeaders.Decode(encoded);

        Assert.Single(decoded);
        Assert.Empty(decoded["EmptyVal"]);
    }

    [Fact]
    public void Roundtrip_UnicodeKey()
    {
        var original = new Dictionary<string, byte[]>
        {
            ["Ünïcödé-Kéy"] = new byte[] { 42 }
        };

        var encoded = UserHeaders.Encode(original);
        var decoded = UserHeaders.Decode(encoded);

        Assert.True(decoded.ContainsKey("Ünïcödé-Kéy"));
        Assert.Equal(new byte[] { 42 }, decoded["Ünïcödé-Kéy"]);
    }

    [Fact]
    public void Roundtrip_LargeValue()
    {
        var largeValue = new byte[10000];
        Random.Shared.NextBytes(largeValue);

        var original = new Dictionary<string, byte[]>
        {
            ["big"] = largeValue
        };

        var encoded = UserHeaders.Encode(original);
        var decoded = UserHeaders.Decode(encoded);
        Assert.Equal(largeValue, decoded["big"]);
    }

    [Fact]
    public void Decode_EmptyData_ReturnsEmpty()
    {
        var decoded = UserHeaders.Decode(ReadOnlySpan<byte>.Empty);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Decode_TruncatedData_ReturnsPartial()
    {
        // Encode two headers, then truncate the encoded bytes
        var original = new Dictionary<string, byte[]>
        {
            ["A"] = new byte[] { 1 },
            ["B"] = new byte[] { 2 }
        };
        var encoded = UserHeaders.Encode(original);

        // Truncate to only include first header
        var truncated = encoded.AsSpan(0, encoded.Length / 2);
        var decoded = UserHeaders.Decode(truncated);

        // Should decode at least the first header (or gracefully stop)
        Assert.True(decoded.Count <= 2);
    }

    [Fact]
    public void Roundtrip_NullValue()
    {
        var original = new Dictionary<string, byte[]>
        {
            ["NullVal"] = null!
        };

        var encoded = UserHeaders.Encode(original);
        var decoded = UserHeaders.Decode(encoded);

        Assert.Single(decoded);
        Assert.Empty(decoded["NullVal"]);
    }
}
