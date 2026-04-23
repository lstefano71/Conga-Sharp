namespace CongaSharp.Tests.Marshalling;

using CongaSharp.Errors;
using CongaSharp.Marshalling;

using Xunit;

public class StringMarshallerTests
{
  [Fact]
  public unsafe void ReadFromPointer_NullReturnsNull()
  {
    Assert.Null(StringMarshaller.ReadFromPointer(null));
  }

  [Fact]
  public unsafe void ReadFromPointer_ReadsString()
  {
    fixed (char* ptr = "hello") {
      Assert.Equal("hello", StringMarshaller.ReadFromPointer(ptr));
    }
  }

  [Fact]
  public unsafe void ReadFromPointer_EmptyString()
  {
    fixed (char* ptr = "") {
      Assert.Equal("", StringMarshaller.ReadFromPointer(ptr));
    }
  }

  [Fact]
  public unsafe void WriteToBuffer_FitsExactly()
  {
    var buf = new char[6]; // "hello" + null = 6
    fixed (char* ptr = buf) {
      int required;
      var rc = StringMarshaller.WriteToBuffer("hello", ptr, 6, &required);
      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal(6, required);
      Assert.Equal("hello", new string(ptr));
    }
  }

  [Fact]
  public unsafe void WriteToBuffer_TooSmall()
  {
    var buf = new char[3];
    fixed (char* ptr = buf) {
      int required;
      var rc = StringMarshaller.WriteToBuffer("hello", ptr, 3, &required);
      Assert.Equal(ErrorCodes.BufferTooSmall, rc);
      Assert.Equal(6, required); // "hello" + null = 6
    }
  }

  [Fact]
  public unsafe void WriteToBuffer_NullBuffer()
  {
    int required;
    var rc = StringMarshaller.WriteToBuffer("hello", null, 0, &required);
    Assert.Equal(ErrorCodes.BufferTooSmall, rc);
    Assert.Equal(6, required);
  }

  [Fact]
  public unsafe void WriteToBuffer_EmptyString()
  {
    var buf = new char[1]; // just null terminator
    fixed (char* ptr = buf) {
      int required;
      var rc = StringMarshaller.WriteToBuffer("", ptr, 1, &required);
      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal(1, required);
      Assert.Equal('\0', buf[0]);
    }
  }

  [Fact]
  public unsafe void WriteToBuffer_UnicodeString()
  {
    string s = "héllo 世界";
    var buf = new char[s.Length + 1];
    fixed (char* ptr = buf) {
      var rc = StringMarshaller.WriteToBuffer(s, ptr, buf.Length, null);
      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal(s, new string(ptr));
    }
  }
}
