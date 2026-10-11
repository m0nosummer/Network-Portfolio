using System;
using System.Threading;
using System.Threading.Tasks;
using Arena.Core;

namespace Arena.Network
{
    public interface IConnectionManager
    {
        event Action<NetworkPacket, int> OnPacketReceived;
        event Action<int> OnClientConnected;
        event Action<int> OnClientDisconnected;

        Task StartServerAsync(int port, CancellationToken cancellationToken);
        Task ConnectToServerAsync(string ip, int port, CancellationToken cancellationToken);
        void SendPackets(NetworkPacket[] packets);
        void Shutdown();
    }
}