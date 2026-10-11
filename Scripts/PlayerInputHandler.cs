using UnityEngine;
using UnityEngine.InputSystem;
using Arena.Core.DependencyInjection;
using Arena.Logging;

namespace Arena.Input
{
    public class PlayerInputHandler : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private bool enableInputOnStart = true;
        [SerializeField] private LayerMask groundLayer = ~0;
        
        private PlayerInputActions _inputActions;
        private IGameLogger _logger;
        private Camera _mainCamera;
        private int _firePressedFrame = -1;
        
        public Vector2 MoveInput { get; private set; }
        public Vector2 MouseScreenPosition { get; private set; }
        public Vector3 MouseWorldPosition { get; private set; }
        public bool FirePressed { get; private set; }
        public bool FireHeld { get; private set; }
        
        public event System.Action OnFirePerformed;
        
        public bool IsInputEnabled { get; private set; }

        private void Awake()
        {
            InitializeInputActions();
            InjectDependencies();
            CacheReferences();
        }

        private void Start()
        {
            if (enableInputOnStart)
            {
                EnableInput();
            }
            
            _logger?.Log(LogLevel.Debug, "Input", "PlayerInputHandler initialized");
        }

        private void OnEnable()
        {
            if (_inputActions != null && IsInputEnabled)
            {
                EnableInput();
            }
        }

        private void Update()
        {
            if (!IsInputEnabled) return;
            
            UpdateContinuousInputs();
            UpdateMouseWorldPosition();
        }

        private void LateUpdate()
        {
            if (!IsInputEnabled) return;

            if (FirePressed && Time.frameCount > _firePressedFrame)
            {
                ResetButtonStates();
            }
        }

        private void InitializeInputActions()
        {
            try
            {
                _inputActions = new PlayerInputActions();
                SubscribeToInputEvents();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PlayerInputHandler] Failed to initialize: {ex.Message}");
            }
        }

        private void InjectDependencies()
        {
            var container = GameInstaller.Container;
            if (container != null && container.IsRegistered<IGameLogger>())
            {
                _logger = container.Resolve<IGameLogger>();
            }
        }

        private void CacheReferences()
        {
            _mainCamera = Camera.main;
            
            if (_mainCamera == null)
            {
                Debug.LogWarning("[PlayerInputHandler] Main camera not found!");
            }
        }

        private void SubscribeToInputEvents()
        {
            if (_inputActions == null) return;

            _inputActions.Player.Fire.performed += OnFireInputPerformed;
        }

        private void UnsubscribeFromInputEvents()
        {
            if (_inputActions == null) return;

            _inputActions.Player.Fire.performed -= OnFireInputPerformed;
        }

        private void OnFireInputPerformed(InputAction.CallbackContext context)
        {
            if (!IsInputEnabled) return;
            
            FirePressed = true;
            _firePressedFrame = Time.frameCount;
            OnFirePerformed?.Invoke();
            
            _logger?.Log(LogLevel.Trace, "Input", "Fire pressed");
        }
        
        private void UpdateContinuousInputs()
        {
            if (_inputActions == null) return;

            MoveInput = _inputActions.Player.Move.ReadValue<Vector2>();
            MouseScreenPosition = _inputActions.Player.Look.ReadValue<Vector2>();
            FireHeld = _inputActions.Player.Fire.IsPressed();
        }
        private void UpdateMouseWorldPosition()
        {
            if (_mainCamera == null)
            {
                MouseWorldPosition = Vector3.zero;
                return;
            }
    
            Ray ray = _mainCamera.ScreenPointToRay(MouseScreenPosition);
    
            if (Physics.Raycast(ray, out RaycastHit hit, 1000f, groundLayer))
            {
                MouseWorldPosition = hit.point;
            }
            else
            {
                float distance = -ray.origin.y / ray.direction.y;
                if (distance > 0)
                {
                    MouseWorldPosition = ray.origin + ray.direction * distance;
                }
            }
        }

        private void ResetButtonStates()
        {
            FirePressed = false;
            _firePressedFrame = -1;
        }

        public bool ConsumeFirePressed()
        {
            if (!FirePressed)
                return false;

            ResetButtonStates();
            return true;
        }

        public void EnableInput()
        {
            if (_inputActions == null)
            {
                Debug.LogError("[PlayerInputHandler] Input actions not initialized!");
                return;
            }
            
            _inputActions.Player.Enable();
            IsInputEnabled = true;
            
            _logger?.Log(LogLevel.Debug, "Input", "Player input enabled");
        }

        public void DisableInput()
        {
            if (_inputActions == null) return;
            
            _inputActions.Player.Disable();
            IsInputEnabled = false;
            
            MoveInput = Vector2.zero;
            FirePressed = false;
            FireHeld = false;
            
            _logger?.Log(LogLevel.Debug, "Input", "Player input disabled");
        }

        public void SetGroundLayer(LayerMask layer)
        {
            groundLayer = layer;
        }

        private void OnDisable()
        {
            DisableInput();
        }

        private void OnDestroy()
        {
            UnsubscribeFromInputEvents();
            _inputActions?.Dispose();
        }
    }
}
