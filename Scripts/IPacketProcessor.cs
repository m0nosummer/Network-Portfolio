using Arena.Core;

namespace Arena.Network
{
    public interface IPacketProcessor
    {
        NetworkPacket[] CreatePackets(INetworkMessage message);
        INetworkMessage ProcessPacket(NetworkPacket packet);
    }
}