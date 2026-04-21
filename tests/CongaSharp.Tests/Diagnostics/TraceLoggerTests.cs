namespace CongaSharp.Tests.Diagnostics;

using CongaSharp.Diagnostics;
using Xunit;

public class TraceLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public TraceLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CongaSharpTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void DefaultLevel_IsOff()
    {
        using var logger = new TraceLogger();
        Assert.Equal(TraceLevel.Off, logger.Level);
    }

    [Fact]
    public void Log_DoesNothing_WhenOff()
    {
        var path = Path.Combine(_tempDir, "off.log");
        using var logger = new TraceLogger { Level = TraceLevel.Off, FilePath = path };
        logger.LogError("should not appear");
        logger.Dispose();
        
        var content = File.ReadAllText(path);
        Assert.Empty(content);
    }

    [Fact]
    public void LogError_WritesWhenLevelIsErrors()
    {
        var path = Path.Combine(_tempDir, "errors.log");
        using var logger = new TraceLogger { Level = TraceLevel.Errors, FilePath = path };
        logger.LogError("test error");
        logger.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("[Errors]", content);
        Assert.Contains("test error", content);
    }

    [Fact]
    public void LogConnection_Suppressed_WhenLevelIsErrors()
    {
        var path = Path.Combine(_tempDir, "suppress.log");
        using var logger = new TraceLogger { Level = TraceLevel.Errors, FilePath = path };
        logger.LogConnection("should not appear");
        logger.Dispose();

        var content = File.ReadAllText(path);
        Assert.DoesNotContain("should not appear", content);
    }

    [Fact]
    public void HigherLevel_IncludesLowerMessages()
    {
        var path = Path.Combine(_tempDir, "all.log");
        using var logger = new TraceLogger { Level = TraceLevel.Wire, FilePath = path };
        logger.LogError("err");
        logger.LogConnection("conn");
        logger.LogMessage("msg");
        logger.LogWire("wire");
        logger.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("err", content);
        Assert.Contains("conn", content);
        Assert.Contains("msg", content);
        Assert.Contains("wire", content);
    }

    [Fact]
    public void LogIncludesTimestamp()
    {
        var path = Path.Combine(_tempDir, "ts.log");
        using var logger = new TraceLogger { Level = TraceLevel.Errors, FilePath = path };
        logger.LogError("timestamped");
        logger.Dispose();

        var content = File.ReadAllText(path);
        // Should contain a date-like pattern
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}", content);
    }

    [Fact]
    public void ChangeFilePath_SwitchesFile()
    {
        var path1 = Path.Combine(_tempDir, "file1.log");
        var path2 = Path.Combine(_tempDir, "file2.log");
        using var logger = new TraceLogger { Level = TraceLevel.Errors, FilePath = path1 };
        logger.LogError("in file1");
        logger.FilePath = path2;
        logger.LogError("in file2");
        logger.Dispose();

        Assert.Contains("in file1", File.ReadAllText(path1));
        Assert.Contains("in file2", File.ReadAllText(path2));
        Assert.DoesNotContain("in file2", File.ReadAllText(path1));
    }

    [Fact]
    public void ConcurrentWrites_DoNotCorrupt()
    {
        var path = Path.Combine(_tempDir, "concurrent.log");
        using var logger = new TraceLogger { Level = TraceLevel.Wire, FilePath = path };

        Parallel.For(0, 100, i =>
        {
            logger.LogWire($"message-{i}");
        });
        logger.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.Equal(100, lines.Length);
    }

    [Fact]
    public void LogException_FormatsCorrectly()
    {
        var path = Path.Combine(_tempDir, "ex.log");
        using var logger = new TraceLogger { Level = TraceLevel.Errors, FilePath = path };
        logger.LogException(new InvalidOperationException("test failure"));
        logger.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("test failure", content);
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        var logger = new TraceLogger { Level = TraceLevel.Errors };
        logger.Dispose();
        logger.Dispose(); // should not throw
    }

    [Fact]
    public void CreatesDirectoryIfNeeded()
    {
        var nestedDir = Path.Combine(_tempDir, "sub", "dir");
        var path = Path.Combine(nestedDir, "nested.log");
        using var logger = new TraceLogger { Level = TraceLevel.Errors, FilePath = path };
        logger.LogError("nested");
        logger.Dispose();

        Assert.True(File.Exists(path));
        Assert.Contains("nested", File.ReadAllText(path));
    }
}
