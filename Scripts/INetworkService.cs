using System;
using System.Threading.Tasks;
using Arena.Network;

namespace Arena.Core
{
    public interface INetworkService
    {
        bool IsConnected { get; }
        bool IsServer { get; }
        int LocalClientId { get; }
        
        event Action<int> OnClientConnected;
        event Action<int> OnClientDisconnected;
        event Action<string> OnConnectionFailed;
        
        Task StartServerAsync(int port);
        Task ConnectToServerAsync(string ip, int port);
        void SendMessage(INetworkMessage message);
        void Disconnect();
        
        IMessageRouter MessageRouter { get; }
    }

    public class NetworkStatistics
    {
        public int TcpPacketsSent { get; set; }
        public int TcpPacketsReceived { get; set; }
        public int UdpPacketsSent { get; set; }
        public int UdpPacketsReceived { get; set; }
        public int RetransmissionCount { get; set; }
    }
}