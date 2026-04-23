namespace CongaSharp.Modes;

/// <summary>
/// Creates IConnectionMode instances from a ModeKind.
/// </summary>
public static class ModeFactory
{
  public static IConnectionMode Create(ModeKind kind) => kind switch {
    ModeKind.Raw => new RawMode(),
    ModeKind.Text => new TextMode(),
    ModeKind.BlkRaw => new BlkRawMode(),
    ModeKind.BlkText => new BlkTextMode(),
    ModeKind.Command => new CommandMode(),
    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown connection mode")
  };
}
