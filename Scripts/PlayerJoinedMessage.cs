using System.IO;
using UnityEngine;
using Arena.Core;

namespace Arena.Network.Messages
{
    // Server→All Clients, TCP
    public class PlayerJoinedMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerJoined;
        
        public int PlayerId { get; set; }
        public string PlayerName { get; set; } = "Player";
        public Vector3 SpawnPosition { get; set; }
        public float Health { get; set; }
        
        protected override void SerializeData(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(PlayerName ?? string.Empty);
            writer.Write(SpawnPosition.x);
            writer.Write(SpawnPosition.y);
            writer.Write(SpawnPosition.z);
            writer.Write(Health);
        }
        
        protected override void DeserializeData(BinaryReader reader)
        {
            PlayerId = reader.ReadInt32();
            PlayerName = reader.ReadString();
            SpawnPosition = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
            Health = reader.ReadSingle();
        }
    }
}