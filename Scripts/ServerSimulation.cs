using UnityEngine;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Logging;

namespace Arena.Gameplay
{
    public class ServerSimulation : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private NetworkConfig networkConfig;

        private INetworkService _networkService;
        private IGameLogger _logger;
        
        private readonly ConcurrentDictionary<int, PlayerState> _playerStates = new();
        private readonly Dictionary<uint, ServerBullet> _activeBullets = new();
        private float _stateUpdateTimer;
        
        private uint _nextBulletId = 1;
        private readonly object _bulletIdLock = new();

        private float _fixedTimestep;
        private float _accumulatedTime;

        private void Start()
        {
            if (networkConfig == null)
            {
                _logger?.Log(LogLevel.Error, "Server", "NetworkConfig is not assigned!");
                return;
            }
            
            _fixedTimestep = networkConfig.GetServerTickInterval();
            _accumulatedTime = 0f;
            
            _logger?.Log(LogLevel.Info, "Server", "Fixed Timestep: {0}s ({1}Hz)", 
                _fixedTimestep, networkConfig.ServerTickRateHz);
            
            InjectDependencies();
            SubscribeToEvents();
        }

        private void Update()
        {
            _accumulatedTime += Time.deltaTime;
            
            while (_accumulatedTime >= _fixedTimestep)
            {
                SimulatePhysics(_fixedTimestep);
                _accumulatedTime -= _fixedTimestep;
            }
            
            UpdateBullets();
            BroadcastPlayerStates();
        }

        private void SimulatePhysics(float deltaTime)
        {
            foreach (var state in _playerStates.Values)
            {
                if (!state.IsAlive) continue;
                
                state.Position = ClampToMapBounds(state.Position + state.Velocity * deltaTime);
            }
        }

        private void InjectDependencies()
        {
            var container = GameInstaller.Container;
            
            if (container == null)
            {
                Debug.LogError("[ServerSimulation] DIContainer not found!");
                return;
            }

            _networkService = container.Resolve<INetworkService>();
            _logger = container.Resolve<IGameLogger>();
        }

        private void SubscribeToEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.RegisterHandler(MessageType.PlayerInput, OnPlayerInputMessage);
                _networkService.MessageRouter.RegisterHandler(MessageType.Fire, OnFireMessage);
                _networkService.MessageRouter.RegisterHandler(MessageType.Handshake, OnHandshakeMessage);
        
                _networkService.OnClientConnected += OnClientConnected;
                _networkService.OnClientDisconnected += OnClientDisconnected;
            }
        }

        private void UnsubscribeFromEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.UnregisterHandler(MessageType.PlayerInput, OnPlayerInputMessage);
                _networkService.MessageRouter.UnregisterHandler(MessageType.Fire, OnFireMessage);
                _networkService.MessageRouter.UnregisterHandler(MessageType.Handshake, OnHandshakeMessage);
        
                _networkService.OnClientConnected -= OnClientConnected;
                _networkService.OnClientDisconnected -= OnClientDisconnected;
            }
        }

        private void OnPlayerInputMessage(INetworkMessage message, int senderId)
        {
            if (message is PlayerInputMessage inputMsg)
                ProcessPlayerInput(inputMsg, senderId);
        }

        private void OnFireMessage(INetworkMessage message, int senderId)
        {
            if (message is FireMessage fireMsg)
                ProcessFire(fireMsg, senderId);
        }

        private void OnHandshakeMessage(INetworkMessage message, int senderId)
        {
            if (message is HandshakeMessage handshake)
                HandleClientHandshake(handshake, senderId);
        }
        
        private void ProcessPlayerInput(PlayerInputMessage input, int senderId)
        {
            if (!_playerStates.TryGetValue(senderId, out var state))
                return;

            if (!state.IsAlive)
                return;

            if (state.HasProcessedInput && input.SeqNumber <= state.LastProcessedInput)
                return;

            Vector2 moveInput = Vector2.ClampMagnitude(input.MoveInput, 1f);
    
            Vector3 velocity = new Vector3(
                moveInput.x * networkConfig.PlayerMoveSpeed,
                0,
                moveInput.y * networkConfig.PlayerMoveSpeed
            );
    
            state.Velocity = new Vector3(
                velocity.x,
                state.Velocity.y,
                velocity.z
            );
    
            if (input.MouseWorldPosition != Vector3.zero)
            {
                Vector3 direction = input.MouseWorldPosition - state.Position;
                direction.y = 0;
        
                if (direction.sqrMagnitude > 0.01f)
                {
                    state.Rotation = Quaternion.LookRotation(direction);
                }
            }
    
            state.LastProcessedInput = input.SeqNumber;
            state.HasProcessedInput = true;
        }

        private Vector3 ClampToMapBounds(Vector3 position)
        {
            position.x = Mathf.Clamp(position.x, GameConstants.Map.MinBound, GameConstants.Map.MaxBound);
            position.z = Mathf.Clamp(position.z, GameConstants.Map.MinBound, GameConstants.Map.MaxBound);
            return position;
        }
        
        private void ProcessFire(FireMessage fire, int senderId)
        {
            var shooter = _playerStates.GetValueOrDefault(senderId);
            
            if (shooter == null || !shooter.IsAlive)
            {
                _logger?.Log(LogLevel.Warning, "Server", 
                    "Fire rejected: Player {0} invalid state", senderId);
                return;
            }
            
            uint bulletId = GetNextBulletId();
            
            var bullet = new ServerBullet
            {
                BulletId = bulletId,
                OwnerId = senderId,
                Position = fire.FirePosition,
                Direction = fire.FireDirection.normalized,
                Speed = fire.BulletSpeed,
                Damage = fire.Damage,
                SpawnTime = Time.time,
                Lifetime = networkConfig.BulletLifetime
            };
            
            _activeBullets.Add(bulletId, bullet);
            
            var spawnMsg = new BulletSpawnMessage
            {
                TargetId = -1,
                BulletId = bulletId,
                OwnerId = senderId,
                SpawnPosition = fire.FirePosition,
                Direction = fire.FireDirection.normalized,
                Speed = fire.BulletSpeed,
                Damage = fire.Damage
            };
            
            _networkService?.SendMessage(spawnMsg);
            
            _logger?.Log(LogLevel.Debug, "Server", 
                "Bullet {0} spawned by Player {1}", bulletId, senderId);
        }
        
        private void HandleClientHandshake(HandshakeMessage handshake, int senderId)
        {
            _logger?.Log(LogLevel.Info, "Server", 
                "Handshake from Client {0}: {1}", senderId, handshake.PlayerName);
            
            int sentCount = 0;
            foreach (var existingState in _playerStates.Values)
            {
                if (existingState.PlayerId == senderId)
                    continue;
                
                var existingPlayerMsg = new PlayerJoinedMessage
                {
                    TargetId = senderId,
                    PlayerId = existingState.PlayerId,
                    PlayerName = $"Player_{existingState.PlayerId}",
                    SpawnPosition = existingState.Position,
                    Health = existingState.Health
                };
                
                _networkService?.SendMessage(existingPlayerMsg);
                sentCount++;
            }
            
            if (sentCount > 0)
            {
                _logger?.Log(LogLevel.Info, "Server", 
                    "Sent {0} existing player(s) to Client {1}", sentCount, senderId);
            }
        }

        private void OnClientConnected(int clientId)
        {
            int playerId = clientId;
            Vector3 spawnPos = GameConstants.Map.SpawnPositions[playerId % GameConstants.Map.SpawnPositions.Length];
            
            var newState = new PlayerState
            {
                PlayerId = playerId,
                Position = spawnPos,
                Rotation = Quaternion.identity,
                Velocity = Vector3.zero,
                Health = networkConfig.PlayerMaxHealth,
                IsAlive = true,
                LastProcessedInput = 0
            };
            
            _playerStates.TryAdd(playerId, newState);
            
            var joinMsg = new PlayerJoinedMessage
            {
                TargetId = -1,
                PlayerId = playerId,
                PlayerName = $"Player_{playerId}",
                SpawnPosition = spawnPos,
                Health = networkConfig.PlayerMaxHealth
            };
            
            _networkService?.SendMessage(joinMsg);
            
            _logger?.Log(LogLevel.Info, "Server", 
                "Player {0} joined at {1}", playerId, spawnPos);
        }

        private void OnClientDisconnected(int clientId)
        {
            int playerId = clientId;

            if (_playerStates.TryRemove(playerId, out _))
            {
                var leftMsg = new PlayerLeftMessage
                {
                    TargetId = -1,
                    PlayerId = playerId
                };
                
                _networkService?.SendMessage(leftMsg);
                
                _logger?.Log(LogLevel.Info, "Server", "Player {0} left", playerId);
            }
        }

        private void UpdateBullets()
        {
            var bulletsToRemove = new List<uint>();
            
            foreach (var kvp in _activeBullets)
            {
                var bullet = kvp.Value;
                
                if (Time.time - bullet.SpawnTime > bullet.Lifetime)
                {
                    bulletsToRemove.Add(kvp.Key);
                    BroadcastBulletDestroy(kvp.Key, DestroyReason.Timeout);
                    continue;
                }
                
                Vector3 prevPosition = bullet.Position;
                bullet.Position += bullet.Direction * bullet.Speed * Time.deltaTime;

                float moveDistance = Vector3.Distance(prevPosition, bullet.Position);
                bool hitWall = Physics.Raycast(
                    prevPosition,
                    bullet.Direction,
                    out RaycastHit wallHit,
                    moveDistance,
                    LayerMask.GetMask("Obstacle"),
                    QueryTriggerInteraction.Ignore);

                float nearestHitDistance = hitWall ? wallHit.distance : moveDistance;
                PlayerState hitPlayer = null;

                foreach (var playerState in _playerStates.Values)
                {
                    if (playerState.PlayerId == bullet.OwnerId) continue;
                    if (!playerState.IsAlive) continue;

                    if (TryGetPlayerHitDistance(
                            playerState.Position,
                            prevPosition,
                            bullet.Position,
                            GameConstants.Physics.PlayerRadius,
                            out float playerHitDistance)
                        && playerHitDistance <= nearestHitDistance)
                    {
                        nearestHitDistance = playerHitDistance;
                        hitPlayer = playerState;
                    }
                }

                if (hitPlayer != null)
                {
                    hitPlayer.Health -= bullet.Damage;

                    _logger?.Log(LogLevel.Info, "Server", 
                        "Bullet {0} hit Player {1}. Damage: {2}, HP: {3}",
                        bullet.BulletId, hitPlayer.PlayerId, bullet.Damage, hitPlayer.Health);

                    if (hitPlayer.Health <= 0)
                    {
                        hitPlayer.IsAlive = false;
                        hitPlayer.Health = 0;
                        _logger?.Log(LogLevel.Info, "Server", 
                            "Player {0} killed Player {1}", bullet.OwnerId, hitPlayer.PlayerId);
                    }

                    bulletsToRemove.Add(kvp.Key);
                    BroadcastBulletDestroy(kvp.Key, DestroyReason.HitPlayer);
                    continue;
                }

                if (hitWall)
                {
                    bulletsToRemove.Add(kvp.Key);
                    BroadcastBulletDestroy(kvp.Key, DestroyReason.HitWall);
                    continue;
                }
                
                // 맵 경계
                if (Mathf.Abs(bullet.Position.x) > GameConstants.Map.MaxBound || 
                    Mathf.Abs(bullet.Position.z) > GameConstants.Map.MaxBound)
                {
                    bulletsToRemove.Add(kvp.Key);
                    BroadcastBulletDestroy(kvp.Key, DestroyReason.Timeout);
                }
                
            }
            
            foreach (var bulletId in bulletsToRemove)
            {
                _activeBullets.Remove(bulletId);
            }
        }

        private bool TryGetPlayerHitDistance(
            Vector3 point,
            Vector3 lineStart,
            Vector3 lineEnd,
            float radius,
            out float hitDistance)
        {
            Vector2 p = new Vector2(point.x, point.z);
            Vector2 a = new Vector2(lineStart.x, lineStart.z);
            Vector2 b = new Vector2(lineEnd.x, lineEnd.z);

            Vector2 ab = b - a;
            Vector2 ap = p - a;

            float segmentLength = ab.magnitude;
            if (segmentLength < 0.0001f)
            {
                hitDistance = 0f;
                return Vector2.Distance(p, a) <= radius;
            }

            Vector2 segmentDirection = ab / segmentLength;
            float projectedDistance = Vector2.Dot(ap, segmentDirection);
            Vector2 closest = a + segmentDirection * projectedDistance;
            float perpendicularDistance = Vector2.Distance(p, closest);

            if (perpendicularDistance > radius)
            {
                hitDistance = 0f;
                return false;
            }

            float entryOffset = Mathf.Sqrt(radius * radius - perpendicularDistance * perpendicularDistance);
            float entryDistance = projectedDistance - entryOffset;
            float exitDistance = projectedDistance + entryOffset;

            if (exitDistance < 0f || entryDistance > segmentLength)
            {
                hitDistance = 0f;
                return false;
            }

            hitDistance = Mathf.Max(0f, entryDistance);
            return true;
        }

        private void BroadcastBulletDestroy(uint bulletId, DestroyReason reason)
        {
            var destroyMsg = new BulletDestroyMessage
            {
                TargetId = -1,
                BulletId = bulletId,
                Reason = reason
            };
            
            _networkService?.SendMessage(destroyMsg);
            
            _logger?.Log(LogLevel.Debug, "Server", "Bullet {0} destroyed ({1})", bulletId, reason);
        }

        private void BroadcastPlayerStates()
        {
            _stateUpdateTimer += Time.deltaTime;
            
            float updateInterval = networkConfig.GetServerTickInterval();
            
            if (_stateUpdateTimer >= updateInterval)
            {
                foreach (var state in _playerStates.Values)
                {
                    var stateMsg = new PlayerStateMessage
                    {
                        PlayerId = state.PlayerId,
                        Position = state.Position,
                        Rotation = state.Rotation,
                        Velocity = state.Velocity,
                        Health = state.Health,
                        IsAlive = state.IsAlive,
                        LastProcessedInput = state.LastProcessedInput
                    };
                    
                    _networkService?.SendMessage(stateMsg);
                }
                
                _stateUpdateTimer -= updateInterval;

                if (_stateUpdateTimer > updateInterval)
                {
                    _stateUpdateTimer = 0f;
                }
            }
        }
        
        private uint GetNextBulletId()
        {
            lock (_bulletIdLock)
            {
                return _nextBulletId++;
            }
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromEvents();
        }
        
        private class PlayerState
        {
            public int PlayerId;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public float Health;
            public bool IsAlive;
            public uint LastProcessedInput;
            public bool HasProcessedInput;
        }

        private class ServerBullet
        {
            public uint BulletId;
            public int OwnerId;
            public Vector3 Position;
            public Vector3 Direction;
            public float Speed;
            public float Damage;
            public float SpawnTime;
            public float Lifetime;
        }
    }
}
