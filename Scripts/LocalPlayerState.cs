using UnityEngine;
using System.Collections.Generic;
using Arena.Core;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Logging;
using Arena.Input;
using Arena.Player.Prediction;

namespace Arena.Player.States
{
    public class LocalPlayerState : IPlayerState
    {
        private readonly Player _player;
        private readonly INetworkService _networkService;
        private readonly IGameLogger _logger;
        private readonly NetworkConfig _config;
        
        private PlayerInputHandler _inputHandler;
        private Vector2 _moveInput;
        private Vector3 _mouseWorldPosition;
        private uint _inputSeq = 1;

        private const int MAX_PENDING_INPUTS = 60;
        private readonly ClientPredictionHistory _predictionHistory = new(MAX_PENDING_INPUTS);

        private float _lastReconciliationTime;
        private float _lastInputSendTime;

        public LocalPlayerState(Player player, INetworkService networkService, IGameLogger logger, NetworkConfig config)
        {
            _player = player;
            _networkService = networkService;
            _logger = logger;
            _config = config;
        }

        public void OnEnter()
        {
            _logger?.Log(LogLevel.Debug, "Player", 
                "Local player state activated for {0}", _player.PlayerId);
    
            if (_player.Rigidbody != null)
            {
                _player.Rigidbody.isKinematic = true;
            }
    
            _lastReconciliationTime = Time.time;
            _lastInputSendTime = Time.time;
            _predictionHistory.Clear();
    
            SetupInputHandler();
            SetupCamera();
        }

        public void Update()
        {
            HandleInput();
            RotateTowardsMouse();
        }

        public void FixedUpdate()
        {
            ApplyMovement();
        }

        public void OnServerStateReceived(PlayerStateMessage serverState)
        {
            if (!_predictionHistory.TryAcceptSnapshot(serverState.TransportSeqNumber))
            {
                _logger?.Log(LogLevel.Trace, "Player",
                    "Ignoring stale player snapshot packet seq={0}",
                    serverState.TransportSeqNumber);
                return;
            }

            if (!_predictionHistory.Acknowledge(serverState.LastProcessedInput))
            {
                _logger?.Log(LogLevel.Trace, "Player",
                    "Ignoring stale player snapshot ack={0}", serverState.LastProcessedInput);
                return;
            }

            if (_predictionHistory.HistoryOverflowed)
            {
                _logger?.Log(LogLevel.Warning, "Player",
                    "Prediction history overflowed - snapping to authoritative state");

                _player.transform.position = serverState.Position;
                _player.transform.rotation = serverState.Rotation;
                _predictionHistory.Clear();
                _lastReconciliationTime = Time.time;
                return;
            }

            Vector3 correctedTarget = serverState.Position + _predictionHistory.GetReplayOffset();
            float positionError = Vector3.Distance(_player.transform.position, correctedTarget);
            
            float reconciliationThreshold = _config != null ? _config.ReconciliationThreshold : 0.3f;
            float hardThreshold = _config != null ? _config.HardReconciliationThreshold : 1.0f;
            float reconciliationInterval = _config != null ? _config.ReconciliationInterval : 0.1f;
            
            if (positionError < reconciliationThreshold)
            {
                return;
            }
            
            float timeSinceLastReconciliation = Time.time - _lastReconciliationTime;
            if (timeSinceLastReconciliation < reconciliationInterval && positionError < hardThreshold)
            {
                return;
            }
            
            _lastReconciliationTime = Time.time;
            
            if (positionError > hardThreshold)
            {
                _logger?.Log(LogLevel.Debug, "Player",
                    "Desync {0:F2}m - reconciling", positionError);
                 
                _player.transform.position = correctedTarget;
                _player.transform.rotation = serverState.Rotation;
            }
            else if (positionError > reconciliationThreshold)
            {
                _player.transform.position = Vector3.Lerp(
                    _player.transform.position,
                    correctedTarget,
                    0.15f
                );
            }
        }

        private void HandleInput()
        {
            if (_inputHandler == null) return;
            
            _moveInput = _inputHandler.MoveInput;
            _mouseWorldPosition = _inputHandler.MouseWorldPosition;
            
            float inputSendInterval = _config != null ? _config.GetInputSendInterval() : 0.05f;
            
            if (Time.time - _lastInputSendTime >= inputSendInterval)
            {
                SendInputToServer();
                _lastInputSendTime = Time.time;
            }
        }

        private void RotateTowardsMouse()
        {
            if (_mouseWorldPosition == Vector3.zero) return;
            
            Vector3 direction = _mouseWorldPosition - _player.transform.position;
            direction.y = 0;
            
            if (direction.sqrMagnitude > 0.01f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(direction);
                _player.transform.rotation = Quaternion.RotateTowards(
                    _player.transform.rotation,
                    targetRotation,
                    _player.RotationSpeed * Time.deltaTime
                );
            }
        }

        private void ApplyMovement()
        {
            Vector3 positionBeforePrediction = _player.transform.position;
            Vector2 clampedMoveInput = Vector2.ClampMagnitude(_moveInput, 1f);
            Vector3 velocity = new Vector3(clampedMoveInput.x, 0, clampedMoveInput.y) * _player.MoveSpeed;
            Vector3 movement = velocity * Time.fixedDeltaTime;
            _player.transform.position += movement;

            Vector3 boundedPosition = _player.transform.position;
            boundedPosition.x = Mathf.Clamp(
                boundedPosition.x,
                GameConstants.Map.MinBound,
                GameConstants.Map.MaxBound);
            boundedPosition.z = Mathf.Clamp(
                boundedPosition.z,
                GameConstants.Map.MinBound,
                GameConstants.Map.MaxBound);
            _player.transform.position = boundedPosition;
            
            if (Physics.Raycast(_player.transform.position, Vector3.down, out RaycastHit hit, GameConstants.Physics.GroundRayDistance))
            {
                _player.transform.position = new Vector3(
                    _player.transform.position.x,
                    hit.point.y,
                    _player.transform.position.z
                );
            }

            Vector3 predictedDisplacement = _player.transform.position - positionBeforePrediction;
            predictedDisplacement.y = 0f;

            if (_networkService?.IsConnected == true)
                _predictionHistory.AccumulatePredictedDisplacement(predictedDisplacement);
            else
                _predictionHistory.Clear();
        }

        private void SendInputToServer()
        {
            if (_networkService == null || !_networkService.IsConnected)
                return;

            uint currentSeq = _inputSeq++;
    
            var inputMsg = new PlayerInputMessage
            {
                MoveInput = _moveInput,
                MouseWorldPosition = _mouseWorldPosition,
                AttackPressed = _inputHandler.FireHeld,
                SeqNumber = currentSeq
            };
             
            _networkService.SendMessage(inputMsg);
            _predictionHistory.RecordSentInput(currentSeq);
        }

        private void SetupInputHandler()
        {
            _inputHandler = _player.GetComponent<PlayerInputHandler>();
            if (_inputHandler == null)
            {
                _inputHandler = _player.gameObject.AddComponent<PlayerInputHandler>();
            }
            
            int groundLayer = LayerMask.GetMask("Default", "Ground");
            if (groundLayer == 0) groundLayer = ~0;
            _inputHandler.SetGroundLayer(groundLayer);
            
            _inputHandler.EnableInput();
            _inputHandler.OnFirePerformed += OnFireInput;
        }

        private void OnFireInput()
        {
            // Fire handled in Player.cs
        }
        
        public void OnExit()
        {
            if (_inputHandler != null)
            {
                _inputHandler.DisableInput();
                _inputHandler.OnFirePerformed -= OnFireInput;
            }
            
            _predictionHistory.Clear();
        }
        
        private void SetupCamera()
        {
            if (Camera.main != null)
            {
                var cameraFollow = Camera.main.GetComponent<Arena.Gameplay.CameraFollow>();
                if (cameraFollow != null)
                {
                    cameraFollow.SetTarget(_player.transform);
                }
            }
        }
    }
}
