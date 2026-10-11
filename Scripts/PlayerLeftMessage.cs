using System.IO;
using Arena.Core;

namespace Arena.Network.Messages
{
    // Server→All Clients, TCP
    public class PlayerLeftMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerLeft;
        
        public int PlayerId { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(PlayerId);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            PlayerId = reader.ReadInt32();
        }
    }
}