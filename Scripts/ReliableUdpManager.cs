using System;
using System.Collections.Generic;
using System.Diagnostics;
using Arena.Core;

namespace Arena.Network
{
    public class ReliableUdpManager
    {
        private class PendingPacket
        {
            public uint SeqNumber;
            public byte[] Data;
            public double LastSendTime;
            public int RetryCount;
        }

        private readonly Dictionary<uint, PendingPacket> _pendingPackets = new();
        private readonly HashSet<uint> _receivedSeqs = new();
        private readonly Queue<uint> _receivedSeqOrder = new();
        private readonly Stopwatch _stopwatch = new();
        private readonly object _lock = new();

        private const int MaxTrackedReceivedSeqs = 2048;
        
        private double _retryTimeout = 0.2;
        private int _maxRetries = 5;

        public ReliableUdpManager()
        {
            _stopwatch.Start();
        }
        
        public void Configure(float retryTimeout, int maxRetries)
        {
            _retryTimeout = retryTimeout;
            _maxRetries = maxRetries;
        }

        public void TrackPacket(uint seqNumber, byte[] data)
        {
            lock (_lock)
            {
                _pendingPackets[seqNumber] = new PendingPacket
                {
                    SeqNumber = seqNumber,
                    Data = data,
                    LastSendTime = _stopwatch.Elapsed.TotalSeconds,
                    RetryCount = 0
                };
            }
        }

        public bool ProcessAck(byte[] data)
        {
            if (data.Length == 8)
            {
                uint magic = BitConverter.ToUInt32(data, 0);
                if (magic == GameConstants.Protocol.AckMagicNumber)
                {
                    uint seq = BitConverter.ToUInt32(data, 4);
                    lock (_lock)
                    {
                        _pendingPackets.Remove(seq);
                    }
                    return true;
                }
            }
            return false;
        }

        public bool TryAcceptIncoming(uint seqNumber)
        {
            lock (_lock)
            {
                if (_receivedSeqs.Contains(seqNumber))
                    return false;

                _receivedSeqs.Add(seqNumber);
                _receivedSeqOrder.Enqueue(seqNumber);

                while (_receivedSeqOrder.Count > MaxTrackedReceivedSeqs)
                {
                    uint oldestSeq = _receivedSeqOrder.Dequeue();
                    _receivedSeqs.Remove(oldestSeq);
                }

                return true;
            }
        }

        public void Update()
        {
            lock (_lock)
            {
                double currentTime = _stopwatch.Elapsed.TotalSeconds;
                var toRemove = new List<uint>();

                foreach (var kvp in _pendingPackets)
                {
                    var packet = kvp.Value;

                    if (currentTime - packet.LastSendTime > _retryTimeout)
                    {
                        if (packet.RetryCount >= _maxRetries)
                        {
                            UnityEngine.Debug.LogWarning($"[ReliableUDP] Packet {packet.SeqNumber} failed after {_maxRetries} retries");
                            toRemove.Add(kvp.Key);
                        }
                    }
                }

                foreach (var seq in toRemove)
                {
                    _pendingPackets.Remove(seq);
                }
            }
        }

        public List<byte[]> GetPacketsToRetry()
        {
            lock (_lock)
            {
                var packetsToRetry = new List<byte[]>();
                double currentTime = _stopwatch.Elapsed.TotalSeconds;
                
                foreach (var kvp in _pendingPackets)
                {
                    var packet = kvp.Value;

                    if (currentTime - packet.LastSendTime > _retryTimeout && packet.RetryCount < _maxRetries)
                    {
                        packet.LastSendTime = currentTime;
                        packet.RetryCount++;
                        packetsToRetry.Add(packet.Data);
                    }
                }

                return packetsToRetry;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _pendingPackets.Clear();
                _receivedSeqs.Clear();
                _receivedSeqOrder.Clear();
            }
        }
    }
}
