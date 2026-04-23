namespace CongaSharp.Diagnostics;

/// <summary>
/// Thread-safe file-based trace logger.
/// Each Root gets its own TraceLogger instance.
/// </summary>
public sealed class TraceLogger : IDisposable
{
  private readonly Lock _lock = new();
  private StreamWriter? _writer;
  private string? _filePath;
  private int _disposed;

  public TraceLevel Level { get; set; } = TraceLevel.Off;

  public string? FilePath {
    get => _filePath;
    set {
      lock (_lock) {
        CloseWriter();
        _filePath = value;
        if (!string.IsNullOrEmpty(value)) {
          var dir = Path.GetDirectoryName(value);
          if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
          _writer = new StreamWriter(value, append: true) { AutoFlush = true };
        }
      }
    }
  }

  public void Log(TraceLevel level, string message)
  {
    if (level > Level || Level == TraceLevel.Off) return;

    var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

    lock (_lock) {
      _writer?.WriteLine(line);
    }
  }

  public void LogError(string message) => Log(TraceLevel.Errors, message);
  public void LogConnection(string message) => Log(TraceLevel.Connections, message);
  public void LogMessage(string message) => Log(TraceLevel.Messages, message);
  public void LogWire(string message) => Log(TraceLevel.Wire, message);

  public void LogException(Exception ex)
  {
    LogError($"Exception: {ex.GetType().Name}: {ex.Message}");
  }

  private void CloseWriter()
  {
    _writer?.Dispose();
    _writer = null;
  }

  public void Dispose()
  {
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0) {
      lock (_lock) {
        CloseWriter();
      }
    }
  }
}
