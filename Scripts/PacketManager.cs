using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Arena.Logging;

namespace Arena.Network
{
    internal class PacketManager
    {
        private readonly IGameLogger _logger;
        private readonly Dictionary<FragmentKey, FragmentGroup> _fragmentBuffer = new();
        private readonly object _fragmentLock = new();

        private const float FragmentTimeoutSeconds = 5f;

        public PacketManager(IGameLogger logger)
        {
            _logger = logger;
        }

        public INetworkMessage ProcessFragmentedPacket(NetworkPacket packet)
        {
            lock (_fragmentLock)
            {
                ushort fragmentIndex = GetFragmentIndex(packet);

                if (fragmentIndex == 0)
                {
                    _logger.Log(LogLevel.Error, "Packet",
                        "Invalid fragment index for group {0}", packet.Header.FragmentId);
                    return null;
                }

                uint groupSeq = unchecked(packet.Header.SeqNumber - fragmentIndex);
                var key = new FragmentKey(packet.Header.ClientId, groupSeq);

                if (!_fragmentBuffer.ContainsKey(key))
                {
                    _fragmentBuffer[key] = new FragmentGroup
                    {
                        ReceivedTime = Time.time,
                        Packets = new Dictionary<ushort, NetworkPacket>()
                    };
                }

                var group = _fragmentBuffer[key];

                if (group.Packets.ContainsKey(fragmentIndex))
                {
                    _logger.Log(LogLevel.Debug, "Packet",
                        "Duplicate fragment {0} ignored for group {1}", fragmentIndex, packet.Header.FragmentId);
                    CleanupOldFragments();
                    return null;
                }

                group.Packets[fragmentIndex] = packet;

                if (packet.Header.Flags.HasFlag(PacketFlags.LastFragment))
                {
                    group.ExpectedCount = fragmentIndex;
                }

                if (group.ExpectedCount > 0 && group.Packets.Count >= group.ExpectedCount)
                {
                    _fragmentBuffer.Remove(key);
                    return ReassembleFragments(group);
                }

                CleanupOldFragments();
                return null;
            }
        }

        private INetworkMessage ReassembleFragments(FragmentGroup group)
        {
            using var stream = new MemoryStream();

            for (int index = 1; index <= group.ExpectedCount; index++)
            {
                if (!group.Packets.TryGetValue((ushort)index, out var fragment))
                {
                    _logger.Log(LogLevel.Error, "Packet", "Missing fragment {0}", index);
                    return null;
                }

                stream.Write(fragment.Payload, 0, fragment.Payload.Length);
            }

            var data = stream.ToArray();

            return MessageFactory.CreateMessage(data, data.Length);
        }

        private static ushort GetFragmentIndex(NetworkPacket packet)
        {
            return unchecked((ushort)(packet.Header.SeqNumber - (uint)packet.Header.FragmentId));
        }

        private void CleanupOldFragments()
        {
            var toRemove = new List<FragmentKey>();

            foreach (var kvp in _fragmentBuffer)
            {
                if (Time.time - kvp.Value.ReceivedTime > FragmentTimeoutSeconds)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
            {
                _logger.Log(LogLevel.Warning, "Packet", "Fragment group {0} timed out", id.GroupSeq);
                _fragmentBuffer.Remove(id);
            }
        }

        private readonly struct FragmentKey : IEquatable<FragmentKey>
        {
            public readonly int ClientId;
            public readonly uint GroupSeq;

            public FragmentKey(int clientId, uint groupSeq)
            {
                ClientId = clientId;
                GroupSeq = groupSeq;
            }

            public bool Equals(FragmentKey other)
            {
                return ClientId == other.ClientId && GroupSeq == other.GroupSeq;
            }

            public override bool Equals(object obj)
            {
                return obj is FragmentKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return (ClientId * 397) ^ unchecked((int)GroupSeq);
            }
        }

        private class FragmentGroup
        {
            public Dictionary<ushort, NetworkPacket> Packets;
            public float ReceivedTime;
            public int ExpectedCount = -1;
        }
    }
}
