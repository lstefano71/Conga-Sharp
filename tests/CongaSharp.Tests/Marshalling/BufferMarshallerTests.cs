namespace CongaSharp.Tests.Marshalling;

using CongaSharp.Errors;
using CongaSharp.Marshalling;

using Xunit;

public class BufferMarshallerTests
{
  [Fact]
  public unsafe void ReadFromPointer_NullReturnsEmpty()
  {
    var span = BufferMarshaller.ReadFromPointer(null, 10);
    Assert.True(span.IsEmpty);
  }

  [Fact]
  public unsafe void ReadFromPointer_ZeroLengthReturnsEmpty()
  {
    byte b = 42;
    var span = BufferMarshaller.ReadFromPointer(&b, 0);
    Assert.True(span.IsEmpty);
  }

  [Fact]
  public unsafe void ReadFromPointer_ReadsCorrectly()
  {
    var data = new byte[] { 1, 2, 3, 4 };
    fixed (byte* ptr = data) {
      var span = BufferMarshaller.ReadFromPointer(ptr, 4);
      Assert.Equal(4, span.Length);
      Assert.Equal(1, span[0]);
      Assert.Equal(4, span[3]);
    }
  }

  [Fact]
  public unsafe void WriteToBuffer_FitsExactly()
  {
    var source = new byte[] { 10, 20, 30 };
    var dest = new byte[3];
    fixed (byte* ptr = dest) {
      int actual;
      var rc = BufferMarshaller.WriteToBuffer(source, ptr, 3, &actual);
      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal(3, actual);
      Assert.Equal(source, dest);
    }
  }

  [Fact]
  public unsafe void WriteToBuffer_TooSmall()
  {
    var source = new byte[] { 10, 20, 30 };
    var dest = new byte[2];
    fixed (byte* ptr = dest) {
      int actual;
      var rc = BufferMarshaller.WriteToBuffer(source, ptr, 2, &actual);
      Assert.Equal(ErrorCodes.BufferTooSmall, rc);
      Assert.Equal(3, actual);
    }
  }

  [Fact]
  public unsafe void WriteToBuffer_NullBuffer()
  {
    var source = new byte[] { 10, 20, 30 };
    int actual;
    var rc = BufferMarshaller.WriteToBuffer(source, null, 0, &actual);
    Assert.Equal(ErrorCodes.BufferTooSmall, rc);
    Assert.Equal(3, actual);
  }

  [Fact]
  public unsafe void WriteToBuffer_EmptyData()
  {
    var dest = new byte[5];
    fixed (byte* ptr = dest) {
      int actual;
      var rc = BufferMarshaller.WriteToBuffer(ReadOnlySpan<byte>.Empty, ptr, 5, &actual);
      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal(0, actual);
    }
  }
}
