using CongaSharp.Errors;
using Xunit;

namespace CongaSharp.Tests;

public class ErrorCodesTests
{
    [Fact]
    public void SuccessIsZero()
    {
        Assert.Equal(0, ErrorCodes.Success);
    }

    [Fact]
    public void CongaSharpCodesStartAt2000()
    {
        Assert.True(ErrorCodes.BufferTooSmall >= 2000);
        Assert.True(ErrorCodes.ShuttingDown >= 2000);
        Assert.True(ErrorCodes.CrcFailure >= 2000);
    }
}
