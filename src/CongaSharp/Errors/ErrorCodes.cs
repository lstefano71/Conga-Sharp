namespace CongaSharp.Errors;

/// <summary>
/// Error codes returned by all Conga-Sharp C API functions.
/// Codes 0-1999 reuse original Conga codes where applicable.
/// Codes 2000+ are Conga-Sharp specific.
/// </summary>
public static class ErrorCodes
{
  // Success
  public const int Success = 0;

  // Conga-compatible codes
  public const int Timeout = 100;
  public const int InvalidName = 1002;
  public const int InvalidMode = 1003;
  public const int CommandNameInUse = 1008;
  public const int NameInUse = 1009;
  public const int NotServer = 1010;
  public const int NotClient = 1011;
  public const int ConnectFailed = 1111;
  public const int SocketClosed = 1119;
  public const int BufferExceeded = 1135;

  // Conga-Sharp specific codes (2000+)
  public const int BufferTooSmall = 2001;
  public const int ShuttingDown = 2002;
  public const int CrcFailure = 2003;
  public const int CompressionError = 2004;
  public const int InvalidProperty = 2005;
  public const int PropertyReadOnly = 2006;
  public const int ObjectNotReady = 2007;
  public const int ObjectAlreadyStarted = 2008;
  public const int InvalidHandle = 2009;
  public const int ProtocolError = 2010;
  public const int InvalidJson = 2011;
  public const int BindFailed = 2013;
}
