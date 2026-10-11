using UnityEngine;

namespace Arena.Network
{
    [CreateAssetMenu(fileName = "NetworkConfig", menuName = "Arena/Config/Network Config")]
    public class NetworkConfig : ScriptableObject
    {
        [Header("Server")]
        public string DefaultServerIP = "127.0.0.1";
        public int DefaultPort = 7777;
        
        [Header("Network Performance")]
        public int ServerTickRateHz = 20;
        public int ClientTickRateHz = 20;
        public int MaxMessagesPerFrame = 10;
        public float ConnectionTimeoutSeconds = 5f;
        
        [Header("Reliability")]
        public int MaxRetries = 5;
        public float RetryTimeoutSeconds = 0.2f;
        
        
        [Header("Player Stat")]
        public float PlayerMoveSpeed = 7f;
        public float PlayerRotationSpeed = 720f;
        public float PlayerMaxHealth = 100f;
        
        [Header("Combat Stat")]
        public float AttackDamage = 25f;
        public float BulletSpeed = 20f;
        public float BulletLifetime = 3f;
        public float FireRate = 0.5f;
        
        [Header("Client Interpolation")]
        public float InterpolationSpeed = 20f;
        public float SnapThreshold = 5f;
        public float ReconciliationThreshold = 0.3f;
        public float HardReconciliationThreshold = 1.0f;
        public float ReconciliationInterval = 0.1f;
        
        [Header("Vision")]
        public float DayCircleVisionRange = 5f;
        public float NightCircleVisionRange = 3f;
        public float DayConeVisionRange = 15f;
        public float NightConeVisionRange = 8f;
        [Range(30f, 180f)]
        public float DayVisionAngle = 75f;
        [Range(30f, 180f)]
        public float NightVisionAngle = 45f;
        
        [Header("Fog")]
        [Range(0f, 1f)]
        public float DayFogOpacity = 0.3f;
        
        [Range(0f, 1f)]
        public float NightFogOpacity = 0.85f;
        
        [Header("Time Cycle")]
        public float CycleDuration = 60f;

        private void OnValidate()
        {
            ServerTickRateHz = Mathf.Clamp(ServerTickRateHz, 10, 120);
            ClientTickRateHz = Mathf.Clamp(ClientTickRateHz, 10, 120);
            MaxMessagesPerFrame = Mathf.Clamp(MaxMessagesPerFrame, 1, 100);
            MaxRetries = Mathf.Clamp(MaxRetries, 1, 10);
            
            ConnectionTimeoutSeconds = Mathf.Max(ConnectionTimeoutSeconds, 1f);
            RetryTimeoutSeconds = Mathf.Clamp(RetryTimeoutSeconds, 0.05f, 1f);
            
            PlayerMoveSpeed = Mathf.Clamp(PlayerMoveSpeed, 1f, 20f);
            PlayerRotationSpeed = Mathf.Clamp(PlayerRotationSpeed, 90f, 1440f);
            PlayerMaxHealth = Mathf.Clamp(PlayerMaxHealth, 1f, 1000f);
            
            AttackDamage = Mathf.Clamp(AttackDamage, 1f, 200f);
            BulletSpeed = Mathf.Clamp(BulletSpeed, 5f, 100f);
            BulletLifetime = Mathf.Clamp(BulletLifetime, 0.5f, 10f);
            FireRate = Mathf.Clamp(FireRate, 0.05f, 5f);
            
            InterpolationSpeed = Mathf.Clamp(InterpolationSpeed, 1f, 50f);
            SnapThreshold = Mathf.Clamp(SnapThreshold, 1f, 20f);
            
            DayCircleVisionRange = Mathf.Clamp(DayCircleVisionRange, 1f, 15f);
            NightCircleVisionRange = Mathf.Clamp(NightCircleVisionRange, 1f, 10f);
            DayConeVisionRange = Mathf.Clamp(DayConeVisionRange, 1f, 50f);
            NightConeVisionRange = Mathf.Clamp(NightConeVisionRange, 1f, 30f);
            
            CycleDuration = Mathf.Max(CycleDuration, 10f);
        }
        
        public float GetBaseVisionRange(bool isNight) => isNight ? NightCircleVisionRange : DayCircleVisionRange;
        public float GetConeVisionRange(bool isNight) => isNight ? NightConeVisionRange : DayConeVisionRange;
        public float GetConeVisionAngle(bool isNight) => isNight ? NightVisionAngle : DayVisionAngle;
        public float GetFogOpacity(bool isNight) => isNight ? NightFogOpacity : DayFogOpacity;
        public float GetInputSendInterval() => 1f / ClientTickRateHz;
        public float GetServerTickInterval() => 1f / ServerTickRateHz;
    }
}
