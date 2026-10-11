using UnityEngine;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Logging;

namespace Arena.Vision
{
    public class GameTimeManager : MonoBehaviour
    {
        private INetworkService _networkService;
        private IGameLogger _logger;
        private NetworkConfig _config;
        
        private float _remainingTime;
        private bool _isNight = false;
        
        private float _broadcastTimer;
        private const float BROADCAST_INTERVAL = 1f;
        
        [Header("Debug")]
        [SerializeField] private bool forceNight = false;
        public bool IsNight => forceNight || _isNight;
        
        private void Start()
        {
            InjectDependencies();
            
            float cycleDuration = _config != null ? _config.CycleDuration : 60f;
            _remainingTime = cycleDuration;
            
            if (_networkService != null && _networkService.IsServer)
            {
                _logger?.Log(LogLevel.Info, "GameTime", "Started - Cycle: {0}s", cycleDuration);
            }
        }
        
        private void InjectDependencies()
        {
            var container = GameInstaller.Container;
            if (container == null) return;
            
            _networkService = container.Resolve<INetworkService>();
            _logger = container.Resolve<IGameLogger>();
            _config = container.Resolve<NetworkConfig>();
        }
        
        private void Update()
        {
            if (_networkService == null || !_networkService.IsServer) return;
            
            _remainingTime -= Time.deltaTime;
            
            if (_remainingTime <= 0f)
            {
                ToggleDayNight();
                _remainingTime = _config != null ? _config.CycleDuration : 60f;
            }
            
            BroadcastTime();
        }
        
        private void ToggleDayNight()
        {
            _isNight = !_isNight;
            
            _logger?.Log(LogLevel.Info, "GameTime", "=== {0} ===", _isNight ? "NIGHT" : "DAY");
            
            BroadcastTimeImmediate();
        }
        
        private void BroadcastTime()
        {
            _broadcastTimer += Time.deltaTime;
            
            if (_broadcastTimer >= BROADCAST_INTERVAL)
            {
                BroadcastTimeImmediate();
                _broadcastTimer = 0f;
            }
        }
        
        private void BroadcastTimeImmediate()
        {
            if (_networkService == null || !_networkService.IsConnected) return;
            
            var timeMsg = new GameTimeMessage
            {
                TargetId = -1,
                RemainingTime = _remainingTime,
                IsNight = IsNight,
                CycleDuration = _config != null ? _config.CycleDuration : 60f
            };
            
            _networkService.SendMessage(timeMsg);
        }
    }
}