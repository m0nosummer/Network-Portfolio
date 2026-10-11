using System;
using Arena.Core;

namespace Arena.Network
{
    public interface IMessageRouter
    {
        void RegisterHandler(MessageType type, Action<INetworkMessage, int> handler);
        void RouteMessage(INetworkMessage message, int senderId);
        void UnregisterHandler(MessageType type, Action<INetworkMessage, int> handler);
        void ClearHandlers();
    }
}