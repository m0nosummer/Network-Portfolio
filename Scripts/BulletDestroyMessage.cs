using System.IO;
using Arena.Core;

namespace Arena.Network.Messages
{
    // Server -> Client
    public class BulletDestroyMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.BulletDestroy;
        public uint BulletId { get; set; }
        public DestroyReason Reason { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(BulletId);
            writer.Write((byte)Reason);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            BulletId = reader.ReadUInt32();
            Reason = (DestroyReason)reader.ReadByte();
        }
    }
    
    public enum DestroyReason : byte
    {
        HitPlayer,
        HitWall,
        Timeout
    }
}