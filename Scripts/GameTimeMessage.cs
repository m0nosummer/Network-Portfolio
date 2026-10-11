using System.IO;
using Arena.Core;

namespace Arena.Network.Messages
{
    public class GameTimeMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.GameTime;
        public float RemainingTime { get; set; }
        public bool IsNight { get; set; }
        
        public float CycleDuration { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(RemainingTime);
            writer.Write(IsNight);
            writer.Write(CycleDuration);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            RemainingTime = reader.ReadSingle();
            IsNight = reader.ReadBoolean();
            CycleDuration = reader.ReadSingle();
        }
    }
}