using UnityEngine;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Logging;
using Arena.Player.States;
using Arena.Input;
using Arena.Vision;

namespace Arena.Player
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public class Player : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private NetworkConfig networkConfig;
        
        [Header("Identity")]
        [SerializeField] private int playerId = -1;
        [SerializeField] private bool isLocalPlayer = false;
        
        [Header("Combat")]
        [SerializeField] private GameObject bulletPrefab;
        [SerializeField] private Transform firePoint;
        
        [Header("Components")]
        [SerializeField] private Rigidbody rb;
        [SerializeField] private MeshRenderer meshRenderer;

        public int PlayerId => playerId;
        public bool IsLocalPlayer => isLocalPlayer;
        public float MoveSpeed => networkConfig != null ? networkConfig.PlayerMoveSpeed : 7f;
        public float RotationSpeed => networkConfig != null ? networkConfig.PlayerRotationSpeed : 720f;
        public Rigidbody Rigidbody => rb;
        public float Health { get; private set; }
        public bool IsAlive { get; private set; }
        public NetworkConfig Config => networkConfig;

        private INetworkService _networkService;
        private IGameLogger _logger;
        private bool _hasAcceptedSnapshot;
        private uint _lastAcceptedSnapshotSeq;
        
        // State Machine
        private IPlayerState _currentState;
        private LocalPlayerState _localState;
        private RemotePlayerState _remoteState;
        private DeadPlayerState _deadState;
        
        private PlayerInputHandler _inputHandler;
        
        private float _lastFireTime = float.NegativeInfinity;
        private Material _playerMaterial;
        private Color _originalColor;
        
        // FogWar
        private OptionalFogOfWarBridge _fogWar;
        private int _fogRevealerIndex = -1;
        private bool _fogUnavailableLogged;
        private Camera _mainCamera;
        
        // Day/Night
        private GameTimerUI _gameTimerUI;
        private bool _currentIsNight = false;

        private void Awake()
        {
            if (networkConfig == null)
            {
                Debug.LogError("[Player] NetworkConfig is not assigned!");
            }
            
            InitializeComponents();
            InjectDependencies();
            _mainCamera = Camera.main;
        }
        
        private void Start()
        {
            InitializeStates();
            SubscribeToNetworkEvents();
            SetupVisuals();
            
            Health = networkConfig != null ? networkConfig.PlayerMaxHealth : 100f;
            IsAlive = true;
            
            _gameTimerUI = FindAnyObjectByType<GameTimerUI>();
            if (_gameTimerUI != null)
            {
                _gameTimerUI.OnDayNightChanged += OnDayNightChanged;
                _currentIsNight = _gameTimerUI.IsNight;
            }
            
            _logger?.Log(LogLevel.Info, "Player", "Player {0} initialized - IsLocal: {1}", playerId, isLocalPlayer);
        }
        
        private void Update()
        {
            _currentState?.Update();
            
            if (!IsAlive) return;
            
            if (isLocalPlayer)
            {
                UpdateFogConeDirection();
            }
            
            if (isLocalPlayer && _inputHandler != null && _inputHandler.ConsumeFirePressed() && CanFire())
            {
                Fire();
            }
        }
        
        private void FixedUpdate()
        {
            _currentState?.FixedUpdate();
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromNetworkEvents();
            UnregisterFromVisionReceiver();
            UnregisterFromFogWar();
            
            if (_gameTimerUI != null)
            {
                _gameTimerUI.OnDayNightChanged -= OnDayNightChanged;
            }
        }

        private void InitializeComponents()
        {
            if (rb == null)
                rb = GetComponent<Rigidbody>();
            
            if (meshRenderer == null)
                meshRenderer = GetComponentInChildren<MeshRenderer>();
            
            ConfigureRigidbody();
        }

        private void InjectDependencies()
        {
            var container = GameInstaller.Container;
            
            if (container == null)
            {
                Debug.LogError("[Player] DIContainer not found!");
                return;
            }

            _networkService = container.Resolve<INetworkService>();
            _logger = container.Resolve<IGameLogger>();
        }

        private void InitializeStates()
        {
            _localState = new LocalPlayerState(this, _networkService, _logger, networkConfig);
            _remoteState = new RemotePlayerState(this, _logger, networkConfig);
            _deadState = new DeadPlayerState(this, _logger);
            
            _currentState = isLocalPlayer ? (IPlayerState)_localState : _remoteState;
            _currentState.OnEnter();
            
            if (isLocalPlayer)
            {
                _inputHandler = GetComponent<PlayerInputHandler>();
            }
        }

        private void ConfigureRigidbody()
        {
            rb.mass = 1f;
            rb.linearDamping = 5f;
            rb.angularDamping = 5f;
            rb.useGravity = true;
            rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        private void SetupVisuals()
        {
            if (meshRenderer != null)
            {
                _playerMaterial = new Material(meshRenderer.material);
                _originalColor = isLocalPlayer 
                    ? new Color(0.2f, 0.5f, 1f)
                    : new Color(1f, 0.3f, 0.3f);
                _playerMaterial.color = _originalColor;
                meshRenderer.material = _playerMaterial;
            }
        }
        
        private void OnDayNightChanged(bool isNight)
        {
            if (!isLocalPlayer) return;
    
            _currentIsNight = isNight;
            if (networkConfig == null) return;
    
            UnregisterFromFogWar();
            RegisterWithFogWar();
    
            _logger?.Log(LogLevel.Info, "Player", "Vision: {0} - Range={1}, Angle={2}", 
                isNight ? "NIGHT" : "DAY",
                networkConfig.GetConeVisionRange(isNight),
                networkConfig.GetConeVisionAngle(isNight));
        }
        
        private void UpdateFogConeDirection()
        {
            if (_fogWar == null || !_fogWar.IsAvailable || _fogRevealerIndex < 0) return;
            
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null) return;

            Vector2 mouseScreenPos = Vector2.zero;
            
            if (UnityEngine.InputSystem.Mouse.current != null)
            {
                mouseScreenPos = UnityEngine.InputSystem.Mouse.current.position.ReadValue();
            }
            else
            {
                return;
            }

            Ray ray = _mainCamera.ScreenPointToRay(mouseScreenPos);
            Plane groundPlane = new Plane(Vector3.up, new Vector3(0, transform.position.y, 0));
            
            if (groundPlane.Raycast(ray, out float distance))
            {
                Vector3 mouseWorldPos = ray.GetPoint(distance);
                Vector3 direction = mouseWorldPos - transform.position;
                direction.y = 0;
                
                if (direction.sqrMagnitude > 0.01f)
                {
                    _fogWar.TrySetConeDirection(_fogRevealerIndex, direction);
                }
            }
        }
        
        private void RegisterWithVisionReceiver()
        {
            if (playerId < 0) return;
    
            var visionReceiver = FindAnyObjectByType<VisionReceiver>();
            if (visionReceiver != null)
            {
                visionReceiver.RegisterPlayer(playerId, gameObject);
                _logger?.Log(LogLevel.Debug, "Player", 
                    "Player {0} registered with VisionReceiver", playerId);
            }
        }
        
        private void UnregisterFromVisionReceiver()
        {
            if (playerId < 0) return;
    
            var visionReceiver = FindAnyObjectByType<VisionReceiver>();
            if (visionReceiver != null)
            {
                visionReceiver.UnregisterPlayer(playerId);
            }
        }
        
        private void RegisterWithFogWar()
        {
            if (!isLocalPlayer || networkConfig == null || _fogRevealerIndex >= 0) return;

            if (_fogWar == null && !OptionalFogOfWarBridge.TryCreate(out _fogWar))
            {
                if (!_fogUnavailableLogged)
                {
                    _logger?.Log(LogLevel.Info, "Player",
                        "Optional Fog of War inactive; using fully-visible fallback");
                    _fogUnavailableLogged = true;
                }

                return;
            }

            _fogUnavailableLogged = false;

            int circleRange = Mathf.RoundToInt(networkConfig.GetBaseVisionRange(_currentIsNight));
            int coneRange = Mathf.RoundToInt(networkConfig.GetConeVisionRange(_currentIsNight));
            float coneAngle = networkConfig.GetConeVisionAngle(_currentIsNight);

            if (_fogWar.TryAddRevealer(
                    transform,
                    circleRange,
                    coneRange,
                    coneAngle,
                    out int revealerIndex))
            {
                _fogRevealerIndex = revealerIndex;

                _logger?.Log(LogLevel.Info, "Player",
                    "FogWar registered - Base:{0}, Cone:{1}, Angle:{2}",
                    circleRange, coneRange, coneAngle);
            }
        }
        
        private void UnregisterFromFogWar()
        {
            if (_fogWar != null && _fogRevealerIndex >= 0)
            {
                _fogWar.TryRemoveRevealer(_fogRevealerIndex);
            }

            _fogRevealerIndex = -1;
        }

        public void SetPlayerId(int id)
        {
            playerId = id;
            UpdateObjectName();
            RegisterWithVisionReceiver();
            RegisterWithFogWar();
        }
        
        public void SetLocalPlayer(bool isLocal)
        {
            isLocalPlayer = isLocal;
            
            _currentState?.OnExit();
            
            if (_localState == null)
                _localState = new LocalPlayerState(this, _networkService, _logger, networkConfig);
            if (_remoteState == null)
                _remoteState = new RemotePlayerState(this, _logger, networkConfig);
            
            _currentState = isLocal ? (IPlayerState)_localState : _remoteState;
            _currentState?.OnEnter();
            
            if (isLocal)
            {
                _inputHandler = GetComponent<PlayerInputHandler>();
                RegisterWithFogWar();
            }
            
            if (_playerMaterial != null)
            {
                _originalColor = isLocal ? new Color(0.2f, 0.5f, 1f) : new Color(1f, 0.3f, 0.3f);
                _playerMaterial.color = _originalColor;
            }
            
            UpdateObjectName();
        }
        
        private void UpdateObjectName()
        {
            gameObject.name = isLocalPlayer ? $"Player_{playerId}_Local" : $"Player_{playerId}_Remote";
        }
        
        private bool CanFire()
        {
            float fireRate = networkConfig != null ? networkConfig.FireRate : 0.5f;
            return IsAlive && Time.time - _lastFireTime >= fireRate;
        }

        private void Fire()
        {
            _lastFireTime = Time.time;

            if (bulletPrefab == null)
            {
                _logger?.Log(LogLevel.Error, "Player", "Bullet prefab not assigned!");
                return;
            }

            Vector3 spawnPos = firePoint != null 
                ? firePoint.position 
                : transform.position + transform.forward * 0.5f + Vector3.up * 0.5f;

            SendFireMessage(spawnPos, GetFireDirection(spawnPos));
    
            _logger?.Log(LogLevel.Debug, "Player", 
                "Player {0} sent fire request", playerId);
        }

        private Vector3 GetFireDirection(Vector3 spawnPosition)
        {
            Vector3 direction = transform.forward;

            if (_inputHandler != null && _inputHandler.MouseWorldPosition != Vector3.zero)
            {
                Vector3 cursorDirection = _inputHandler.MouseWorldPosition - spawnPosition;
                cursorDirection.y = 0f;

                if (cursorDirection.sqrMagnitude > 0.0001f)
                    direction = cursorDirection.normalized;
            }

            direction.y = 0f;
            return direction.sqrMagnitude > 0.0001f
                ? direction.normalized
                : Vector3.forward;
        }

        private void SendFireMessage(Vector3 position, Vector3 direction)
        {
            if (_networkService == null || !_networkService.IsConnected)
                return;
            
            float damage = networkConfig != null ? networkConfig.AttackDamage : 25f;
            float speed = networkConfig != null ? networkConfig.BulletSpeed : 20f;
            
            var fireMsg = new FireMessage
            {
                FirePosition = position,
                FireDirection = direction,
                Damage = damage,
                BulletSpeed = speed
            };
            
            _networkService.SendMessage(fireMsg);
        }

        public void TakeDamage(float damage, int attackerId)
        {
            if (!IsAlive) return;
            
            Health -= damage;
            
            _logger?.Log(LogLevel.Info, "Player", 
                "Player {0} took {1} damage from Player {2}. Health: {3}", 
                playerId, damage, attackerId, Health);
            
            if (_playerMaterial != null)
            {
                _playerMaterial.color = Color.red;
                Invoke(nameof(ResetColor), 0.1f);
            }
            
            if (Health <= 0)
            {
                Die();
            }
        }

        private void ResetColor()
        {
            if (_playerMaterial != null)
            {
                _playerMaterial.color = _originalColor;
            }
        }

        private void Die()
        {
            _logger?.Log(LogLevel.Info, "Player", "Player {0} died", playerId);
            
            IsAlive = false;
            
            if (_inputHandler != null)
            {
                _inputHandler.DisableInput();
            }
            
            _currentState?.OnExit();
            _currentState = _deadState;
            _currentState.OnEnter();
            
            gameObject.SetActive(false);
        }

        private void SubscribeToNetworkEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.RegisterHandler(MessageType.PlayerState, OnPlayerStateMessage);
            }
        }

        private void UnsubscribeFromNetworkEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.UnregisterHandler(MessageType.PlayerState, OnPlayerStateMessage);
            }
        }

        private void OnPlayerStateMessage(INetworkMessage message, int senderId)
        {
            if (senderId != 0)
            {
                _logger?.Log(LogLevel.Warning, "Player", 
                    "Rejected PlayerState from non-server source: {0}", senderId);
                return;
            }
    
            if (message is PlayerStateMessage stateMsg && stateMsg.PlayerId == playerId)
            {
                if (_hasAcceptedSnapshot &&
                    unchecked((int)(stateMsg.TransportSeqNumber - _lastAcceptedSnapshotSeq)) <= 0)
                    return;

                _hasAcceptedSnapshot = true;
                _lastAcceptedSnapshotSeq = stateMsg.TransportSeqNumber;

                float prevHealth = Health;
                Health = stateMsg.Health;
                
                if (Health < prevHealth && Health > 0)
                {
                    _logger?.Log(LogLevel.Info, "Player", 
                        "Player {0} hit! {1} → {2}", playerId, prevHealth, Health);
                    
                    if (_playerMaterial != null)
                    {
                        _playerMaterial.color = Color.red;
                        Invoke(nameof(ResetColor), 0.1f);
                    }
                }
                
                if (!stateMsg.IsAlive && IsAlive)
                {
                    IsAlive = false;
                    Die();
                    return;
                }
                
                if (!IsAlive) return;
                
                if (isLocalPlayer)
                {
                    _localState?.OnServerStateReceived(stateMsg);
                }
                else
                {
                    _remoteState?.SetTargetTransform(stateMsg.Position, stateMsg.Rotation, stateMsg.Velocity);
                }
            }
        }
    }
}
