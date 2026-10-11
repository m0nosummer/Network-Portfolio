using System.IO;
using UnityEngine;
using Arena.Core;

namespace Arena.Network.Messages
{
    public class PlayerStateMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerState;
        
        public int PlayerId { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Velocity { get; set; }
        public float Health { get; set; }
        public bool IsAlive { get; set; }
        public uint LastProcessedInput { get; set; }
        public uint TransportSeqNumber { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Position.z);
            writer.Write(Rotation.x);
            writer.Write(Rotation.y);
            writer.Write(Rotation.z);
            writer.Write(Rotation.w);
            writer.Write(Velocity.x);
            writer.Write(Velocity.y);
            writer.Write(Velocity.z);
            writer.Write(Health);
            writer.Write(IsAlive);
            writer.Write(LastProcessedInput);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            PlayerId = reader.ReadInt32();
            Position = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            Rotation = new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            Velocity = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            Health = reader.ReadSingle();
            IsAlive = reader.ReadBoolean();
            LastProcessedInput = reader.ReadUInt32();
        }
    }
}
