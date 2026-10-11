using UnityEngine;

namespace Arena.Core
{
    public static class GameConstants
    {
        public static class Protocol
        {
            public const int MaxPacketSize = 1400; // MTU
            public const int PacketHeaderSize = 23;
            public const int ChecksumSize = 4;
            public const int MaxPayloadSize = MaxPacketSize - PacketHeaderSize - ChecksumSize;
            public const int BufferSize = 4096;
            public const uint MagicNumber = 0x41524E41; // "ARNA"
        public const uint AckMagicNumber = 0x41434B00;
            public const byte ProtocolVersion = 1;
        }

 
        public static class Map
        {
            public const float MinBound = -50f;
            public const float MaxBound = 50f;
            
            public static readonly Vector3[] SpawnPositions = 
            {
                new Vector3(-10f, 0f, 0f),
                new Vector3(10f, 0f, 0f),
            };
        }

        public static class Physics
        {
            public const float PlayerRadius = 0.5f;
            public const float BulletRadius = 0.1f;
            public const float GroundRayDistance = 2f;
        }
    }
}
