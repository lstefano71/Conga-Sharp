namespace CongaSharp.Modes;

using System.Collections.Concurrent;
using System.Text.Json;
using CongaSharp.Events;

/// <summary>
/// Text mode: unframed, accumulates bytes until EOM pattern found.
/// EOM patterns are configured via the EOM property as an array of byte arrays.
/// </summary>
public sealed class TextMode : IConnectionMode
{
    private readonly ConcurrentDictionary<string, List<byte>> _buffers = new();

    /// <summary>
    /// EOM byte sequences. If empty, behaves like Raw mode.
    /// Set from the PropertyStore EOM value (JSON array of arrays).
    /// </summary>
    public List<byte[]> EomPatterns { get; set; } = new();

    public bool UsesFraming => false;

    public IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return Array.Empty<CongaEvent>();

        var buffer = _buffers.GetOrAdd(connectionName, _ => new List<byte>());
        var events = new List<CongaEvent>();

        lock (buffer)
        {
            buffer.AddRange(data.ToArray());

            // No EOM configured — deliver everything immediately
            if (EomPatterns.Count == 0)
            {
                events.Add(new CongaEvent
                {
                    ObjectName = connectionName,
                    Type = EventType.Receive,
                    Payload = buffer.ToArray()
                });
                buffer.Clear();
                return events;
            }

            // Scan for EOM patterns
            while (TryFindEom(buffer, out int endIndex))
            {
                var message = new byte[endIndex];
                buffer.CopyTo(0, message, 0, endIndex);
                buffer.RemoveRange(0, endIndex);

                events.Add(new CongaEvent
                {
                    ObjectName = connectionName,
                    Type = EventType.Receive,
                    Payload = message
                });
            }
        }

        return events;
    }

    public IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame)
        => throw new InvalidOperationException("Text mode does not use framing");

    public OutboundMessage PrepareOutbound(string connectionName, byte[] payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName)
    {
        if (closeFlag == PostSendAction.CloseCommand || closeFlag == PostSendAction.EmitSentEvent)
            return new OutboundMessage { Payload = payload, ErrorCode = Errors.ErrorCodes.InvalidMode };

        return new OutboundMessage
        {
            Payload = payload,
            PostAction = closeFlag
        };
    }

    public IReadOnlyList<CongaEvent> OnDisconnected(string connectionName)
    {
        var events = new List<CongaEvent>();

        // Deliver any remaining buffered data
        if (_buffers.TryRemove(connectionName, out var buffer))
        {
            lock (buffer)
            {
                if (buffer.Count > 0)
                {
                    events.Add(new CongaEvent
                    {
                        ObjectName = connectionName,
                        Type = EventType.Receive,
                        Payload = buffer.ToArray()
                    });
                }
            }
        }

        events.Add(new CongaEvent { ObjectName = connectionName, Type = EventType.Closed });
        return events;
    }

    public void ResetState(string connectionName)
    {
        _buffers.TryRemove(connectionName, out _);
    }

    /// <summary>
    /// Configures EOM from a JSON property value like "[[13,10]]" or "[[13,10],[10]]".
    /// Uses System.Text.Json DOM to avoid AOT trimming issues.
    /// </summary>
    public void ConfigureEomFromJson(string eomJson)
    {
        EomPatterns.Clear();
        if (string.IsNullOrWhiteSpace(eomJson) || eomJson == "[]") return;

        using var doc = JsonDocument.Parse(eomJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        foreach (var inner in doc.RootElement.EnumerateArray())
        {
            if (inner.ValueKind != JsonValueKind.Array) continue;
            var pattern = new List<byte>();
            foreach (var b in inner.EnumerateArray())
            {
                if (b.TryGetInt32(out var val))
                    pattern.Add((byte)val);
            }
            if (pattern.Count > 0)
                EomPatterns.Add(pattern.ToArray());
        }
    }

    private bool TryFindEom(List<byte> buffer, out int endIndex)
    {
        endIndex = 0;
        foreach (var pattern in EomPatterns)
        {
            int idx = FindPattern(buffer, pattern);
            if (idx >= 0)
            {
                endIndex = idx + pattern.Length;
                return true;
            }
        }
        return false;
    }

    private static int FindPattern(List<byte> buffer, byte[] pattern)
    {
        if (pattern.Length == 0 || buffer.Count < pattern.Length) return -1;

        for (int i = 0; i <= buffer.Count - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
