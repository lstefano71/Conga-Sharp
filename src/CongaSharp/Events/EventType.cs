namespace CongaSharp.Events;

/// <summary>
/// Numeric codes for Conga-Sharp events.
/// Matches PRD §9.1.
/// </summary>
public enum EventType
{
  Connect = 1,
  Receive = 2,
  Block = 3,
  BlockLast = 4,
  Progress = 5,
  Sent = 6,
  Closed = 7,
  Timeout = 8,
  Error = 9
}
