using System.IO;
using UnityEngine;
using Arena.Core;

namespace Arena.Network.Messages
{
    // Server -> Client
    public class BulletSpawnMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.BulletSpawn;
        
        public uint BulletId { get; set; }           // ID given by server
        public int OwnerId { get; set; }             // Player who fire
        public Vector3 SpawnPosition { get; set; }
        public Vector3 Direction { get; set; }
        public float Speed { get; set; }
        public float Damage { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(BulletId);
            writer.Write(OwnerId);
            
            writer.Write(SpawnPosition.x);
            writer.Write(SpawnPosition.y);
            writer.Write(SpawnPosition.z);
            
            writer.Write(Direction.x);
            writer.Write(Direction.y);
            writer.Write(Direction.z);
            
            writer.Write(Speed);
            writer.Write(Damage);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            BulletId = reader.ReadUInt32();
            OwnerId = reader.ReadInt32();
            
            SpawnPosition = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            
            Direction = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            
            Speed = reader.ReadSingle();
            Damage = reader.ReadSingle();
        }
    }
}