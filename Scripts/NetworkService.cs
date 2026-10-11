using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using UnityEngine;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Logging;
using Arena.Network.Messages;

namespace Arena.Network
{
    public class NetworkService : MonoBehaviour, INetworkService
    {
        private IGameLogger _logger;
        private NetworkConfig _config;
        
        private IConnectionManager _connectionManager;
        private IPacketProcessor _packetProcessor;
        private IMessageRouter _messageRouter;
        
        private readonly ConcurrentQueue<ReceivedPacket> _incomingPackets = new();
        private readonly ConcurrentQueue<QueuedConnectionEvent> _connectionEvents = new();
        private CancellationTokenSource _cts;
        private Action<NetworkPacket, int> _packetReceivedHandler;
        private Action<int> _clientConnectedHandler;
        private Action<int> _clientDisconnectedHandler;
        private bool _connectionEventsSubscribed;
        private int _connectionVersion;

        public bool IsConnected { get; private set; }
        public bool IsServer { get; private set; }
        public int LocalClientId { get; private set; } = -1;
        
        public IMessageRouter MessageRouter => _messageRouter;

        public event Action<int> OnClientConnected;
        public event Action<int> OnClientDisconnected;
        public event Action<string> OnConnectionFailed;

        public void Initialize(NetworkConfig config, IGameLogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            var container = GameInstaller.Container;
            if (container == null)
            {
                throw new InvalidOperationException("DIContainer not initialized!");
            }
            
            // Factory
            _connectionManager = container.Resolve<IConnectionManager>();
            _packetProcessor = container.Resolve<IPacketProcessor>();
            _messageRouter = container.Resolve<IMessageRouter>();
            
            _logger.Log(LogLevel.Info, "Network", "NetworkService initialized via DI Container");
        }

        private void Update()
        {
            ProcessConnectionEvents();
            ProcessIncomingPackets();
        }

        public async Task StartServerAsync(int port)
        {
            if (IsConnected)
            {
                _logger.Log(LogLevel.Warning, "Network", "Already connected!");
                return;
            }

            int connectionVersion = BeginConnectionAttempt(isServer: true);

            try
            {
                await _connectionManager.StartServerAsync(port, _cts.Token);

                if (connectionVersion != Volatile.Read(ref _connectionVersion))
                    return;
                 
                IsConnected = true;
                IsServer = true;
                LocalClientId = 0;
                
                _logger.Log(LogLevel.Info, "Network", "Server started on port {0} (LocalClientId=0)", port);
            }
            catch (Exception ex)
            {
                if (connectionVersion != Volatile.Read(ref _connectionVersion))
                    return;

                CleanupConnectionState();
                _logger.LogException(ex, "StartServer");
                OnConnectionFailed?.Invoke($"Server start failed: {ex.Message}");
                throw;
            }
        }

        public async Task ConnectToServerAsync(string ip, int port)
        {
            if (IsConnected)
            {
                _logger.Log(LogLevel.Warning, "Network", "Already connected!");
                return;
            }

            int connectionVersion = BeginConnectionAttempt(isServer: false);

            try
            {
                await _connectionManager.ConnectToServerAsync(ip, port, _cts.Token);

                if (connectionVersion != Volatile.Read(ref _connectionVersion))
                    return;
                 
                IsConnected = true;
                IsServer = false;
                
                _logger.Log(LogLevel.Info, "Network", "Connected to {0}:{1} (waiting for ID assignment)", ip, port);
            }
            catch (Exception ex)
            {
                if (connectionVersion != Volatile.Read(ref _connectionVersion))
                    return;

                CleanupConnectionState();
                _logger.LogException(ex, "ConnectToServer");
                OnConnectionFailed?.Invoke($"Connection failed: {ex.Message}");
                throw;
            }
        }

        public void SendMessage(INetworkMessage message)
        {
            if (!IsConnected)
            {
                _logger.Log(LogLevel.Warning, "Network", "Cannot send message: not connected");
                return;
            }

            try
            {
                var packets = _packetProcessor.CreatePackets(message);
                
                foreach (var packet in packets)
                {
                    packet.Header.ClientId = LocalClientId;
                }
                
                _connectionManager.SendPackets(packets);
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "SendMessage");
            }
        }

        public void Disconnect()
        {
            bool hadConnectionState = IsConnected || _cts != null || _connectionEventsSubscribed;

            if (hadConnectionState)
                _logger?.Log(LogLevel.Info, "Network", "Disconnecting...");

            CleanupConnectionState();

            if (hadConnectionState)
                _logger?.Log(LogLevel.Info, "Network", "Disconnected");
        }

        private int BeginConnectionAttempt(bool isServer)
        {
            if (_cts != null || _connectionEventsSubscribed)
                CleanupConnectionState();

            ClearQueuedWork();

            int connectionVersion = Interlocked.Increment(ref _connectionVersion);
            _cts = new CancellationTokenSource();
            IsConnected = false;
            IsServer = isServer;
            LocalClientId = isServer ? 0 : -1;

            SubscribeToConnectionEvents(connectionVersion);
            return connectionVersion;
        }
        
        private void SubscribeToConnectionEvents(int connectionVersion)
        {
            if (_connectionEventsSubscribed || _connectionManager == null)
                return;

            _packetReceivedHandler = (packet, senderId) =>
                HandlePacketReceived(packet, senderId, connectionVersion);
            _clientConnectedHandler = id =>
                EnqueueConnectionEvent(ConnectionEventType.Connected, id, connectionVersion);
            _clientDisconnectedHandler = id =>
                EnqueueConnectionEvent(ConnectionEventType.Disconnected, id, connectionVersion);

            _connectionManager.OnPacketReceived += _packetReceivedHandler;
            _connectionManager.OnClientConnected += _clientConnectedHandler;
            _connectionManager.OnClientDisconnected += _clientDisconnectedHandler;

            _connectionEventsSubscribed = true;
        }

        private void UnsubscribeFromConnectionEvents()
        {
            if (!_connectionEventsSubscribed || _connectionManager == null)
                return;

            if (_packetReceivedHandler != null)
                _connectionManager.OnPacketReceived -= _packetReceivedHandler;

            if (_clientConnectedHandler != null)
                _connectionManager.OnClientConnected -= _clientConnectedHandler;

            if (_clientDisconnectedHandler != null)
                _connectionManager.OnClientDisconnected -= _clientDisconnectedHandler;

            _connectionEventsSubscribed = false;
            _packetReceivedHandler = null;
            _clientConnectedHandler = null;
            _clientDisconnectedHandler = null;
        }

        private void HandlePacketReceived(NetworkPacket packet, int actualSenderId, int connectionVersion)
        {
            if (connectionVersion != Volatile.Read(ref _connectionVersion))
                return;

            _incomingPackets.Enqueue(new ReceivedPacket
            {
                Packet = packet,
                SenderId = actualSenderId,
                ConnectionVersion = connectionVersion
            });
        }

        private void EnqueueConnectionEvent(
            ConnectionEventType eventType,
            int clientId,
            int connectionVersion)
        {
            if (connectionVersion != Volatile.Read(ref _connectionVersion))
                return;

            _connectionEvents.Enqueue(new QueuedConnectionEvent
            {
                EventType = eventType,
                ClientId = clientId,
                ConnectionVersion = connectionVersion
            });
        }

        private void ProcessConnectionEvents()
        {
            while (_connectionEvents.TryDequeue(out var connectionEvent))
            {
                if (connectionEvent.ConnectionVersion != Volatile.Read(ref _connectionVersion))
                    continue;

                if (connectionEvent.EventType == ConnectionEventType.Connected)
                {
                    OnClientConnected?.Invoke(connectionEvent.ClientId);
                    continue;
                }

                bool serverConnectionLost = !IsServer && connectionEvent.ClientId == 0;
                if (serverConnectionLost)
                {
                    bool wasConnected = IsConnected;
                    CleanupConnectionState();
                    OnClientDisconnected?.Invoke(0);

                    if (wasConnected)
                    {
                        const string reason = "Connection to server closed";
                        _logger?.Log(LogLevel.Warning, "Network", reason);
                        OnConnectionFailed?.Invoke(reason);
                    }

                    return;
                }

                OnClientDisconnected?.Invoke(connectionEvent.ClientId);
            }
        }

        private void ProcessIncomingPackets()
        {
            if (!IsConnected)
                return;

            int processed = 0;
            int maxPerFrame = _config != null ? _config.MaxMessagesPerFrame : 10;
             
            while (processed < maxPerFrame && _incomingPackets.TryDequeue(out var received))
            {
                if (received.ConnectionVersion != Volatile.Read(ref _connectionVersion))
                    continue;

                processed++;

                try
                {
                    var message = _packetProcessor.ProcessPacket(received.Packet);
                    if (message == null)
                        continue;

                    if (message is PlayerStateMessage playerState)
                        playerState.TransportSeqNumber = received.Packet.Header.SeqNumber;

                    if (!IsServer && message.Type == MessageType.Handshake && LocalClientId < 0)
                    {
                        if (message is HandshakeMessage handshake &&
                            received.SenderId == 0 &&
                            handshake.TargetId > 0)
                        {
                            LocalClientId = handshake.TargetId;
                            _logger.Log(LogLevel.Info, "Network",
                                "Assigned LocalClientId: {0}", LocalClientId);
                        }
                    }

                    _messageRouter?.RouteMessage(message, received.SenderId);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "ProcessIncomingPacket");
                }
            }
             
            if (_incomingPackets.Count > 100)
            {
                _logger.Log(LogLevel.Warning, "Network", "Packet queue backlog: {0} packets", _incomingPackets.Count);
            }
        }

        private void CleanupConnectionState()
        {
            Interlocked.Increment(ref _connectionVersion);

            var cts = _cts;
            _cts = null;

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            UnsubscribeFromConnectionEvents();
            _connectionManager?.Shutdown();
            cts?.Dispose();

            IsConnected = false;
            IsServer = false;
            LocalClientId = -1;

            ClearQueuedWork();
        }

        private void ClearQueuedWork()
        {
            while (_incomingPackets.TryDequeue(out _)) { }
            while (_connectionEvents.TryDequeue(out _)) { }
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        private struct ReceivedPacket
        {
            public NetworkPacket Packet;
            public int SenderId;
            public int ConnectionVersion;
        }

        private struct QueuedConnectionEvent
        {
            public ConnectionEventType EventType;
            public int ClientId;
            public int ConnectionVersion;
        }

        private enum ConnectionEventType
        {
            Connected,
            Disconnected
        }
    }
}
