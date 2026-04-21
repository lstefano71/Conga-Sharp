namespace CongaSharp.Diagnostics;

/// <summary>
/// Trace verbosity levels for diagnostic logging.
/// Higher levels include all lower-level messages.
/// </summary>
public enum TraceLevel
{
    Off = 0,
    Errors = 1,
    Connections = 2,
    Messages = 3,
    Wire = 4
}
