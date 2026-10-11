namespace Arena.Core
{
    public enum MessageType : byte
    {
        None = 0,
        Handshake = 1,
        PlayerInput = 2,
        PlayerState = 3,
        VisionData = 4,
        PlayerDeath = 5,
        Heartbeat = 6,
        Fire = 7,              // Client -> Server: 발사 요청
        BulletSpawn = 8,       // Server -> Client: 총알 생성 명령
        BulletDestroy = 9,     // Server -> Client: 총알 제거 명령
        Respawn = 10,
        PlayerJoined = 11,  
        PlayerLeft = 12,
        GameTime = 13
    }
}   