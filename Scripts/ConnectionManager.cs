using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Arena.Core;
using Arena.Logging;

namespace Arena.Network
{
    public class ConnectionManager : IConnectionManager
    {
        private readonly IGameLogger _logger;
        private readonly NetworkConfig _config;
        private readonly NetworkStatistics _stats = new();
        private readonly ByteArrayPool _bufferPool = new(GameConstants.Protocol.BufferSize, 50);
        
        
        // Server components
        private TcpListener _tcpListener;
        private UdpClient _udpServer;
        private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
        private readonly ConcurrentDictionary<int, UdpRegistrationState> _udpRegistrations = new();
        
        // Client components
        private TcpClient _tcpClient;
        private UdpClient _udpClient;
        private IPEndPoint _serverEndPoint;
        private ReliableUdpManager _clientReliableManager;
        private TcpSendQueue _clientTcpSendQueue;
        private byte[] _clientUdpRegistrationPayload;
        private DateTime _clientUdpRegistrationRetryAtUtc;
        private bool _clientUdpRegistrationConfirmed;
        private readonly PendingUdpQueue _clientPendingReliableUdp = new();
        private readonly object _transportLifecycleLock = new();
        private CancellationTokenSource _transportCts;
        private TransportMode _transportMode;
        private TransportCallbacks _transportCallbacks;
        private int _transportGeneration;
        private int _clientDisconnectSignaled = 1;
        
        private int _nextClientId = 1;
        private readonly object _clientIdLock = new();

        private enum TransportMode
        {
            None,
            Server,
            Client
        }

        private sealed class TransportCallbacks
        {
            public readonly Action<NetworkPacket, int> PacketReceived;
            public readonly Action<int> ClientConnected;
            public readonly Action<int> ClientDisconnected;

            public TransportCallbacks(
                Action<NetworkPacket, int> packetReceived,
                Action<int> clientConnected,
                Action<int> clientDisconnected)
            {
                PacketReceived = packetReceived;
                ClientConnected = clientConnected;
                ClientDisconnected = clientDisconnected;
            }
        }

        private sealed class UdpRegistrationState
        {
            public readonly ClientSession Session;
            public readonly ulong Token;
            public readonly PendingUdpQueue PendingReliableUdp = new();

            public UdpRegistrationState(ClientSession session, ulong token)
            {
                Session = session;
                Token = token;
            }
        }

        private sealed class PendingUdpPacket
        {
            public readonly byte[] Data;
            public readonly uint SeqNumber;

            public PendingUdpPacket(byte[] data, uint seqNumber)
            {
                Data = data;
                SeqNumber = seqNumber;
            }
        }

        private sealed class PendingUdpQueue
        {
            private readonly Queue<PendingUdpPacket> _packets = new();
            private int _queuedBytes;

            public bool TryEnqueue(byte[] data, uint seqNumber)
            {
                if (data == null)
                    throw new ArgumentNullException(nameof(data));

                if (_packets.Count >= MaxPendingUdpRegistrationFrames ||
                    data.Length > MaxPendingUdpRegistrationBytes - _queuedBytes)
                {
                    return false;
                }

                _packets.Enqueue(new PendingUdpPacket(data, seqNumber));
                _queuedBytes += data.Length;
                return true;
            }

            public List<PendingUdpPacket> Drain()
            {
                var drained = new List<PendingUdpPacket>(_packets.Count);
                while (_packets.Count > 0)
                    drained.Add(_packets.Dequeue());

                _queuedBytes = 0;
                return drained;
            }

            public void Clear()
            {
                _packets.Clear();
                _queuedBytes = 0;
            }
        }

        private const uint UdpRegistrationMagic = 0x47525055;
        private const int UdpRegistrationPayloadSize = sizeof(uint) + sizeof(int) + sizeof(ulong);
        private const int MaxPendingClientTcpFrames = 8192;
        private const int MaxPendingClientTcpBytes = 1024 * 1024;
        private const int MaxPendingUdpRegistrationFrames = 256;
        private const int MaxPendingUdpRegistrationBytes = 256 * 1024;
        private static readonly TimeSpan UdpRegistrationRetryInterval =
            TimeSpan.FromMilliseconds(250);

        public event Action<NetworkPacket, int> OnPacketReceived;
        public event Action<int> OnClientConnected;
        public event Action<int> OnClientDisconnected;

        public ConnectionManager(NetworkConfig config, IGameLogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task StartServerAsync(int port, CancellationToken ct)
        {
            int transportGeneration = 0;
            TcpListener tcpListener = null;
            UdpClient udpServer = null;

            try
            {
                ct.ThrowIfCancellationRequested();
                transportGeneration = BeginTransport(
                    TransportMode.Server,
                    ct,
                    out var transportToken,
                    out var transportCallbacks);

                tcpListener = new TcpListener(IPAddress.Any, port);
                tcpListener.Start();
                 
                udpServer = new UdpClient(port);
                ConfigureUdpSocket(udpServer);

                lock (_transportLifecycleLock)
                {
                    EnsureCurrentTransportLocked(transportGeneration, TransportMode.Server);
                    _tcpListener = tcpListener;
                    _udpServer = udpServer;
                }
                 
                // Individual threads
                _ = Task.Run(
                    () => TcpAcceptLoopAsync(
                        tcpListener,
                        transportGeneration,
                        transportCallbacks,
                        transportToken),
                    transportToken);
                _ = Task.Run(
                    () => UdpReceiveLoopAsync(
                        udpServer,
                        transportGeneration,
                        transportCallbacks,
                        transportToken),
                    transportToken);
                _ = Task.Run(
                    () => ReliableUpdateLoopAsync(
                        TransportMode.Server,
                        transportGeneration,
                        udpServer,
                        null,
                        transportToken),
                    transportToken);
                 
                _logger.Log(LogLevel.Info, "Connection", "Server started on port {0}", port);
                 
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                if (transportGeneration != 0)
                    CleanupTransport(transportGeneration, notifyClient: false);

                CloseListener(tcpListener);
                CloseUdpClient(udpServer);
                _logger.LogException(ex, "StartServer");
                throw new NetworkException("Failed to start server", NetworkErrorCode.ServerStartFailed, ex);
            }
        }

        public async Task ConnectToServerAsync(string ip, int port, CancellationToken ct)
        {
            int transportGeneration = 0;
            TcpClient tcpClient = null;
            UdpClient udpClient = null;
            TcpSendQueue clientTcpSendQueue = null;

            try
            {
                ct.ThrowIfCancellationRequested();
                transportGeneration = BeginTransport(
                    TransportMode.Client,
                    ct,
                    out var transportToken,
                    out var transportCallbacks);

                // TCP
                tcpClient = new TcpClient { NoDelay = true };
                 
                var connectTask = tcpClient.ConnectAsync(ip, port);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5f), transportToken);
                 
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                 
                if (completedTask != connectTask)
                {
                    ObserveFault(connectTask);
                    transportToken.ThrowIfCancellationRequested();
                    throw new NetworkException("Connection timeout", NetworkErrorCode.ConnectionTimeout);
                }

                await connectTask;
                transportToken.ThrowIfCancellationRequested();
                 
                // UDP
                var serverEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
                 
                udpClient = new UdpClient(0);
                udpClient.Connect(serverEndPoint);
                ConfigureUdpSocket(udpClient);
                 
                var reliableManager = new ReliableUdpManager();
                reliableManager.Configure(_config.RetryTimeoutSeconds, _config.MaxRetries);

                clientTcpSendQueue = new TcpSendQueue(
                    tcpClient.GetStream(),
                    MaxPendingClientTcpFrames,
                    MaxPendingClientTcpBytes,
                    _logger,
                    "ClientTransport",
                    _ => SignalClientDisconnected(transportGeneration));

                lock (_transportLifecycleLock)
                {
                    EnsureCurrentTransportLocked(transportGeneration, TransportMode.Client);
                    _tcpClient = tcpClient;
                    _udpClient = udpClient;
                    _serverEndPoint = serverEndPoint;
                    _clientReliableManager = reliableManager;
                    _clientTcpSendQueue = clientTcpSendQueue;
                }
                 
                // Receive loops
                _ = Task.Run(
                    () => TcpReceiveLoopAsync(
                        tcpClient,
                        transportGeneration,
                        transportCallbacks,
                        transportToken),
                    transportToken);
                _ = Task.Run(
                    () => UdpClientReceiveLoopAsync(
                        udpClient,
                        reliableManager,
                        transportGeneration,
                        transportCallbacks,
                        transportToken),
                    transportToken);
                _ = Task.Run(
                    () => ReliableUpdateLoopAsync(
                        TransportMode.Client,
                        transportGeneration,
                        udpClient,
                        reliableManager,
                        transportToken),
                    transportToken);
                 
                _logger.Log(LogLevel.Info, "Connection", "Connected to {0}:{1}", ip, port);
            }
            catch (NetworkException ex)
            {
                if (transportGeneration != 0)
                    CleanupTransport(transportGeneration, notifyClient: false);

                clientTcpSendQueue?.Dispose();
                CloseTcpClient(tcpClient);
                CloseUdpClient(udpClient);
                _logger.LogException(ex, "ConnectToServer");
                throw;
            }
            catch (OperationCanceledException)
            {
                if (transportGeneration != 0)
                    CleanupTransport(transportGeneration, notifyClient: false);

                clientTcpSendQueue?.Dispose();
                CloseTcpClient(tcpClient);
                CloseUdpClient(udpClient);
                throw new NetworkException("Connection timeout", NetworkErrorCode.ConnectionTimeout);
            }
            catch (Exception ex)
            {
                if (transportGeneration != 0)
                    CleanupTransport(transportGeneration, notifyClient: false);

                clientTcpSendQueue?.Dispose();
                CloseTcpClient(tcpClient);
                CloseUdpClient(udpClient);
                _logger.LogException(ex, "ConnectToServer");
                throw new NetworkException("Connection failed", NetworkErrorCode.ConnectionFailed, ex);
            }
        }

        public void SendPackets(NetworkPacket[] packets)
        {
            if (packets == null || packets.Length == 0) return;

            foreach (var packet in packets)
            {
                try
                {
                    var data = packet.Serialize();
                    
                    if (RequiresTcp(packet.Header.Type))
                    {
                        SendViaTcp(data, packet.TargetId);
                        _stats.TcpPacketsSent++;
                    }
                    else
                    {
                        bool reliable = packet.Header.Flags.HasFlag(PacketFlags.Reliable);
                        SendViaUdp(data, packet.TargetId, reliable, packet.Header.SeqNumber);
                        _stats.UdpPacketsSent++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "SendPacket");
                }
            }
        }

        private async Task ReliableUpdateLoopAsync(
            TransportMode mode,
            int transportGeneration,
            UdpClient udpSocket,
            ReliableUdpManager clientReliableManager,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested &&
                   IsCurrentTransport(transportGeneration, mode))
            {
                try
                {
                    await Task.Delay(50, ct);

                    if (!IsCurrentTransport(transportGeneration, mode))
                        break;

                    if (mode == TransportMode.Server)
                    {
                        foreach (var session in _sessions.Values)
                        {
                            if (!IsCurrentTransport(transportGeneration, mode))
                                return;

                            session.ReliableManager.Update();
                        }
                    }
                    else
                    {
                        RetryPendingClientUdpRegistration(
                            transportGeneration,
                            udpSocket,
                            clientReliableManager);
                        clientReliableManager?.Update();
                    }

                    ProcessReliableRetries(
                        mode,
                        transportGeneration,
                        udpSocket,
                        clientReliableManager);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "ReliableUpdateLoop");
                }
            }
        }

        private void ProcessReliableRetries(
            TransportMode mode,
            int transportGeneration,
            UdpClient udpSocket,
            ReliableUdpManager clientReliableManager)
        {
            if (mode == TransportMode.Server && udpSocket != null)
            {
                foreach (var session in _sessions.Values)
                {
                    if (!IsCurrentTransport(transportGeneration, mode))
                        return;

                    var packetsToRetry = session.ReliableManager.GetPacketsToRetry();
                     
                    foreach (var packet in packetsToRetry)
                    {
                        try
                        {
                            if (TryGetCurrentServerUdpEndPoint(
                                    transportGeneration,
                                    udpSocket,
                                    session,
                                    out var endpoint) &&
                                endpoint != null)
                            {
                                udpSocket.Send(packet, packet.Length, endpoint);
                                _stats.RetransmissionCount++;
                                
                                _logger.Log(LogLevel.Trace, "Connection", 
                                    "Retransmitted to Client {0}", session.ClientId);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogException(ex, $"Retry-Client{session.ClientId}");
                        }
                    }
                }
            }
            
            if (mode == TransportMode.Client &&
                udpSocket != null &&
                clientReliableManager != null &&
                IsCurrentTransport(transportGeneration, mode))
            {
                var packetsToRetry = clientReliableManager.GetPacketsToRetry();
                
                foreach (var packet in packetsToRetry)
                {
                    try
                    {
                        if (!IsCurrentTransport(transportGeneration, mode))
                            return;

                        udpSocket.Send(packet, packet.Length);
                        _stats.RetransmissionCount++;
                        
                        _logger.Log(LogLevel.Trace, "Connection", "Client retransmitted packet");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogException(ex, "ClientRetry");
                    }
                }
            }
        }


        private async Task TcpAcceptLoopAsync(
            TcpListener tcpListener,
            int transportGeneration,
            TransportCallbacks transportCallbacks,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested &&
                   IsCurrentTransport(transportGeneration, TransportMode.Server))
            {
                try
                {
                    if (tcpListener.Pending())
                    {
                        var tcpClient = await tcpListener.AcceptTcpClientAsync();
                        tcpClient.NoDelay = true;

                        if (!IsCurrentTransport(transportGeneration, TransportMode.Server))
                        {
                            tcpClient.Close();
                            break;
                        }
                         
                        int clientId = GetNextClientId();
                         
                        var session = new ClientSession(clientId, tcpClient, _logger);
                        session.ReliableManager.Configure(_config.RetryTimeoutSeconds, _config.MaxRetries);

                        ulong udpRegistrationToken = CreateUdpRegistrationToken();
                        var registration = new UdpRegistrationState(session, udpRegistrationToken);
                        bool sessionCommitted = false;

                        lock (_transportLifecycleLock)
                        {
                            if (IsCurrentTransportLocked(
                                    transportGeneration,
                                    TransportMode.Server) &&
                                _sessions.TryAdd(clientId, session))
                            {
                                if (_udpRegistrations.TryAdd(clientId, registration))
                                {
                                    InvokeClientEventSubscribersSafely(
                                        transportCallbacks.ClientConnected,
                                        clientId,
                                        "ClientConnected");

                                    sessionCommitted =
                                        IsCurrentTransportLocked(
                                            transportGeneration,
                                            TransportMode.Server) &&
                                        _sessions.TryGetValue(clientId, out var currentSession) &&
                                        ReferenceEquals(currentSession, session) &&
                                        _udpRegistrations.TryGetValue(clientId, out var currentRegistration) &&
                                        ReferenceEquals(currentRegistration, registration);
                                }

                                if (!sessionCommitted)
                                {
                                    TryRemoveExactSession(session);
                                    if (_udpRegistrations.TryGetValue(clientId, out var currentRegistration) &&
                                        ReferenceEquals(currentRegistration, registration))
                                    {
                                        _udpRegistrations.TryRemove(clientId, out _);
                                    }
                                }
                            }
                        }

                        if (!sessionCommitted)
                        {
                            session.Dispose();
                            continue;
                        }

                        _logger.Log(LogLevel.Info, "Connection", "Client {0} connected", clientId);

                        if (!IsCurrentServerSession(transportGeneration, session))
                            continue;

                        SendUdpRegistrationChallenge(
                            session,
                            udpRegistrationToken,
                            transportGeneration,
                            transportCallbacks.ClientDisconnected);

                        if (!IsCurrentServerSession(transportGeneration, session))
                            continue;

                        _ = Task.Run(
                            () => HandleTcpClientAsync(
                                session,
                                transportGeneration,
                                transportCallbacks,
                                ct),
                            ct);
                    }
                    
                    await Task.Delay(50, ct);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        _logger.LogException(ex, "TcpAcceptLoop");
                }
            }
        }

        private async Task HandleTcpClientAsync(
            ClientSession session,
            int transportGeneration,
            TransportCallbacks transportCallbacks,
            CancellationToken ct)
        {
            try
            {
                var stream = session.TcpClient.GetStream();
                
                await ProcessTcpStreamAsync(
                    stream,
                    session.ClientId,
                    transportGeneration,
                    TransportMode.Server,
                    transportCallbacks,
                    ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"HandleClient-{session.ClientId}");
            }
            finally
            {
                DisconnectClient(session, transportCallbacks.ClientDisconnected);
            }
        }

        private void SendViaTcp(byte[] data, int targetId)
        {
            try
            {
                byte[] lengthHeader = BitConverter.GetBytes(data.Length);
                byte[] frameData = new byte[4 + data.Length];
                Buffer.BlockCopy(lengthHeader, 0, frameData, 0, 4);
                Buffer.BlockCopy(data, 0, frameData, 4, data.Length);
                
                _logger.Log(LogLevel.Debug, "Connection", 
                    "📡 TCP Send: {0} bytes to Target={1}", frameData.Length, targetId);

                TransportMode mode;
                TcpClient tcpClient = null;
                TcpSendQueue clientTcpSendQueue = null;
                List<ClientSession> serverSessions = null;
                TransportCallbacks transportCallbacks;
                int transportGeneration;

                lock (_transportLifecycleLock)
                {
                    mode = _transportMode;
                    transportGeneration = _transportGeneration;
                    transportCallbacks = _transportCallbacks;

                    if (mode == TransportMode.Server)
                    {
                        serverSessions = new List<ClientSession>();

                        if (targetId == -1)
                        {
                            serverSessions.AddRange(_sessions.Values);
                        }
                        else if (_sessions.TryGetValue(targetId, out var session))
                        {
                            serverSessions.Add(session);
                        }
                    }
                    else if (mode == TransportMode.Client)
                    {
                        tcpClient = _tcpClient;
                        clientTcpSendQueue = _clientTcpSendQueue;
                    }
                }
                 
                if (mode == TransportMode.Server)
                {
                    foreach (var session in serverSessions)
                    {
                        SendTcpFrameToSession(
                            session,
                            frameData,
                            transportGeneration,
                            transportCallbacks.ClientDisconnected);
                    }
                }
                else if (mode == TransportMode.Client &&
                         tcpClient != null &&
                         clientTcpSendQueue != null)
                {
                    if (!IsCurrentClientTransport(
                            transportGeneration,
                            tcpClient,
                            clientTcpSendQueue))
                    {
                        return;
                    }

                    TcpFrameQueueResult result = clientTcpSendQueue.TryQueue(frameData);
                    if (result != TcpFrameQueueResult.Queued)
                    {
                        _logger.Log(LogLevel.Warning, "Connection",
                            "Client TCP frame rejected: {0}", result);
                        SignalClientDisconnected(transportGeneration);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "SendViaTcp");
            }
        }

        private void SendTcpFrameToSession(
            ClientSession session,
            byte[] frameData,
            int transportGeneration,
            Action<int> clientDisconnectedHandler)
        {
            if (session == null)
                return;

            try
            {
                if (!IsCurrentServerSession(transportGeneration, session))
                    return;

                if (!session.IsConnected)
                {
                    DisconnectClient(session, clientDisconnectedHandler);
                    return;
                }

                session.SendTcpFrame(frameData);
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"TcpSend-Client{session.ClientId}");
                DisconnectClient(session, clientDisconnectedHandler);
            }
        }

        private void SendViaUdp(byte[] data, int targetId, bool reliable, uint seqNumber)
        {
            try
            {
                TransportMode mode;
                int transportGeneration;
                UdpClient udpSocket = null;
                ReliableUdpManager clientReliableManager = null;
                List<ClientSession> serverSessions = null;

                lock (_transportLifecycleLock)
                {
                    mode = _transportMode;
                    transportGeneration = _transportGeneration;

                    if (mode == TransportMode.Server)
                    {
                        udpSocket = _udpServer;
                        serverSessions = new List<ClientSession>();

                        if (targetId == -1)
                        {
                            serverSessions.AddRange(_sessions.Values);
                        }
                        else if (_sessions.TryGetValue(targetId, out var session))
                        {
                            serverSessions.Add(session);
                        }
                    }
                    else if (mode == TransportMode.Client)
                    {
                        udpSocket = _udpClient;
                        clientReliableManager = _clientReliableManager;
                    }
                }

                if (mode == TransportMode.Server)
                {
                    if (targetId != -1 && serverSessions.Count == 0)
                    {
                        _logger.Log(LogLevel.Warning, "Connection",
                            "UDP endpoint not found for client {0}", targetId);
                        return;
                    }

                    foreach (var session in serverSessions)
                    {
                        SendUdpToServerSession(
                            data,
                            reliable,
                            seqNumber,
                            udpSocket,
                            session,
                            transportGeneration);
                    }
                }
                else if (mode == TransportMode.Client &&
                         udpSocket != null &&
                         clientReliableManager != null)
                {
                    SendUdpFromClient(
                        data,
                        reliable,
                        seqNumber,
                        udpSocket,
                        clientReliableManager,
                        transportGeneration);
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "SendViaUdp");
            }
        }

        private void SendUdpToServerSession(
            byte[] data,
            bool reliable,
            uint seqNumber,
            UdpClient udpSocket,
            ClientSession session,
            int transportGeneration)
        {
            IPEndPoint endpoint = null;
            bool queueFull = false;
            Action<int> clientDisconnectedHandler = null;

            lock (_transportLifecycleLock)
            {
                if (udpSocket == null ||
                    !IsCurrentTransportLocked(transportGeneration, TransportMode.Server) ||
                    !ReferenceEquals(_udpServer, udpSocket) ||
                    !_sessions.TryGetValue(session.ClientId, out var currentSession) ||
                    !ReferenceEquals(currentSession, session) ||
                    !_udpRegistrations.TryGetValue(session.ClientId, out var registration) ||
                    !ReferenceEquals(registration.Session, session))
                {
                    return;
                }

                endpoint = session.UdpEndPoint;
                if (endpoint == null)
                {
                    if (!reliable)
                        return;

                    if (registration.PendingReliableUdp.TryEnqueue(data, seqNumber))
                        return;

                    queueFull = true;
                    clientDisconnectedHandler = _transportCallbacks?.ClientDisconnected;
                }
            }

            if (queueFull)
            {
                _logger.Log(LogLevel.Error, "Connection",
                    "Reliable UDP registration queue full for Client {0}", session.ClientId);
                DisconnectClient(session, clientDisconnectedHandler);
                return;
            }

            if (reliable)
                session.ReliableManager.TrackPacket(seqNumber, data);

            udpSocket.Send(data, data.Length, endpoint);
        }

        private void SendUdpFromClient(
            byte[] data,
            bool reliable,
            uint seqNumber,
            UdpClient udpSocket,
            ReliableUdpManager reliableManager,
            int transportGeneration)
        {
            bool queueFull = false;

            lock (_transportLifecycleLock)
            {
                if (!IsCurrentTransportLocked(transportGeneration, TransportMode.Client) ||
                    !ReferenceEquals(_udpClient, udpSocket) ||
                    !ReferenceEquals(_clientReliableManager, reliableManager))
                {
                    return;
                }

                if (!_clientUdpRegistrationConfirmed)
                {
                    if (!reliable)
                        return;

                    if (_clientPendingReliableUdp.TryEnqueue(data, seqNumber))
                        return;

                    queueFull = true;
                }
            }

            if (queueFull)
            {
                _logger.Log(LogLevel.Error, "Connection",
                    "Client reliable UDP registration queue full");
                SignalClientDisconnected(transportGeneration);
                return;
            }

            if (reliable)
                reliableManager.TrackPacket(seqNumber, data);

            udpSocket.Send(data, data.Length);
        }
        
        private async Task TcpReceiveLoopAsync(
            TcpClient tcpClient,
            int transportGeneration,
            TransportCallbacks transportCallbacks,
            CancellationToken ct)
        {
            bool shouldSignalDisconnect = false;

            try
            {
                var stream = tcpClient.GetStream();
                await ProcessTcpStreamAsync(
                    stream,
                    0,
                    transportGeneration,
                    TransportMode.Client,
                    transportCallbacks,
                    ct);

                shouldSignalDisconnect = !ct.IsCancellationRequested &&
                    IsCurrentTransport(transportGeneration, TransportMode.Client);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested &&
                    IsCurrentTransport(transportGeneration, TransportMode.Client))
                {
                    _logger.LogException(ex, "TcpReceiveLoop");
                    shouldSignalDisconnect = true;
                }
            }
            finally
            {
                if (shouldSignalDisconnect)
                    SignalClientDisconnected(transportGeneration);
            }
        }

        private async Task UdpReceiveLoopAsync(
            UdpClient udpServer,
            int transportGeneration,
            TransportCallbacks transportCallbacks,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested &&
                   IsCurrentTransport(transportGeneration, TransportMode.Server))
            {
                try
                {
                    var result = await udpServer.ReceiveAsync();

                    if (!IsCurrentTransport(transportGeneration, TransportMode.Server))
                        break;
                    
                    // ACK = [magic number(4 bytes)][Seq number (4 bytes)]
                    bool isAck = false;
                    if (result.Buffer.Length == 8)
                    {
                        uint magic = BitConverter.ToUInt32(result.Buffer, 0);
                        if (magic == GameConstants.Protocol.AckMagicNumber)
                        {
                            uint seq = BitConverter.ToUInt32(result.Buffer, 4);
                            
                            if (TryGetRegisteredSessionByUdpEndPoint(
                                    transportGeneration,
                                    result.RemoteEndPoint,
                                    out var ackSession))
                            {
                                if (ackSession.ReliableManager.ProcessAck(result.Buffer))
                                {
                                    _logger.Log(LogLevel.Trace, "Connection", 
                                        "ACK seq={0} processed for Client {1}", seq, ackSession.ClientId);
                                }
                            }
                            else
                            {
                                _logger.Log(LogLevel.Warning, "Connection", 
                                    "ACK from unknown endpoint: {0}", result.RemoteEndPoint);
                            }
                            isAck = true;
                        }
                    }
                    
                    if (isAck) continue;
                    
                    if (TryHandleUdpRegistration(
                            result.Buffer,
                            result.RemoteEndPoint,
                            transportGeneration,
                            out var registeredSession,
                            out var pendingReliablePackets))
                    {
                        if (registeredSession != null)
                        {
                            try
                            {
                                udpServer.Send(
                                    result.Buffer,
                                    result.Buffer.Length,
                                    result.RemoteEndPoint);
                            }
                            catch (Exception ex)
                            {
                                _logger.Log(LogLevel.Warning, "Connection",
                                    "UDP registration confirmation send failed: {0}", ex.Message);
                            }

                            FlushServerPendingReliableUdp(
                                registeredSession,
                                pendingReliablePackets,
                                udpServer,
                                result.RemoteEndPoint,
                                transportGeneration);
                        }

                        continue;
                    }
                    
                    // General data
                    NetworkPacket packet;
                    try
                    {
                        packet = NetworkPacket.Deserialize(result.Buffer, result.Buffer.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(LogLevel.Warning, "Connection", "Invalid UDP packet from {0}: {1}", 
                            result.RemoteEndPoint, ex.Message);
                        continue;
                    }
                    
                    if (!TryGetRegisteredSessionByUdpEndPoint(
                            transportGeneration,
                            result.RemoteEndPoint,
                            out var session))
                    {
                        _logger.Log(LogLevel.Warning, "Connection",
                            "UDP packet from unregistered endpoint {0}", result.RemoteEndPoint);
                        continue;
                    }

                    int clientId = session.ClientId;
                    if (packet.Header.ClientId != clientId)
                    {
                        _logger.Log(LogLevel.Warning, "Connection",
                            "UDP ClientId mismatch from {0}: registered={1}, header={2}",
                            result.RemoteEndPoint, clientId, packet.Header.ClientId);
                        continue;
                    }

                    session.UpdateActivity();
                    
                    // Reliable ACK
                    if (packet.Header.Flags.HasFlag(PacketFlags.Reliable))
                    {
                        SendAck(packet.Header.SeqNumber, result.RemoteEndPoint, udpServer);

                        if (!session.ReliableManager.TryAcceptIncoming(packet.Header.SeqNumber))
                        {
                            _logger.Log(LogLevel.Trace, "Connection",
                                "Ignored duplicate reliable packet seq={0} from Client {1}",
                                packet.Header.SeqNumber, clientId);
                            continue;
                        }
                    }
                    
                    if (!TryNotifyPacketReceived(
                            packet,
                            clientId,
                            transportGeneration,
                            TransportMode.Server,
                            transportCallbacks.PacketReceived))
                        break;

                    _stats.UdpPacketsReceived++;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    _logger.Log(LogLevel.Trace, "Connection", "UDP ICMP error (harmless): {0}", ex.Message);
                    await Task.Delay(10, ct);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested &&
                        IsCurrentTransport(transportGeneration, TransportMode.Server))
                    {
                        _logger.LogException(ex, "UdpReceiveLoop");
                        await Task.Delay(100, ct);
                    }
                }
            }
        }

        private async Task UdpClientReceiveLoopAsync(
            UdpClient udpClient,
            ReliableUdpManager reliableManager,
            int transportGeneration,
            TransportCallbacks transportCallbacks,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested &&
                   IsCurrentTransport(transportGeneration, TransportMode.Client))
            {
                try
                {
                    var result = await udpClient.ReceiveAsync();

                    if (!IsCurrentTransport(transportGeneration, TransportMode.Client))
                        break;

                    if (TryHandleUdpRegistrationConfirmation(
                            result.Buffer,
                            transportGeneration,
                            udpClient,
                            reliableManager))
                    {
                        continue;
                    }
              
                    if (reliableManager.ProcessAck(result.Buffer))
                    {
                        _logger.Log(LogLevel.Trace, "Connection", "Client ACK processed");
                        continue;
                    }
            
                    try
                    {
                        var packet = NetworkPacket.Deserialize(result.Buffer, result.Buffer.Length);
                        
                        if (packet.Header.Flags.HasFlag(PacketFlags.Reliable))
                        {
                            SendAckToServer(packet.Header.SeqNumber, udpClient);

                            if (!reliableManager.TryAcceptIncoming(packet.Header.SeqNumber))
                            {
                                _logger.Log(LogLevel.Trace, "Connection",
                                    "Ignored duplicate reliable packet seq={0} from server",
                                    packet.Header.SeqNumber);
                                continue;
                            }
                        }
                
                        if (!TryNotifyPacketReceived(
                                packet,
                                0,
                                transportGeneration,
                                TransportMode.Client,
                                transportCallbacks.PacketReceived))
                            break;

                        _stats.UdpPacketsReceived++;
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(LogLevel.Warning, "Connection", "Invalid UDP packet: {0}", ex.Message);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    _logger.Log(LogLevel.Trace, "Connection", "UDP ICMP error (harmless): {0}", ex.Message);
                    await Task.Delay(10, ct);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested &&
                        IsCurrentTransport(transportGeneration, TransportMode.Client))
                    {
                        _logger.LogException(ex, "UdpClientReceiveLoop");
                        await Task.Delay(100, ct);
                    }
                }
            }
        }

        private async Task ProcessTcpStreamAsync(
            NetworkStream stream,
            int senderId,
            int transportGeneration,
            TransportMode mode,
            TransportCallbacks transportCallbacks,
            CancellationToken ct)
        {
            byte[] lengthBuffer = new byte[4];
            byte[] dataBuffer = _bufferPool.Rent();
    
            try
            {
                while (!ct.IsCancellationRequested &&
                       IsCurrentTransport(transportGeneration, mode))
                {
                    if (!await ReadExactAsync(stream, lengthBuffer, 0, 4, ct))
                        break;
            
                    int packetLength = BitConverter.ToInt32(lengthBuffer, 0);
            
                    if (packetLength <= 0 || packetLength > GameConstants.Protocol.MaxPacketSize)
                    {
                        _logger.Log(LogLevel.Error, "Connection", 
                            "Invalid packet length: {0} from client {1}", packetLength, senderId);
                        break;
                    }
            
                    if (dataBuffer.Length < packetLength)
                    {
                        _bufferPool.Return(dataBuffer);
                        dataBuffer = new byte[packetLength];
                    }
            
                    if (!await ReadExactAsync(stream, dataBuffer, 0, packetLength, ct))
                        break;

                    if (mode == TransportMode.Client &&
                        TryHandleUdpRegistrationChallenge(
                            dataBuffer,
                            packetLength,
                            transportGeneration))
                    {
                        continue;
                    }

                    var packet = NetworkPacket.Deserialize(dataBuffer, packetLength);

                    if (!TryNotifyPacketReceived(
                            packet,
                            senderId,
                            transportGeneration,
                            mode,
                            transportCallbacks.PacketReceived))
                        break;

                    _stats.TcpPacketsReceived++;
                }
            }
            finally
            {
                _bufferPool.Return(dataBuffer);
            }
        }

        private async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct);
                if (bytesRead == 0)
                    return false;
                
                totalRead += bytesRead;
            }
            return true;
        }

        private void SendAck(uint seqNumber, IPEndPoint endpoint, UdpClient udpServer)
        {
            try
            {
                var ackPacket = new byte[8];
                BitConverter.GetBytes(GameConstants.Protocol.AckMagicNumber).CopyTo(ackPacket, 0);
                BitConverter.GetBytes(seqNumber).CopyTo(ackPacket, 4);
                
                udpServer?.Send(ackPacket, ackPacket.Length, endpoint);
                
                _logger.Log(LogLevel.Trace, "Connection", "Sent ACK for seq {0}", seqNumber);
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "SendAck");
            }
        }

        private void SendAckToServer(uint seqNumber, UdpClient udpClient)
        {
            try
            {
                var ackPacket = new byte[8];
                BitConverter.GetBytes(GameConstants.Protocol.AckMagicNumber).CopyTo(ackPacket, 0);
                BitConverter.GetBytes(seqNumber).CopyTo(ackPacket, 4);
                
                udpClient?.Send(ackPacket, ackPacket.Length);
                
                _logger.Log(LogLevel.Trace, "Connection", "Sent ACK for seq {0}", seqNumber);
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "SendAckToServer");
            }
        }

        private void DisconnectClient(int clientId)
        {
            ClientSession session;
            Action<int> clientDisconnectedHandler;
            lock (_transportLifecycleLock)
            {
                if (!_sessions.TryGetValue(clientId, out session))
                    return;

                clientDisconnectedHandler = _transportCallbacks?.ClientDisconnected;
            }

            DisconnectClient(session, clientDisconnectedHandler);
        }

        private void DisconnectClient(
            ClientSession session,
            Action<int> clientDisconnectedHandler)
        {
            if (session == null)
                return;

            lock (_transportLifecycleLock)
            {
                if (!TryRemoveExactSession(session))
                    return;

                if (_udpRegistrations.TryGetValue(session.ClientId, out var registration) &&
                    ReferenceEquals(registration.Session, session))
                {
                    _udpRegistrations.TryRemove(session.ClientId, out _);
                }
            }

            session.Dispose();

            InvokeClientEventSubscribersSafely(
                clientDisconnectedHandler,
                session.ClientId,
                "ClientDisconnected");
            _logger.Log(LogLevel.Info, "Connection", "Client {0} disconnected", session.ClientId);
        }

        private bool TryRemoveExactSession(ClientSession session)
        {
            var pair = new KeyValuePair<int, ClientSession>(session.ClientId, session);
            return ((ICollection<KeyValuePair<int, ClientSession>>)_sessions).Remove(pair);
        }

        private int GetNextClientId()
        {
            lock (_clientIdLock)
            {
                return _nextClientId++;
            }
        }

        private void ConfigureUdpSocket(UdpClient client)
        {
            client.Client.ReceiveBufferSize = GameConstants.Protocol.BufferSize * 4;
            client.Client.SendBufferSize = GameConstants.Protocol.BufferSize * 4;
            
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                client.Client.IOControl(
                    (IOControlCode)SIO_UDP_CONNRESET,
                    new byte[] { 0, 0, 0, 0 },
                    null
                );
                _logger.Log(LogLevel.Debug, "Connection", "Windows UDP ICMP errors suppressed");
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Debug, "Connection", "UDP socket configuration: {0}", ex.Message);
            }
        }

        private bool RequiresTcp(PacketType type)
        {
            return type == PacketType.Handshake
                   || type == PacketType.Disconnect
                   || type == PacketType.PlayerLifecycle;
        }

        private void SendUdpRegistrationChallenge(
            ClientSession session,
            ulong token,
            int transportGeneration,
            Action<int> clientDisconnectedHandler)
        {
            byte[] payload = CreateUdpRegistrationPayload(session.ClientId, token);
            byte[] frame = new byte[sizeof(int) + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
            Buffer.BlockCopy(payload, 0, frame, sizeof(int), payload.Length);

            SendTcpFrameToSession(
                session,
                frame,
                transportGeneration,
                clientDisconnectedHandler);
        }

        private bool TryHandleUdpRegistrationChallenge(
            byte[] data,
            int length,
            int transportGeneration)
        {
            if (!IsUdpRegistrationPayload(data, length))
                return false;

            int clientId = BitConverter.ToInt32(data, sizeof(uint));
            ulong token = BitConverter.ToUInt64(data, sizeof(uint) + sizeof(int));
            UdpClient udpClient;
            ReliableUdpManager reliableManager;

            if (clientId <= 0 || token == 0)
            {
                _logger.Log(LogLevel.Warning, "Connection",
                    "Rejected invalid TCP UDP-registration challenge");
                return true;
            }

            byte[] registration = CreateUdpRegistrationPayload(clientId, token);

            lock (_transportLifecycleLock)
            {
                if (!IsCurrentTransportLocked(transportGeneration, TransportMode.Client) ||
                    _udpClient == null ||
                    _clientReliableManager == null)
                {
                    return true;
                }

                udpClient = _udpClient;
                reliableManager = _clientReliableManager;
                _clientUdpRegistrationPayload = registration;
                _clientUdpRegistrationConfirmed = false;
                _clientUdpRegistrationRetryAtUtc = DateTime.MinValue;
            }

            RetryPendingClientUdpRegistration(
                transportGeneration,
                udpClient,
                reliableManager);
            return true;
        }

        private bool TryHandleUdpRegistration(
            byte[] data,
            IPEndPoint remoteEndPoint,
            int transportGeneration,
            out ClientSession registeredSession,
            out List<PendingUdpPacket> pendingReliablePackets)
        {
            registeredSession = null;
            pendingReliablePackets = null;

            if (!IsUdpRegistrationPayload(data, data?.Length ?? 0))
                return false;

            int clientId = BitConverter.ToInt32(data, sizeof(uint));
            ulong token = BitConverter.ToUInt64(data, sizeof(uint) + sizeof(int));
            bool registered = false;

            lock (_transportLifecycleLock)
            {
                if (IsCurrentTransportLocked(transportGeneration, TransportMode.Server) &&
                    _udpRegistrations.TryGetValue(clientId, out var registration) &&
                    registration.Token == token &&
                    _sessions.TryGetValue(clientId, out var currentSession) &&
                    ReferenceEquals(currentSession, registration.Session))
                {
                    bool endpointAlreadyOwned = false;
                    foreach (var existingSession in _sessions.Values)
                    {
                        if (!ReferenceEquals(existingSession, currentSession) &&
                            existingSession.UdpEndPoint?.Equals(remoteEndPoint) == true)
                        {
                            endpointAlreadyOwned = true;
                            break;
                        }
                    }

                    if (!endpointAlreadyOwned &&
                        (currentSession.UdpEndPoint == null ||
                         currentSession.UdpEndPoint.Equals(remoteEndPoint)))
                    {
                        currentSession.UdpEndPoint = remoteEndPoint;
                        currentSession.UpdateActivity();
                        registeredSession = currentSession;
                        pendingReliablePackets = registration.PendingReliableUdp.Drain();
                        registered = true;
                    }
                }
            }

            if (registered)
            {
                _logger.Log(LogLevel.Info, "Connection",
                    "Registered UDP endpoint for Client {0}: {1}", clientId, remoteEndPoint);
            }
            else
            {
                _logger.Log(LogLevel.Warning, "Connection",
                    "Rejected UDP registration for Client {0} from {1}", clientId, remoteEndPoint);
            }

            return true;
        }

        private void RetryPendingClientUdpRegistration(
            int transportGeneration,
            UdpClient udpClient,
            ReliableUdpManager reliableManager)
        {
            byte[] registration = null;
            DateTime now = DateTime.UtcNow;

            lock (_transportLifecycleLock)
            {
                if (!IsCurrentTransportLocked(transportGeneration, TransportMode.Client) ||
                    !ReferenceEquals(_udpClient, udpClient) ||
                    !ReferenceEquals(_clientReliableManager, reliableManager) ||
                    _clientUdpRegistrationConfirmed ||
                    _clientUdpRegistrationPayload == null ||
                    now < _clientUdpRegistrationRetryAtUtc)
                {
                    return;
                }

                registration = _clientUdpRegistrationPayload;
                _clientUdpRegistrationRetryAtUtc = now + UdpRegistrationRetryInterval;
            }

            try
            {
                udpClient.Send(registration, registration.Length);
                _logger.Log(LogLevel.Debug, "Connection",
                    "Sent TCP-correlated UDP endpoint registration");
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException ex)
            {
                _logger.Log(LogLevel.Warning, "Connection",
                    "UDP registration send will retry: {0}", ex.Message);
            }
        }

        private bool TryHandleUdpRegistrationConfirmation(
            byte[] data,
            int transportGeneration,
            UdpClient udpClient,
            ReliableUdpManager reliableManager)
        {
            if (!IsUdpRegistrationPayload(data, data?.Length ?? 0))
                return false;

            List<PendingUdpPacket> pendingReliablePackets;
            lock (_transportLifecycleLock)
            {
                if (!IsCurrentTransportLocked(transportGeneration, TransportMode.Client) ||
                    !ReferenceEquals(_udpClient, udpClient) ||
                    !ReferenceEquals(_clientReliableManager, reliableManager) ||
                    _clientUdpRegistrationPayload == null ||
                    !ByteArraysEqual(_clientUdpRegistrationPayload, data))
                {
                    return true;
                }

                _clientUdpRegistrationConfirmed = true;
                _clientUdpRegistrationPayload = null;
                pendingReliablePackets = _clientPendingReliableUdp.Drain();
            }

            _logger.Log(LogLevel.Info, "Connection",
                "UDP endpoint registration confirmed");

            FlushClientPendingReliableUdp(
                pendingReliablePackets,
                udpClient,
                reliableManager,
                transportGeneration);
            return true;
        }

        private void FlushServerPendingReliableUdp(
            ClientSession session,
            List<PendingUdpPacket> pendingReliablePackets,
            UdpClient udpServer,
            IPEndPoint endpoint,
            int transportGeneration)
        {
            if (pendingReliablePackets == null)
                return;

            foreach (var pendingPacket in pendingReliablePackets)
            {
                if (!TryGetCurrentServerUdpEndPoint(
                        transportGeneration,
                        udpServer,
                        session,
                        out var currentEndpoint) ||
                    currentEndpoint == null ||
                    !currentEndpoint.Equals(endpoint))
                {
                    return;
                }

                session.ReliableManager.TrackPacket(
                    pendingPacket.SeqNumber,
                    pendingPacket.Data);

                try
                {
                    udpServer.Send(
                        pendingPacket.Data,
                        pendingPacket.Data.Length,
                        currentEndpoint);
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Warning, "Connection",
                        "Initial queued reliable UDP send failed for Client {0}: {1}",
                        session.ClientId,
                        ex.Message);
                }
            }
        }

        private void FlushClientPendingReliableUdp(
            List<PendingUdpPacket> pendingReliablePackets,
            UdpClient udpClient,
            ReliableUdpManager reliableManager,
            int transportGeneration)
        {
            if (pendingReliablePackets == null)
                return;

            foreach (var pendingPacket in pendingReliablePackets)
            {
                if (!IsCurrentClientUdpTransport(
                        transportGeneration,
                        udpClient,
                        reliableManager))
                {
                    return;
                }

                reliableManager.TrackPacket(
                    pendingPacket.SeqNumber,
                    pendingPacket.Data);

                try
                {
                    udpClient.Send(pendingPacket.Data, pendingPacket.Data.Length);
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Warning, "Connection",
                        "Initial queued client reliable UDP send failed: {0}", ex.Message);
                }
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];

            return difference == 0;
        }

        private bool TryGetRegisteredSessionByUdpEndPoint(
            int transportGeneration,
            IPEndPoint remoteEndPoint,
            out ClientSession session)
        {
            lock (_transportLifecycleLock)
            {
                if (!IsCurrentTransportLocked(transportGeneration, TransportMode.Server))
                {
                    session = null;
                    return false;
                }

                foreach (var candidate in _sessions.Values)
                {
                    if (candidate.UdpEndPoint?.Equals(remoteEndPoint) == true)
                    {
                        session = candidate;
                        return true;
                    }
                }

                session = null;
                return false;
            }
        }

        private static byte[] CreateUdpRegistrationPayload(int clientId, ulong token)
        {
            byte[] payload = new byte[UdpRegistrationPayloadSize];
            BitConverter.GetBytes(UdpRegistrationMagic).CopyTo(payload, 0);
            BitConverter.GetBytes(clientId).CopyTo(payload, sizeof(uint));
            BitConverter.GetBytes(token).CopyTo(payload, sizeof(uint) + sizeof(int));
            return payload;
        }

        private static bool IsUdpRegistrationPayload(byte[] data, int length)
        {
            return data != null &&
                   length == UdpRegistrationPayloadSize &&
                   BitConverter.ToUInt32(data, 0) == UdpRegistrationMagic;
        }

        private static ulong CreateUdpRegistrationToken()
        {
            byte[] bytes = new byte[sizeof(ulong)];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            ulong token = BitConverter.ToUInt64(bytes, 0);
            return token == 0 ? 1UL : token;
        }

        private int BeginTransport(
            TransportMode mode,
            CancellationToken cancellationToken,
            out CancellationToken transportToken,
            out TransportCallbacks transportCallbacks)
        {
            var transportCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lock (_transportLifecycleLock)
            {
                if (_transportMode != TransportMode.None)
                {
                    transportCts.Dispose();
                    throw new InvalidOperationException("A network transport is already active.");
                }

                _transportGeneration = unchecked(_transportGeneration + 1);
                if (_transportGeneration == 0)
                    _transportGeneration = 1;

                _transportMode = mode;
                _transportCts = transportCts;
                _transportCallbacks = new TransportCallbacks(
                    OnPacketReceived,
                    OnClientConnected,
                    OnClientDisconnected);
                _clientDisconnectSignaled = mode == TransportMode.Client ? 0 : 1;
                _clientUdpRegistrationPayload = null;
                _clientUdpRegistrationRetryAtUtc = DateTime.MinValue;
                _clientUdpRegistrationConfirmed = false;
                _clientPendingReliableUdp.Clear();
                transportToken = transportCts.Token;
                transportCallbacks = _transportCallbacks;
                return _transportGeneration;
            }
        }

        private void EnsureCurrentTransportLocked(int transportGeneration, TransportMode mode)
        {
            if (!IsCurrentTransportLocked(transportGeneration, mode))
                throw new OperationCanceledException("Network transport was superseded.");
        }

        private bool IsCurrentTransport(int transportGeneration, TransportMode mode)
        {
            lock (_transportLifecycleLock)
            {
                return IsCurrentTransportLocked(transportGeneration, mode);
            }
        }

        private bool IsCurrentTransportLocked(int transportGeneration, TransportMode mode)
        {
            return _transportGeneration == transportGeneration &&
                   _transportMode == mode;
        }

        private bool IsCurrentClientTransport(
            int transportGeneration,
            TcpClient tcpClient,
            TcpSendQueue tcpSendQueue)
        {
            lock (_transportLifecycleLock)
            {
                return IsCurrentTransportLocked(transportGeneration, TransportMode.Client) &&
                       ReferenceEquals(_tcpClient, tcpClient) &&
                       ReferenceEquals(_clientTcpSendQueue, tcpSendQueue);
            }
        }

        private bool IsCurrentServerSession(
            int transportGeneration,
            ClientSession session)
        {
            lock (_transportLifecycleLock)
            {
                return IsCurrentTransportLocked(transportGeneration, TransportMode.Server) &&
                       _sessions.TryGetValue(session.ClientId, out var currentSession) &&
                       ReferenceEquals(currentSession, session);
            }
        }

        private bool TryGetCurrentServerUdpEndPoint(
            int transportGeneration,
            UdpClient udpSocket,
            ClientSession session,
            out IPEndPoint endpoint)
        {
            lock (_transportLifecycleLock)
            {
                if (udpSocket == null ||
                    !IsCurrentTransportLocked(transportGeneration, TransportMode.Server) ||
                    !ReferenceEquals(_udpServer, udpSocket) ||
                    !_sessions.TryGetValue(session.ClientId, out var currentSession) ||
                    !ReferenceEquals(currentSession, session) ||
                    !_udpRegistrations.TryGetValue(session.ClientId, out var registration) ||
                    !ReferenceEquals(registration.Session, session))
                {
                    endpoint = null;
                    return false;
                }

                endpoint = session.UdpEndPoint;
                return true;
            }
        }

        private bool IsCurrentClientUdpTransport(
            int transportGeneration,
            UdpClient udpSocket,
            ReliableUdpManager reliableManager)
        {
            lock (_transportLifecycleLock)
            {
                return udpSocket != null &&
                       reliableManager != null &&
                       IsCurrentTransportLocked(transportGeneration, TransportMode.Client) &&
                       ReferenceEquals(_udpClient, udpSocket) &&
                       ReferenceEquals(_clientReliableManager, reliableManager);
            }
        }

        private bool TryNotifyPacketReceived(
            NetworkPacket packet,
            int senderId,
            int transportGeneration,
            TransportMode mode,
            Action<NetworkPacket, int> packetReceivedHandler)
        {
            if (!IsCurrentTransport(transportGeneration, mode))
                return false;

            packetReceivedHandler?.Invoke(packet, senderId);
            return true;
        }

        private void SignalClientDisconnected(int transportGeneration)
        {
            if (!CleanupTransport(transportGeneration, notifyClient: true))
                return;

            _logger.Log(LogLevel.Info, "Connection", "Server connection closed");
        }

        private bool CleanupTransport(int transportGeneration, bool notifyClient)
        {
            TcpListener tcpListener;
            UdpClient udpServer;
            TcpClient tcpClient;
            UdpClient udpClient;
            ReliableUdpManager reliableManager;
            TcpSendQueue clientTcpSendQueue;
            CancellationTokenSource transportCts;
            List<ClientSession> sessions;
            bool shouldNotifyClient;
            Action<int> clientDisconnectedHandler;

            lock (_transportLifecycleLock)
            {
                if (_transportGeneration != transportGeneration)
                    return false;

                shouldNotifyClient = notifyClient &&
                    _transportMode == TransportMode.Client &&
                    _clientDisconnectSignaled == 0;

                if (notifyClient && !shouldNotifyClient)
                    return false;

                clientDisconnectedHandler = shouldNotifyClient
                    ? _transportCallbacks?.ClientDisconnected
                    : null;

                _clientDisconnectSignaled = 1;
                _transportGeneration = unchecked(_transportGeneration + 1);
                if (_transportGeneration == 0)
                    _transportGeneration = 1;
                _transportMode = TransportMode.None;
                _transportCallbacks = null;

                transportCts = _transportCts;
                _transportCts = null;

                tcpListener = _tcpListener;
                _tcpListener = null;
                udpServer = _udpServer;
                _udpServer = null;

                tcpClient = _tcpClient;
                _tcpClient = null;
                udpClient = _udpClient;
                _udpClient = null;
                _serverEndPoint = null;

                reliableManager = _clientReliableManager;
                _clientReliableManager = null;
                clientTcpSendQueue = _clientTcpSendQueue;
                _clientTcpSendQueue = null;
                _clientUdpRegistrationPayload = null;
                _clientUdpRegistrationRetryAtUtc = DateTime.MinValue;
                _clientUdpRegistrationConfirmed = false;
                _clientPendingReliableUdp.Clear();

                sessions = new List<ClientSession>(_sessions.Values);
                _sessions.Clear();
                _udpRegistrations.Clear();
            }

            try
            {
                transportCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            CloseListener(tcpListener);
            clientTcpSendQueue?.Dispose();
            CloseTcpClient(tcpClient);
            CloseUdpClient(udpServer);
            CloseUdpClient(udpClient);

            reliableManager?.Clear();
            foreach (var session in sessions)
                session.Dispose();

            transportCts?.Dispose();

            if (shouldNotifyClient)
            {
                InvokeClientEventSubscribersSafely(
                    clientDisconnectedHandler,
                    0,
                    "ClientDisconnected");
            }

            return shouldNotifyClient;
        }

        private void InvokeClientEventSubscribersSafely(
            Action<int> subscribers,
            int clientId,
            string context)
        {
            if (subscribers == null)
                return;

            foreach (Action<int> subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    subscriber(clientId);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, context);
                }
            }
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void CloseListener(TcpListener listener)
        {
            try
            {
                listener?.Stop();
            }
            catch
            {
            }
        }

        private static void CloseTcpClient(TcpClient client)
        {
            try
            {
                client?.Close();
            }
            catch
            {
            }
        }

        private static void CloseUdpClient(UdpClient client)
        {
            try
            {
                client?.Close();
            }
            catch
            {
            }
        }

        public void Shutdown()
        {
            try
            {
                lock (_transportLifecycleLock)
                {
                    CleanupTransport(_transportGeneration, notifyClient: false);
                }

                _bufferPool.Clear();
                 
                _logger.Log(LogLevel.Info, "Connection", "Connection manager shutdown complete");
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "Shutdown");
            }
        }
    }
}
