using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Arena.Logging;

namespace Arena.Network
{
    public enum TcpFrameQueueResult
    {
        Queued,
        QueueFull,
        Disposed
    }

    internal sealed class TcpSendQueue : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly IGameLogger _logger;
        private readonly string _context;
        private readonly Action<Exception> _onWriteFault;
        private readonly int _maxQueuedFrames;
        private readonly int _maxQueuedBytes;
        private readonly object _queueLock = new();
        private readonly Queue<byte[]> _frames = new();
        private readonly SemaphoreSlim _frameAvailable = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _writerTask;

        private bool _acceptingFrames = true;
        private int _queuedBytes;
        private int _stopRequested;
        private int _queueFullLogged;
        private int _disposedQueueLogged;

        public Task Completion => _writerTask;

        public TcpSendQueue(
            NetworkStream stream,
            int maxQueuedFrames,
            int maxQueuedBytes,
            IGameLogger logger,
            string context,
            Action<Exception> onWriteFault)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));

            if (maxQueuedFrames <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxQueuedFrames));

            if (maxQueuedBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));

            _maxQueuedFrames = maxQueuedFrames;
            _maxQueuedBytes = maxQueuedBytes;
            _logger = logger;
            _context = string.IsNullOrWhiteSpace(context) ? "TCP send queue" : context;
            _onWriteFault = onWriteFault;
            _writerTask = Task.Run(ProcessQueueAsync);
        }

        public TcpFrameQueueResult TryQueue(byte[] frameData)
        {
            if (frameData == null)
                throw new ArgumentNullException(nameof(frameData));

            if (frameData.Length == 0)
                throw new ArgumentException("TCP frame cannot be empty.", nameof(frameData));

            TcpFrameQueueResult result;
            int queuedFrames;
            int queuedBytes;

            lock (_queueLock)
            {
                if (!_acceptingFrames || Volatile.Read(ref _stopRequested) != 0)
                {
                    result = TcpFrameQueueResult.Disposed;
                }
                else if (_frames.Count >= _maxQueuedFrames ||
                         frameData.Length > _maxQueuedBytes - _queuedBytes)
                {
                    result = TcpFrameQueueResult.QueueFull;
                }
                else
                {
                    _frames.Enqueue(frameData);
                    _queuedBytes += frameData.Length;
                    _frameAvailable.Release();
                    return TcpFrameQueueResult.Queued;
                }

                queuedFrames = _frames.Count;
                queuedBytes = _queuedBytes;
            }

            if (result == TcpFrameQueueResult.QueueFull)
            {
                LogQueueFullOnce(queuedFrames, queuedBytes, frameData.Length);
            }
            else
            {
                LogDisposedQueueOnce();
            }

            return result;
        }

        public Task DisposeAsync()
        {
            RequestStop();
            return _writerTask;
        }

        public void Dispose()
        {
            RequestStop();
        }

        private async Task ProcessQueueAsync()
        {
            CancellationToken cancellationToken = _cts.Token;

            try
            {
                while (true)
                {
                    await _frameAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);

                    byte[] frameData;
                    lock (_queueLock)
                    {
                        if (_frames.Count == 0)
                        {
                            if (!_acceptingFrames)
                                break;

                            continue;
                        }

                        frameData = _frames.Dequeue();
                        _queuedBytes -= frameData.Length;

                        if (_frames.Count <= _maxQueuedFrames / 2 &&
                            _queuedBytes <= _maxQueuedBytes / 2)
                        {
                            Volatile.Write(ref _queueFullLogged, 0);
                        }
                    }

                    await _stream.WriteAsync(
                            frameData,
                            0,
                            frameData.Length,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _stopRequested) != 0)
            {
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _stopRequested) != 0)
            {
            }
            catch (Exception) when (Volatile.Read(ref _stopRequested) != 0)
            {
            }
            catch (Exception ex)
            {
                RequestStop();
                SafeLogException(ex, $"{_context}-Writer");

                try
                {
                    _onWriteFault?.Invoke(ex);
                }
                catch (Exception callbackException)
                {
                    SafeLogException(callbackException, $"{_context}-FaultCallback");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _stopRequested, 1);

                lock (_queueLock)
                {
                    _acceptingFrames = false;
                    _frames.Clear();
                    _queuedBytes = 0;
                }

                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _frameAvailable.Dispose();
                _cts.Dispose();
            }
        }

        private void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
                return;

            lock (_queueLock)
            {
                _acceptingFrames = false;
                _frames.Clear();
                _queuedBytes = 0;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void LogQueueFullOnce(int queuedFrames, int queuedBytes, int rejectedFrameBytes)
        {
            if (Interlocked.Exchange(ref _queueFullLogged, 1) != 0)
                return;

            SafeLog(
                LogLevel.Error,
                "TCP send queue full for {0}: frames={1}/{2}, bytes={3}/{4}, rejected={5}",
                _context,
                queuedFrames,
                _maxQueuedFrames,
                queuedBytes,
                _maxQueuedBytes,
                rejectedFrameBytes);
        }

        private void LogDisposedQueueOnce()
        {
            if (Interlocked.Exchange(ref _disposedQueueLogged, 1) != 0)
                return;

            SafeLog(
                LogLevel.Warning,
                "TCP frame rejected because the send queue is stopped: {0}",
                _context);
        }

        private void SafeLog(LogLevel level, string message, params object[] args)
        {
            try
            {
                _logger?.Log(level, "TcpSendQueue", message, args);
            }
            catch
            {
            }
        }

        private void SafeLogException(Exception exception, string context)
        {
            try
            {
                _logger?.LogException(exception, context);
            }
            catch
            {
            }
        }
    }
}
