using UnityEngine;
using Arena.Network;
using Arena.Logging;

namespace Arena.Player.States
{
    public class RemotePlayerState : IPlayerState
    {
        private readonly Player _player;
        private readonly IGameLogger _logger;
        private readonly NetworkConfig _config;
        
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private Vector3 _serverVelocity;
        
        private Vector3 _predictedPosition;
        private float _timeSinceLastUpdate;
        
        private const float PREDICTION_TIME = 0.1f;
        
        public RemotePlayerState(Player player, IGameLogger logger, NetworkConfig config)
        {
            _player = player;
            _logger = logger;
            _config = config;
        }

        public void OnEnter()
        {
            _targetPosition = _player.transform.position;
            _targetRotation = _player.transform.rotation;
            _serverVelocity = Vector3.zero;
            _predictedPosition = _targetPosition;
            _timeSinceLastUpdate = 0f;
            
            if (_player.Rigidbody != null)
            {
                _player.Rigidbody.isKinematic = true;
            }
            
            _logger?.Log(LogLevel.Debug, "Player", "Remote player state activated for {0}", _player.PlayerId);
        }

        public void OnExit()
        {
            // Cleanup
        }

        public void Update()
        {
            _timeSinceLastUpdate += Time.deltaTime;
            UpdatePrediction();
        }

        public void FixedUpdate()
        {
            InterpolateToTarget();
        }

        public void SetTargetTransform(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            _targetPosition = position;
            _targetRotation = rotation;
            _serverVelocity = velocity;
            
            _timeSinceLastUpdate = 0f;
            
            float snapThreshold = _config != null ? _config.SnapThreshold : 5f;
            
            float distance = Vector3.Distance(_player.transform.position, position);
            if (distance > snapThreshold)
            {
                _logger?.Log(LogLevel.Debug, "Player", 
                    "Large position error ({0:F2}m) - snapping Player {1}", 
                    distance, _player.PlayerId);
                
                _player.transform.position = position;
                _predictedPosition = position;
            }
        }

        private void UpdatePrediction()
        {
            float predictionTime = Mathf.Min(_timeSinceLastUpdate, PREDICTION_TIME);
            _predictedPosition = _targetPosition + _serverVelocity * predictionTime;
        }

        private void InterpolateToTarget()
        {
            if (_player.Rigidbody == null) return;
            
            float interpolationSpeed = _config != null ? _config.InterpolationSpeed : 20f;
            
            Vector3 newPosition = Vector3.Lerp(
                _player.transform.position,
                _predictedPosition,
                interpolationSpeed * Time.fixedDeltaTime
            );
            
            _player.Rigidbody.MovePosition(newPosition);
            
            Quaternion newRotation = Quaternion.Slerp(
                _player.transform.rotation,
                _targetRotation,
                interpolationSpeed * Time.fixedDeltaTime
            );
            
            _player.Rigidbody.MoveRotation(newRotation);
        }
    }
}