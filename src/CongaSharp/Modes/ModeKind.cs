namespace CongaSharp.Modes;

/// <summary>
/// Connection mode enum. Parsed from the string passed to conga_srv_create / conga_clt_create.
/// </summary>
public enum ModeKind
{
    Raw,
    Text,
    BlkRaw,
    BlkText,
    Command
}

public static class ModeKindExtensions
{
    /// <summary>
    /// True if this mode uses the Conga-Sharp wire protocol (52-byte header, CRC, compression).
    /// False for Raw and Text modes which send/receive raw TCP bytes.
    /// </summary>
    public static bool IsFramed(this ModeKind mode) => mode switch
    {
        ModeKind.BlkRaw => true,
        ModeKind.BlkText => true,
        ModeKind.Command => true,
        _ => false
    };

    /// <summary>
    /// Parses a mode string (case-insensitive) to ModeKind.
    /// Returns null if the string is not a valid mode.
    /// </summary>
    public static ModeKind? TryParse(string? modeStr)
    {
        if (string.IsNullOrWhiteSpace(modeStr)) return null;
        return modeStr.Trim().ToLowerInvariant() switch
        {
            "raw" => ModeKind.Raw,
            "text" => ModeKind.Text,
            "blkraw" => ModeKind.BlkRaw,
            "blktext" => ModeKind.BlkText,
            "command" => ModeKind.Command,
            _ => null
        };
    }
}
