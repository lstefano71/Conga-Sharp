namespace CongaSharp.Core;

using CongaSharp.Networking;

public sealed class ConnectionObject : CongaObject
{
    public SocketPipeline? Pipeline { get; internal set; }

    public ConnectionObject(string name, CongaObject parent)
        : base(name, ObjectType.Connection, parent)
    {
    }
}
