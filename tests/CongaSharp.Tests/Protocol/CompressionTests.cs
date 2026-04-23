namespace CongaSharp.Tests.Protocol;

using CongaSharp.Protocol;

using Xunit;

public class CompressionTests
{
  private static readonly byte[] TestData = System.Text.Encoding.UTF8.GetBytes(
      "Hello, this is test data for compression. " + new string('X', 1000));

  [Theory]
  [InlineData(CompressionAlgorithm.None)]
  [InlineData(CompressionAlgorithm.Deflate)]
  [InlineData(CompressionAlgorithm.LZ4)]
  [InlineData(CompressionAlgorithm.Zstd)]
  public void Roundtrip_AllAlgorithms(CompressionAlgorithm algo)
  {
    var compressed = Compression.Compress(algo, TestData);
    var decompressed = Compression.Decompress(algo, compressed);
    Assert.Equal(TestData, decompressed);
  }

  [Theory]
  [InlineData(CompressionAlgorithm.None)]
  [InlineData(CompressionAlgorithm.Deflate)]
  [InlineData(CompressionAlgorithm.LZ4)]
  [InlineData(CompressionAlgorithm.Zstd)]
  public void EmptyData_Roundtrip(CompressionAlgorithm algo)
  {
    var compressed = Compression.Compress(algo, ReadOnlySpan<byte>.Empty);
    var decompressed = Compression.Decompress(algo, compressed);
    Assert.Empty(decompressed);
  }

  [Theory]
  [InlineData(CompressionAlgorithm.Deflate)]
  [InlineData(CompressionAlgorithm.LZ4)]
  [InlineData(CompressionAlgorithm.Zstd)]
  public void CompressedData_IsSmallerThanOriginal(CompressionAlgorithm algo)
  {
    // With repetitive data, compression should reduce size
    var compressed = Compression.Compress(algo, TestData);
    Assert.True(compressed.Length < TestData.Length,
        $"{algo}: compressed {compressed.Length} >= original {TestData.Length}");
  }

  [Fact]
  public void None_ReturnsExactCopy()
  {
    var data = new byte[] { 1, 2, 3 };
    var result = Compression.Compress(CompressionAlgorithm.None, data);
    Assert.Equal(data, result);
  }

  [Theory]
  [InlineData(CompressionAlgorithm.Deflate)]
  [InlineData(CompressionAlgorithm.LZ4)]
  [InlineData(CompressionAlgorithm.Zstd)]
  public void LargeData_Roundtrip(CompressionAlgorithm algo)
  {
    var largeData = new byte[100_000];
    Random.Shared.NextBytes(largeData);
    var compressed = Compression.Compress(algo, largeData);
    var decompressed = Compression.Decompress(algo, compressed);
    Assert.Equal(largeData, decompressed);
  }

  [Fact]
  public void SingleByte_Roundtrip()
  {
    var data = new byte[] { 42 };
    foreach (var algo in Enum.GetValues<CompressionAlgorithm>()) {
      var compressed = Compression.Compress(algo, data);
      var decompressed = Compression.Decompress(algo, compressed);
      Assert.Equal(data, decompressed);
    }
  }
}
