using Arena.Core;
using UnityEngine;
using Arena.Logging;
using Arena.Core.DependencyInjection;

namespace Arena.Combat
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    public class Bullet : MonoBehaviour
    {
        [Header("Bullet Settings")]
        [SerializeField] private float speed = 20f;
        [SerializeField] private float lifetime = 3f;
        [SerializeField] private float damage = 25f;
        
        [Header("Components")]
        [SerializeField] private Rigidbody rb;
        [SerializeField] private SphereCollider col;
        [SerializeField] private TrailRenderer trail;
        
        private uint _networkId = 0;
        private int _ownerPlayerId = -1;
        private float _spawnTime;
        private bool _hasHit = false;
        private bool _isNetworkControlled = false;
        private IGameLogger _logger;

        public uint NetworkId => _networkId;

        private void Awake()
        {
            if (rb == null)
                rb = GetComponent<Rigidbody>();
            
            if (col == null)
                col = GetComponent<SphereCollider>();
            
            rb.useGravity = false;
            rb.isKinematic = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            
            col.isTrigger = true;
            col.radius = GameConstants.Physics.BulletRadius;
            
            var container = GameInstaller.Container;
            if (container != null && container.IsRegistered<IGameLogger>())
            {
                _logger = container.Resolve<IGameLogger>();
            }
        }

        private void Update()
        {
            if (_isNetworkControlled)
            {
                return;
            }
            
            if (Time.time - _spawnTime > lifetime)
            {
                ReturnToPool();
            }
        }
        
        public void FireFromNetwork(uint networkId, Vector3 direction, int ownerPlayerId, float bulletSpeed, float bulletDamage)
        {
            _networkId = networkId;
            _ownerPlayerId = ownerPlayerId;
            _spawnTime = Time.time;
            _hasHit = false;
            _isNetworkControlled = true;
            
            damage = bulletDamage;
            speed = bulletSpeed;
            
            direction.y = 0;
            direction.Normalize();
            
            transform.forward = direction;
            rb.linearVelocity = direction * speed;
            
            if (trail != null)
            {
                trail.Clear();
                trail.emitting = true;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_hasHit) return;
            
            if (_isNetworkControlled)
            {
                return;
            }
            
            var hitPlayer = other.GetComponent<Arena.Player.Player>();
            
            if (hitPlayer != null && hitPlayer.PlayerId == _ownerPlayerId)
                return;
            
            if (hitPlayer != null && hitPlayer.PlayerId != _ownerPlayerId)
            {
                _hasHit = true;
                _logger?.Log(LogLevel.Debug, "Bullet", "Local prediction hit Player {0}", hitPlayer.PlayerId);
                ReturnToPool();
                return;
            }
            
            if (other.CompareTag("Wall") || other.CompareTag("Obstacle") || other.CompareTag("Default"))
            {
                _hasHit = true;
                _logger?.Log(LogLevel.Debug, "Bullet", "Local prediction hit wall");
                ReturnToPool();
            }
        }

        public void DestroyFromNetwork()
        {
            ReturnToPool();
        }

        private void ReturnToPool()
        {
            if (trail != null)
            {
                trail.emitting = false;
            }
            
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            
            gameObject.SetActive(false);
            Destroy(gameObject, 0.1f);
        }
    }
}