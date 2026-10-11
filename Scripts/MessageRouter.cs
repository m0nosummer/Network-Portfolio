using System;
using System.Collections.Generic;
using Arena.Core;
using Arena.Logging;

namespace Arena.Network
{
    public class MessageRouter : IMessageRouter
    {
        private readonly IGameLogger _logger;
        private readonly Dictionary<MessageType, List<Action<INetworkMessage, int>>> _handlers = new();

        public MessageRouter(IGameLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void RegisterHandler(MessageType type, Action<INetworkMessage, int> handler)
        {
            if (!_handlers.ContainsKey(type))
            {
                _handlers[type] = new List<Action<INetworkMessage, int>>();
            }
            
            _handlers[type].Add(handler);
            
            _logger.Log(LogLevel.Debug, "MessageRouter", "Handler registered for {0}", type);
        }

        public void RouteMessage(INetworkMessage message, int senderId)
        {
            if (message == null)
            {
                _logger.Log(LogLevel.Warning, "MessageRouter", "Null message received");
                return;
            }

            if (!_handlers.TryGetValue(message.Type, out var handlers))
            {
                return;
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(message, senderId);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, $"MessageRouter-{message.Type}");
                }
            }
        }

        public void UnregisterHandler(MessageType type, Action<INetworkMessage, int> handler)
        {
            if (_handlers.TryGetValue(type, out var handlers))
            {
                handlers.Remove(handler);
                _logger.Log(LogLevel.Debug, "MessageRouter", "Handler unregistered for {0}", type);
            }
        }

        public void ClearHandlers()
        {
            _handlers.Clear();
            _logger.Log(LogLevel.Info, "MessageRouter", "All handlers cleared");
        }
    }
}