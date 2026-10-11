using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Arena.Logging;

namespace Arena.Network
{
    public class ClientSession : IDisposable
    {
        private const int MaxPendingTcpFrames = 8192;
        private const int MaxPendingTcpBytes = 1024 * 1024;

        public int ClientId { get; }
        public TcpClient TcpClient { get; }
        public IPEndPoint UdpEndPoint { get; set; }
        public ReliableUdpManager ReliableManager { get; }
        
        private readonly IGameLogger _logger;
        private readonly TcpSendQueue _tcpSendQueue;
        private int _disposed;
        
        public bool IsConnected => Volatile.Read(ref _disposed) == 0 &&
                                   (TcpClient?.Connected ?? false);
        public DateTime ConnectedAt { get; }
        public DateTime LastActivityAt { get; private set; }
        
        public ClientSession(int clientId, TcpClient tcpClient, IGameLogger logger)
        {
            ClientId = clientId;
            TcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
            _logger = logger;
            ReliableManager = new ReliableUdpManager();
            ConnectedAt = DateTime.UtcNow;
            LastActivityAt = DateTime.UtcNow;

            _tcpSendQueue = new TcpSendQueue(
                TcpClient.GetStream(),
                MaxPendingTcpFrames,
                MaxPendingTcpBytes,
                _logger,
                $"ClientSession-{clientId}",
                OnTcpWriteFault);
            
            _logger.Log(LogLevel.Debug, "Session", "Session {0} created", clientId);
        }
        
        public void UpdateActivity()
        {
            LastActivityAt = DateTime.UtcNow;
        }

        public TcpFrameQueueResult TryQueueTcpFrame(byte[] frameData)
        {
            if (frameData == null)
                throw new ArgumentNullException(nameof(frameData));

            if (Volatile.Read(ref _disposed) != 0)
                return TcpFrameQueueResult.Disposed;

            return _tcpSendQueue.TryQueue(frameData);
        }

        public void SendTcpFrame(byte[] frameData)
        {
            TcpFrameQueueResult result = TryQueueTcpFrame(frameData);

            if (result == TcpFrameQueueResult.QueueFull)
            {
                throw new InvalidOperationException(
                    $"TCP send queue is full for session {ClientId}.");
            }

            if (result == TcpFrameQueueResult.Disposed)
            {
                throw new ObjectDisposedException(nameof(ClientSession));
            }
        }

        public Task WaitForTcpSendQueueAsync()
        {
            return _tcpSendQueue.Completion;
        }

        private void OnTcpWriteFault(Exception exception)
        {
            Dispose();
        }
        
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _tcpSendQueue?.Dispose();

            try
            {
                ReliableManager?.Clear();
            }
            catch (Exception ex)
            {
                _logger?.LogException(ex, $"ClientSession-Reliable-{ClientId}");
            }

            try
            {
                TcpClient?.Close();
                _logger?.Log(LogLevel.Debug, "Session", "Session {0} disposed", ClientId);
            }
            catch (Exception ex)
            {
                _logger?.LogException(ex, $"ClientSession-{ClientId}");
            }
        }
    }
}
