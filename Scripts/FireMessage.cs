using System.IO;
using UnityEngine;
using Arena.Core;

namespace Arena.Network.Messages
{
    public class FireMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.Fire;
        
        public Vector3 FirePosition { get; set; }
        public Vector3 FireDirection { get; set; }
        public float Damage { get; set; }
        public float BulletSpeed { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            // Position
            writer.Write(FirePosition.x);
            writer.Write(FirePosition.y);
            writer.Write(FirePosition.z);
            
            // Direction
            writer.Write(FireDirection.x);
            writer.Write(FireDirection.y);
            writer.Write(FireDirection.z);
            
            // Stats
            writer.Write(Damage);
            writer.Write(BulletSpeed);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            // Position
            FirePosition = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            
            // Direction
            FireDirection = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            
            // Stats
            Damage = reader.ReadSingle();
            BulletSpeed = reader.ReadSingle();
        }
    }
}