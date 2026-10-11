using UnityEngine;
using TMPro;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Logging;

namespace Arena.Vision
{
    public class GameTimerUI : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private TextMeshProUGUI timerText;
        
        [Header("Colors")]
        [SerializeField] private Color dayColor = new Color(1f, 0.9f, 0.4f);
        [SerializeField] private Color nightColor = new Color(0.4f, 0.5f, 0.9f);
        
        private INetworkService _networkService;
        private IGameLogger _logger;
        private NetworkConfig _config;
        private OptionalFogOfWarBridge _fogWar;
        
        private float _remainingTime = 60f;
        private bool _isNight = false;
        
        private float _lastSyncTime;
        private float _syncedRemainingTime;
        
        public bool IsNight => _isNight;
        
        public event System.Action<bool> OnDayNightChanged;
        
        private void Start()
        {
            InjectDependencies();
            OptionalFogOfWarBridge.TryCreate(out _fogWar);
            UpdateFogSettings();
        }
        
        private void InjectDependencies()
        {
            var container = GameInstaller.Container;
            if (container == null) return;
            
            _networkService = container.Resolve<INetworkService>();
            _logger = container.Resolve<IGameLogger>();
            _config = container.Resolve<NetworkConfig>();
            
            if (_networkService != null && !_networkService.IsServer)
            {
                _networkService.MessageRouter.RegisterHandler(MessageType.GameTime, OnGameTimeMessage);
            }
        }
        
        private void OnGameTimeMessage(INetworkMessage message, int senderId)
        {
            if (message is GameTimeMessage timeMsg)
            {
                bool wasNight = _isNight;
                
                _syncedRemainingTime = timeMsg.RemainingTime;
                _remainingTime = _syncedRemainingTime;
                _isNight = timeMsg.IsNight;
                _lastSyncTime = Time.time;
                
                if (wasNight != _isNight)
                {
                    _logger?.Log(LogLevel.Info, "GameTimerUI", "{0}", _isNight ? "NIGHT" : "DAY");
                    OnDayNightChanged?.Invoke(_isNight);
                    UpdateFogSettings();
                }
            }
        }
        
        private void Update()
        {
            if (_networkService == null || _networkService.IsServer) return;
            
            float elapsed = Time.time - _lastSyncTime;
            _remainingTime = Mathf.Max(0f, _syncedRemainingTime - elapsed);
            
            UpdateDisplay();
        }
        
        private void UpdateDisplay()
        {
            if (timerText == null) return;
            
            int minutes = Mathf.FloorToInt(_remainingTime / 60f);
            int seconds = Mathf.FloorToInt(_remainingTime % 60f);
            
            string phase = _isNight ? "NIGHT" : "DAY";
            timerText.text = $"{phase} {minutes}:{seconds:D2}";
            timerText.color = _isNight ? nightColor : dayColor;
        }
        
        private void UpdateFogSettings()
        {
            if (_fogWar == null || _config == null) return;

            float alpha = _isNight ? _config.NightFogOpacity : _config.DayFogOpacity;
            _fogWar.TrySetFogPlaneAlpha(alpha);
        }
        
        private void OnDestroy()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.UnregisterHandler(MessageType.GameTime, OnGameTimeMessage);
            }
        }
    }
}
