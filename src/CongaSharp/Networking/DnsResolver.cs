namespace CongaSharp.Networking;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// Resolves hostnames to IP addresses. Parses literal IPs directly,
/// otherwise uses async DNS lookup.
/// </summary>
public static class DnsResolver
{
  /// <summary>
  /// Resolves a hostname or IP string to an IPAddress.
  /// If the host is already a valid IP literal, returns it directly.
  /// Otherwise performs async DNS resolution.
  /// </summary>
  public static async Task<IPAddress> ResolveAsync(string host, bool preferIPv6 = false)
  {
    if (IPAddress.TryParse(host, out var parsed))
      return parsed;

    var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
    if (addresses.Length == 0)
      throw new SocketException((int)SocketError.HostNotFound);

    var preferred = preferIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

    // Try preferred family first, then fall back to any
    return Array.Find(addresses, a => a.AddressFamily == preferred) ?? addresses[0];
  }
}
