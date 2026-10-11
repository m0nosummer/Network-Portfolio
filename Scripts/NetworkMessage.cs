using System;
using System.IO;
using Arena.Core;

namespace Arena.Network
{
    [Serializable]
    public abstract class NetworkMessage : INetworkMessage
    {
        public abstract MessageType Type { get; }
        public int TargetId { get; set; } = -1; // broadcast
        public float Timestamp { get; set; }
        
        protected NetworkMessage()
        {
            //TODO 보간할 때 쓰기?
            Timestamp = (float)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        }
        
        public virtual byte[] Serialize()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)Type);
                writer.Write(TargetId);
                writer.Write(Timestamp);
                SerializeData(writer);
                return stream.ToArray();
            }
        }
        
        public virtual void Deserialize(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            using (var reader = new BinaryReader(stream))
            {
                var type = (MessageType)reader.ReadByte();
                TargetId = reader.ReadInt32();
                Timestamp = reader.ReadSingle();
                DeserializeData(reader);
            }
        }
        
        protected abstract void SerializeData(BinaryWriter writer);
        protected abstract void DeserializeData(BinaryReader reader);
    }
}