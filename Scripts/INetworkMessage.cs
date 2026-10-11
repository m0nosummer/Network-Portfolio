using Arena.Core;

namespace Arena.Network
{
    public interface INetworkMessage
    {
        MessageType Type { get; }
        float Timestamp { get; }
        int TargetId { get; set; }
        byte[] Serialize();
        void Deserialize(byte[] data);
    }
}