using System;
using System.Collections.Generic;
using Arena.Core;
using Arena.Logging;

namespace Arena.Network
{
    public class PacketProcessor : IPacketProcessor
    {
        private readonly IGameLogger _logger;
        private readonly PacketManager _packetManager;
        private uint _seqNumber;
        private readonly object _seqLock = new();
        
        public PacketProcessor(IGameLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _packetManager = new PacketManager(logger);
        }

        public NetworkPacket[] CreatePackets(INetworkMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            try
            {
                var messageData = message.Serialize();

                var packets = new List<NetworkPacket>();

                if (messageData.Length <= GameConstants.Protocol.MaxPayloadSize)
                {
                    var packet = CreateSinglePacket(messageData, message.Type);
                    packet.Header.TargetClientId = message.TargetId;
                    packets.Add(packet);
                }
                else
                {
                    var fragmentedPackets = CreateFragmentedPackets(messageData, message.Type);
                    foreach (var packet in fragmentedPackets)
                    {
                        packet.Header.TargetClientId = message.TargetId;
                    }
                    packets.AddRange(fragmentedPackets);
                }

                return packets.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "CreatePackets");
                throw new NetworkException("Failed to create packets", NetworkErrorCode.PacketCreationFailed, ex);
            }
        }

        public INetworkMessage ProcessPacket(NetworkPacket packet)
        {
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));

            if (packet.Header.Flags.HasFlag(PacketFlags.ReservedCompression))
            {
                _logger.Log(LogLevel.Warning, "Packet", "Unsupported compression flag; packet dropped");
                return null;
            }

            try
            {
                INetworkMessage message;
        
                if (packet.Header.Flags.HasFlag(PacketFlags.Fragment))
                {
                    message = _packetManager.ProcessFragmentedPacket(packet);
                }
                else
                {
                    message = ProcessSinglePacket(packet);
                }
                return message;
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "ProcessPacket");
                return null;
            }
        }

        private NetworkPacket CreateSinglePacket(byte[] data, MessageType messageType)
        {
            var flags = PacketFlags.None;
            if (IsCriticalMessageType(messageType))
            {
                flags |= PacketFlags.Reliable;
            }
            
            var packet = new NetworkPacket
            {
                Header = new PacketHeader
                {
                    Type = GetPacketTypeFromMessage(messageType),
                    SeqNumber = GetNextSeqNumber(),
                    Flags = flags
                },
                Payload = data
            };

            return packet;
        }

        private List<NetworkPacket> CreateFragmentedPackets(byte[] data, MessageType messageType)
        {
            var packets = new List<NetworkPacket>();
            var totalFragments = (int)Math.Ceiling((double)data.Length / GameConstants.Protocol.MaxPayloadSize);
            if (totalFragments > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(data), "Too many fragments in one message");

            uint groupSeq = ReserveSeqNumbers(totalFragments + 1);
            var fragmentId = (ushort)(groupSeq & 0xFFFF);

            for (int i = 0; i < totalFragments; i++)
            {
                var offset = i * GameConstants.Protocol.MaxPayloadSize;
                var length = Math.Min(GameConstants.Protocol.MaxPayloadSize, data.Length - offset);
                var fragmentData = new byte[length];
                Array.Copy(data, offset, fragmentData, 0, length);

                var flags = PacketFlags.Fragment;
                if (i == totalFragments - 1) flags |= PacketFlags.LastFragment;
                if (IsCriticalMessageType(messageType)) flags |= PacketFlags.Reliable;

                var packet = new NetworkPacket
                {
                    Header = new PacketHeader
                    {
                        Type = GetPacketTypeFromMessage(messageType),
                        SeqNumber = unchecked(groupSeq + (uint)i + 1),
                        FragmentId = fragmentId,
                        Flags = flags
                    },
                    Payload = fragmentData
                };

                packets.Add(packet);
            }

            _logger.Log(LogLevel.Debug, "Packet", "Created {0} fragments", totalFragments);
            return packets;
        }

        private INetworkMessage ProcessSinglePacket(NetworkPacket packet)
        {
            var data = packet.Payload;

            return MessageFactory.CreateMessage(data, data.Length);
        }

        private uint GetNextSeqNumber()
        {
            return ReserveSeqNumbers(1);
        }

        private uint ReserveSeqNumbers(int count)
        {
            lock (_seqLock)
            {
                uint first = unchecked(_seqNumber + 1);
                _seqNumber = unchecked(_seqNumber + (uint)count);
                return first;
            }
        }

        private bool IsCriticalMessageType(MessageType type)
        {
            return type == MessageType.Fire 
                   || type == MessageType.BulletSpawn 
                   || type == MessageType.BulletDestroy
                   || type == MessageType.PlayerDeath
                   || type == MessageType.Respawn;
        }

        private PacketType GetPacketTypeFromMessage(MessageType messageType)
        {
            return messageType switch
            {
                MessageType.Handshake => PacketType.Handshake,
                MessageType.Heartbeat => PacketType.Heartbeat,
                MessageType.PlayerJoined => PacketType.PlayerLifecycle,
                MessageType.PlayerLeft => PacketType.PlayerLifecycle,
                _ => PacketType.GameData
            };
        }
    }
}
