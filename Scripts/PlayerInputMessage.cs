using System.IO;
using UnityEngine;
using Arena.Core;

namespace Arena.Network.Messages
{
    public class PlayerInputMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerInput;
        
        public Vector2 MoveInput { get; set; }
        public Vector3 MouseWorldPosition { get; set; }
        public bool AttackPressed { get; set; }
        public uint SeqNumber { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(MoveInput.x);
            writer.Write(MoveInput.y);
            writer.Write(MouseWorldPosition.x);
            writer.Write(MouseWorldPosition.y);
            writer.Write(MouseWorldPosition.z);
            writer.Write(AttackPressed);
            writer.Write(SeqNumber);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            MoveInput = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            MouseWorldPosition = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(), 
                reader.ReadSingle()
            );
            AttackPressed = reader.ReadBoolean();
            SeqNumber = reader.ReadUInt32();
        }
    }
}
