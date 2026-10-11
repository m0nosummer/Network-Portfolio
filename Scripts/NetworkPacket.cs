using System;
using System.IO;

namespace Arena.Network
{
    // [Header 23 bytes][Payload N bytes][Checksum 4 bytes]
    public class NetworkPacket
    {
        public PacketHeader Header { get; set; } = new ();
        public byte[] Payload { get; set; }
        public uint Checksum { get; set; }
        public int SenderId => Header.ClientId;
        public int TargetId => Header.TargetClientId;
        
        public byte[] Serialize()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            
            Header.PayloadSize = (ushort)(Payload?.Length ?? 0);
            Header.Serialize(writer);
            
            if (Payload != null && Payload.Length > 0)
            {
                writer.Write(Payload);
            }
            
            var dataBeforeChecksum = stream.ToArray();
            Checksum = CalculateChecksum(dataBeforeChecksum);
            writer.Write(Checksum);
            
            return stream.ToArray();
        }
        
        public static NetworkPacket Deserialize(byte[] data, int length)
        {
            if (data == null || length < 24)
            {
                throw new InvalidDataException("Packet data too small!");
            }
            
            using var stream = new MemoryStream(data, 0, length);
            using var reader = new BinaryReader(stream);
            
            var packet = new NetworkPacket
            {
                Header = PacketHeader.Deserialize(reader)
            };
            
            if (packet.Header.PayloadSize > 0)
            {
                packet.Payload = reader.ReadBytes(packet.Header.PayloadSize);
            }
            
            packet.Checksum = reader.ReadUInt32();
            var calculatedChecksum = CalculateChecksum(data, (int)(stream.Position - 4));
            
            if (packet.Checksum != calculatedChecksum)
            {
                throw new InvalidDataException($"Checksum mismatch! Expected: {packet.Checksum}, Got: {calculatedChecksum}");
            }
            
            return packet;
        }
        
        private static uint CalculateChecksum(byte[] data, int length = -1)
        {
            if (length == -1) length = data.Length;
            
            uint checksum = 0;
            for (int i = 0; i < length; i++)
            {
                checksum = ((checksum << 1) | (checksum >> 31)) ^ data[i];
            }
            return checksum;
        }
    }
}