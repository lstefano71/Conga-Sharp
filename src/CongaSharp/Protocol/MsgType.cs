namespace CongaSharp.Protocol;

public enum MsgType : byte
{
    Data = 0x01,
    Respond = 0x02,
    Progress = 0x03,
    Control = 0x04
}
