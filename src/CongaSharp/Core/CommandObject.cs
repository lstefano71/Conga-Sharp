namespace CongaSharp.Core;

public sealed class CommandObject : CongaObject
{
    public CommandObject(string name, CongaObject parent)
        : base(name, ObjectType.Command, parent)
    {
    }
}
