namespace CongaSharp.Modes;

/// <summary>
/// Action to take after a successful send. Maps to close_flag in C API.
/// </summary>
public enum PostSendAction
{
    None = 0,
    CloseConnection = 1,
    CloseCommand = 2,
    EmitSentEvent = 3
}
