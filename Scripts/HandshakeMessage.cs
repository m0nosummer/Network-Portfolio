using System.IO;
using Arena.Core;

namespace Arena.Network.Messages
{
    public class HandshakeMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.Handshake;
        
        public string ClientVersion { get; set; } = "1.0.0";
        public string PlayerName { get; set; } = "Player";
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(ClientVersion ?? string.Empty);
            writer.Write(PlayerName ?? string.Empty);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            ClientVersion = reader.ReadString();
            PlayerName = reader.ReadString();
        }
    }
}