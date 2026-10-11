using System.Collections.Generic;
using UnityEngine;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Logging;
using Arena.Combat;

namespace Arena.Gameplay
{
    public class ClientBulletManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GameObject bulletPrefab;
        
        private INetworkService _networkService;
        private IGameLogger _logger;
        private NetworkConfig _networkConfig;
        private readonly HashSet<uint> _processedDestroys = new HashSet<uint>();
        private readonly Queue<uint> _processedDestroyOrder = new Queue<uint>();
        private readonly Dictionary<uint, GameObject> _networkBullets = new();
        private readonly Dictionary<uint, float> _bulletExpireAt = new();
        private readonly List<uint> _expiredBulletIds = new();
        private bool _isInitialized = false;
        private bool _wasConnected;

        private const int MaxProcessedDestroyIds = 2048;
        private const float DefaultBulletLifetimeSeconds = 3f;

        private void Start()
        {
            InjectDependencies();
        }

        private void Update()
        {
            ObserveConnectionBoundary();

            if (!_isInitialized && _networkService != null)
            {
                if (_networkService.IsServer)
                {
                    _logger?.Log(LogLevel.Debug, "Bullet", "Server detected - destroying ClientBulletManager");
                    Destroy(gameObject);
                    return;
                }
                
                if (_networkService.IsConnected)
                {
                    Initialize();
                }
            }

            if (_isInitialized)
            {
                ExpireStaleBullets();
            }
        }

        private void Initialize()
        {
            _isInitialized = true;
            
            SubscribeToNetworkEvents();
            
            _logger?.Log(LogLevel.Info, "Bullet", "ClientBulletManager initialized");
        }

        private void InjectDependencies()
        {
            var container = GameInstaller.Container;
            
            if (container == null)
            {
                Debug.LogError("[ClientBulletManager] DIContainer not found!");
                return;
            }

            _networkService = container.Resolve<INetworkService>();
            _logger = container.Resolve<IGameLogger>();
            _networkConfig = container.Resolve<NetworkConfig>();
        }

        private void SubscribeToNetworkEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.RegisterHandler(MessageType.BulletSpawn, OnBulletSpawnMessage);
                _networkService.MessageRouter.RegisterHandler(MessageType.BulletDestroy, OnBulletDestroyMessage);
                _networkService.OnClientDisconnected += OnClientDisconnected;
                _networkService.OnConnectionFailed += OnConnectionFailed;
            }
        }

        private void UnsubscribeFromNetworkEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.UnregisterHandler(MessageType.BulletSpawn, OnBulletSpawnMessage);
                _networkService.MessageRouter.UnregisterHandler(MessageType.BulletDestroy, OnBulletDestroyMessage);
                _networkService.OnClientDisconnected -= OnClientDisconnected;
                _networkService.OnConnectionFailed -= OnConnectionFailed;
            }
        }

        private void ObserveConnectionBoundary()
        {
            if (_networkService == null)
                return;

            bool isConnected = _networkService.IsConnected;
            if (isConnected == _wasConnected)
                return;

            ClearSessionState();
            _wasConnected = isConnected;
        }

        private void OnClientDisconnected(int clientId)
        {
            ClearSessionState();
            _wasConnected = false;
        }

        private void OnConnectionFailed(string reason)
        {
            ClearSessionState();
            _wasConnected = false;
        }

        private void OnBulletSpawnMessage(INetworkMessage message, int senderId)
        {
            ObserveConnectionBoundary();
            if (!_isInitialized || _networkService?.IsConnected != true) return;
            
            if (message is BulletSpawnMessage spawnMsg)
                HandleBulletSpawn(spawnMsg);
        }

        private void OnBulletDestroyMessage(INetworkMessage message, int senderId)
        {
            ObserveConnectionBoundary();
            if (!_isInitialized || _networkService?.IsConnected != true) return;
            
            if (message is BulletDestroyMessage destroyMsg)
                HandleBulletDestroy(destroyMsg);
        }

        private void HandleBulletSpawn(BulletSpawnMessage spawnMsg)
        {
            _logger?.Log(LogLevel.Debug, "Bullet", "Received bullet {0} from Player {1}", 
                spawnMsg.BulletId, spawnMsg.OwnerId);
            
            if (bulletPrefab == null)
            {
                _logger?.Log(LogLevel.Error, "Bullet", "Bullet prefab not assigned!");
                return;
            }

            if (_processedDestroys.Contains(spawnMsg.BulletId))
            {
                _logger?.Log(LogLevel.Debug, "Bullet",
                    "Ignoring late spawn for destroyed bullet {0}", spawnMsg.BulletId);
                return;
            }

            if (_networkBullets.ContainsKey(spawnMsg.BulletId))
            {
                _logger?.Log(LogLevel.Warning, "Bullet", "Bullet {0} already exists", spawnMsg.BulletId);
                return;
            }

            GameObject bulletObj = Instantiate(bulletPrefab, spawnMsg.SpawnPosition, Quaternion.identity);
            var bullet = bulletObj.GetComponent<Bullet>();

            if (bullet != null)
            {
                bullet.FireFromNetwork(
                    spawnMsg.BulletId,
                    spawnMsg.Direction,
                    spawnMsg.OwnerId,
                    spawnMsg.Speed,
                    spawnMsg.Damage
                );

                _networkBullets.Add(spawnMsg.BulletId, bulletObj);
                float maxLifetime = _networkConfig != null
                    ? _networkConfig.BulletLifetime
                    : DefaultBulletLifetimeSeconds;
                _bulletExpireAt[spawnMsg.BulletId] = Time.time + maxLifetime;

                _logger?.Log(LogLevel.Debug, "Bullet", "Spawned bullet {0}, total: {1}", 
                    spawnMsg.BulletId, _networkBullets.Count);
            }
            else
            {
                _logger?.Log(LogLevel.Error, "Bullet", "Bullet component not found!");
                Destroy(bulletObj);
            }
        }

        private void HandleBulletDestroy(BulletDestroyMessage destroyMsg)
        {
            uint bulletId = destroyMsg.BulletId;

            if (!TryMarkDestroyed(bulletId))
            {
                _logger?.Log(LogLevel.Debug, "Bullet", "Bullet {0} already destroyed", bulletId);
                return;
            }

            if (!RemoveBulletObject(bulletId))
            {
                _logger?.Log(LogLevel.Warning, "Bullet",
                    "Cannot destroy bullet {0} - not found", bulletId);
                return;
            }

            _logger?.Log(LogLevel.Debug, "Bullet", "Destroyed bullet {0}, remaining: {1}", 
                bulletId, _networkBullets.Count);
        }

        private void ExpireStaleBullets()
        {
            if (_bulletExpireAt.Count == 0)
                return;

            _expiredBulletIds.Clear();

            foreach (var pair in _bulletExpireAt)
            {
                if (Time.time >= pair.Value)
                {
                    _expiredBulletIds.Add(pair.Key);
                }
            }

            foreach (uint bulletId in _expiredBulletIds)
            {
                if (!TryMarkDestroyed(bulletId))
                    continue;

                RemoveBulletObject(bulletId);
                _logger?.Log(LogLevel.Debug, "Bullet",
                    "Bullet {0} reached the client-side lifetime fallback " +
                    "before an authoritative destroy was observed", bulletId);
            }
        }

        private bool RemoveBulletObject(uint bulletId)
        {
            _bulletExpireAt.Remove(bulletId);

            if (!_networkBullets.TryGetValue(bulletId, out var bulletObj))
                return false;

            _networkBullets.Remove(bulletId);

            if (bulletObj != null)
            {
                var bullet = bulletObj.GetComponent<Bullet>();
                bullet?.DestroyFromNetwork();
            }

            return true;
        }

        private bool TryMarkDestroyed(uint bulletId)
        {
            if (!_processedDestroys.Add(bulletId))
                return false;

            _processedDestroyOrder.Enqueue(bulletId);

            while (_processedDestroyOrder.Count > MaxProcessedDestroyIds)
            {
                uint oldestId = _processedDestroyOrder.Dequeue();
                _processedDestroys.Remove(oldestId);
            }

            return true;
        }

        private void ClearSessionState()
        {
            foreach (var bulletObj in _networkBullets.Values)
            {
                if (bulletObj != null)
                    Destroy(bulletObj);
            }

            _networkBullets.Clear();
            _bulletExpireAt.Clear();
            _expiredBulletIds.Clear();
            _processedDestroys.Clear();
            _processedDestroyOrder.Clear();
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromNetworkEvents();
            ClearSessionState();
        }
    }
}
