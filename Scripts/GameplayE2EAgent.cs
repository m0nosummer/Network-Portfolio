using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Arena.Combat;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Input;
using ArenaPlayer = Arena.Player.Player;

namespace Arena.Verification
{
    public sealed class GameplayE2EAgent : MonoBehaviour
    {
        private const string RoleArgument = "-arena-e2e-role";
        private const string RunArgument = "-arena-e2e-run";
        private const string VideoArgument = "-arena-e2e-video";
        private const string FrameDirectoryArgument = "-arena-e2e-frames";
        private const float StartupTimeoutSeconds = 12f;
        private const float ScenarioTimeoutSeconds = 35f;
        private const float CaptureIntervalSeconds = 0.1f;
        private const float HealthTolerance = 0.01f;
        private const float LocalReconciliationTolerance = 0.75f;
        private const float RemoteInterpolationTolerance = 1f;

        private static bool _bootstrapped;

        private string _role;
        private string _runId;
        private bool _videoCaptureMode;
        private string _frameDirectory;
        private int _capturedFrameCount;
        private INetworkService _networkService;
        private Keyboard _virtualKeyboard;
        private Mouse _virtualMouse;
        private readonly List<InputDevice> _temporarilyDisabledInputDevices = new();
        private bool _finished;
        private string _reconnectSignal;
        private VerificationLog _verificationLog;
        private bool _awaitingServerDisconnect;
        private int _expectedDisconnectCount;

        private readonly HashSet<int> _connectedClientIds = new();
        private readonly HashSet<int> _disconnectedClientIds = new();
        private readonly Dictionary<int, string> _handshakeNames = new();
        private readonly HashSet<int> _movingInputSenders = new();
        private readonly Dictionary<int, uint> _lastInputSeqBySender = new();
        private int _fireMessageCount;
        private int _fireSenderId = -1;

        private readonly Dictionary<int, Vector3> _joinPositions = new();
        private readonly Dictionary<int, PlayerStateMessage> _lastStates = new();
        private readonly Dictionary<int, Vector3> _firstStatePositions = new();
        private readonly Dictionary<int, float> _maxStateDisplacements = new();
        private uint? _spawnedBulletId;
        private int _spawnedBulletOwnerId = -1;
        private uint? _destroyedBulletId;
        private DestroyReason? _destroyReason;
        private bool _sawBulletObject;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_bootstrapped)
                return;

            string role = GetArgumentValue(RoleArgument);
            if (string.IsNullOrWhiteSpace(role))
                return;

            _bootstrapped = true;

            var agentObject = new GameObject(nameof(GameplayE2EAgent));
            DontDestroyOnLoad(agentObject);

            var agent = agentObject.AddComponent<GameplayE2EAgent>();
            agent._role = role.Trim().ToLowerInvariant();
            agent._runId = GetArgumentValue(RunArgument) ?? "unspecified";
            agent._videoCaptureMode = GetArgumentValue(VideoArgument) == "1";
            agent._frameDirectory = GetArgumentValue(FrameDirectoryArgument);
            agent._reconnectSignal = GetArgumentValue("-arena-e2e-reconnect-signal");
            agent._verificationLog = new VerificationLog(true);
        }

        private IEnumerator Start()
        {
            if (_role != "server" && _role != "client-a" && _role != "client-b")
            {
                Finish(false, $"invalid-role={_role}");
                yield break;
            }

            yield return null;
            yield return null;

            float startupDeadline = Time.realtimeSinceStartup + StartupTimeoutSeconds;
            while (GameInstaller.Container == null && Time.realtimeSinceStartup < startupDeadline)
                yield return null;

            if (GameInstaller.Container == null)
            {
                Finish(false, "di-container-timeout");
                yield break;
            }

            try
            {
                _networkService = GameInstaller.Container.Resolve<INetworkService>();
            }
            catch (Exception ex)
            {
                Finish(false, $"network-service-resolve={Sanitize(ex.Message)}");
                yield break;
            }

            SubscribeObservers();

            string buttonName = _role == "server" ? "ServerButton" : "ClientButton";
            Button startButton = FindButton(buttonName);
            if (startButton == null)
            {
                Finish(false, $"missing-button={buttonName}");
                yield break;
            }

            Debug.Log(
                $"E2E|BOOT|role={_role}|run={_runId}|button={buttonName}" +
                $"|unity={Application.unityVersion}|platform={Application.platform}|app-version={Application.version}");
            startButton.onClick.Invoke();

            while (!_networkService.IsConnected && Time.realtimeSinceStartup < startupDeadline)
                yield return null;

            if (!_networkService.IsConnected)
            {
                Finish(false, "network-start-timeout");
                yield break;
            }

            Debug.Log($"E2E|READY|role={_role}|run={_runId}|local={_networkService.LocalClientId}");

            if (_videoCaptureMode && _role == "client-a" && !string.IsNullOrWhiteSpace(_frameDirectory))
            {
                try
                {
                    Directory.CreateDirectory(_frameDirectory);
                    StartCoroutine(CaptureGameFrames());
                    Debug.Log($"E2E|FRAME_CAPTURE_BEGIN|role={_role}|directory={Sanitize(_frameDirectory)}");
                }
                catch (Exception ex)
                {
                    Finish(false, $"frame-directory={Sanitize(ex.Message)}");
                    yield break;
                }
            }

            if (_role == "server")
                yield return RunServerScenario();
            else
            {
                if (!string.IsNullOrEmpty(_reconnectSignal))
                {
                    yield return ReconnectBeforeScenario();
                    if (_finished) yield break;
                }
                yield return RunClientScenario();
            }
        }

        private IEnumerator ReconnectBeforeScenario()
        {
            float deadline = Time.realtimeSinceStartup + StartupTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline &&
                   (FindPlayers().Length != 2 || _lastStates.Count != 2 || _joinPositions.Count != 2))
                yield return null;

            ArenaPlayer[] oldPlayers = FindPlayers();
            if (oldPlayers.Length != 2 || oldPlayers.Count(p => p.IsLocalPlayer) != 1 ||
                _networkService.LocalClientId <= 0 || _lastStates.Count != 2 || _joinPositions.Count != 2)
            {
                Finish(false, "reconnect-initial-spawn-timeout");
                yield break;
            }

            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _awaitingServerDisconnect = true;
            Debug.Log($"E2E|RECONNECT_READY|role={_role}|run={_runId}|pid={pid}");
            var manager = FindAnyObjectByType<NetworkTestManager>();
            Button clientButton = FindButton("ClientButton");
            Button serverButton = FindButton("ServerButton");
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            GameObject panel = (GameObject)typeof(NetworkTestManager).GetField("connectPanel", fields).GetValue(manager);
            var pending = (List<PlayerJoinedMessage>)typeof(NetworkTestManager)
                .GetField("_pendingPlayerJoins", fields).GetValue(manager);
            var spawned = (Dictionary<int, GameObject>)typeof(NetworkTestManager)
                .GetField("_spawnedPlayers", fields).GetValue(manager);

            bool cleared = false;
            deadline = Time.realtimeSinceStartup + 20f;
            while (!_finished && Time.realtimeSinceStartup < deadline)
            {
                cleared = !_networkService.IsConnected && _networkService.LocalClientId == -1 &&
                    oldPlayers.All(p => p == null) && FindPlayers().Length == 0 && spawned.Count == 0 &&
                    pending.Count == 0 && panel.activeInHierarchy &&
                    clientButton.interactable && serverButton.interactable &&
                    (int)typeof(NetworkTestManager).GetField("_localPlayerId", fields).GetValue(manager) == -1;
                if (cleared) break;
                yield return null;
            }
            if (_finished) yield break;
            if (!cleared)
            {
                Finish(false, $"reconnect-cleanup-timeout|connected={_networkService.IsConnected}|players={FindPlayers().Length}");
                yield break;
            }
            if (_expectedDisconnectCount != 1)
            {
                Finish(false, $"reconnect-disconnect-events={_expectedDisconnectCount}");
                yield break;
            }
            try { _verificationLog.ConsumeExpectedConnectionReset(); }
            catch (Exception ex)
            {
                Finish(false, "reconnect-unexpected-error=" + Sanitize(ex.Message));
                yield break;
            }
            _awaitingServerDisconnect = false;
            Debug.Log($"E2E|RECONNECT_CLEANED|role={_role}|run={_runId}|pid={pid}|players=0|id=-1|pending=0|ui=ready");

            deadline = Time.realtimeSinceStartup + 20f;
            while (!File.Exists(_reconnectSignal) && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!File.Exists(_reconnectSignal))
            {
                Finish(false, "reconnect-restart-signal-timeout");
                yield break;
            }

            _joinPositions.Clear();
            _lastStates.Clear();
            _firstStatePositions.Clear();
            _maxStateDisplacements.Clear();
            clientButton.onClick.Invoke();
            deadline = Time.realtimeSinceStartup + StartupTimeoutSeconds;
            while ((!_networkService.IsConnected || _networkService.LocalClientId <= 0) &&
                   Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!_networkService.IsConnected || _networkService.LocalClientId <= 0)
            {
                Finish(false, "reconnect-handshake-timeout");
                yield break;
            }
            Debug.Log($"E2E|RECONNECTED|role={_role}|run={_runId}|pid={pid}|local={_networkService.LocalClientId}");
        }

        private IEnumerator RunServerScenario()
        {
            float deadline = Time.realtimeSinceStartup + ScenarioTimeoutSeconds;

            while (!_finished && Time.realtimeSinceStartup < deadline)
            {
                bool transportAndGameplayObserved =
                    _connectedClientIds.Count == 2 &&
                    _handshakeNames.Count == 2 &&
                    _movingInputSenders.Count == 2 &&
                    _fireMessageCount == 1 &&
                    _connectedClientIds.Contains(_fireSenderId);

                if (transportAndGameplayObserved && _disconnectedClientIds.Count == 2)
                {
                    string ids = string.Join(",", _connectedClientIds.OrderBy(id => id));
                    Finish(true,
                        $"clients=2|ids={ids}|moving-inputs=2|fire-sender={_fireSenderId}|disconnects=2");
                    yield break;
                }

                yield return null;
            }

            Finish(false,
                $"server-timeout|connected={_connectedClientIds.Count}|handshakes={_handshakeNames.Count}" +
                $"|moving-inputs={_movingInputSenders.Count}|fires={_fireMessageCount}" +
                $"|fire-sender={_fireSenderId}|disconnects={_disconnectedClientIds.Count}");
        }

        private IEnumerator RunClientScenario()
        {
            float deadline = Time.realtimeSinceStartup + ScenarioTimeoutSeconds;

            while (_networkService.LocalClientId <= 0 && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (_networkService.LocalClientId <= 0)
            {
                Finish(false, "client-id-timeout");
                yield break;
            }

            int localId = _networkService.LocalClientId;
            Debug.Log($"E2E|IDENTIFIED|role={_role}|local={localId}");

            while (Time.realtimeSinceStartup < deadline)
            {
                ArenaPlayer[] players = FindPlayers();
                int localCount = players.Count(player => player.IsLocalPlayer);
                int remoteCount = players.Count(player => !player.IsLocalPlayer);

                if (localCount == 1 && remoteCount == 1 &&
                    _joinPositions.Count >= 2 && _lastStates.Count >= 2)
                {
                    break;
                }

                yield return null;
            }

            ArenaPlayer localPlayer = FindPlayers().SingleOrDefault(player => player.IsLocalPlayer);
            ArenaPlayer remotePlayer = FindPlayers().SingleOrDefault(player => !player.IsLocalPlayer);
            if (localPlayer == null || remotePlayer == null || _joinPositions.Count < 2 || _lastStates.Count < 2)
            {
                Finish(false,
                    $"player-spawn-timeout|players={FindPlayers().Length}|joins={_joinPositions.Count}|states={_lastStates.Count}");
                yield break;
            }

            int remoteId = remotePlayer.PlayerId;
            Vector3 localStart = _lastStates[localId].Position;
            Vector3 remoteStart = _lastStates[remoteId].Position;
            NetworkConfig config = GameInstaller.Container.Resolve<NetworkConfig>();
            float expectedTargetHealth = Mathf.Max(0f, config.PlayerMaxHealth - config.AttackDamage);
            int expectedBulletOwnerId = _role == "client-a" ? localId : remoteId;

            CreateVirtualInputDevices();

            if (_role == "client-a")
            {
                if (_videoCaptureMode)
                {
                    yield return new WaitForSecondsRealtime(1f);
                    yield return SendVideoFireAt(remoteStart, localPlayer, remoteId);
                }
                else
                    yield return AimAndFireAt(remoteStart, localPlayer, remoteId);

                if (_finished)
                    yield break;
            }

            int expectedTargetId = _role == "client-a" ? remoteId : localId;

            while (Time.realtimeSinceStartup < deadline)
            {
                ObserveBulletObject();

                bool matchingLifecycle =
                    _spawnedBulletId.HasValue &&
                    _spawnedBulletOwnerId == expectedBulletOwnerId &&
                    _destroyedBulletId == _spawnedBulletId &&
                    _destroyReason == DestroyReason.HitPlayer &&
                    _sawBulletObject;

                bool targetHealthReplicated =
                    _lastStates.TryGetValue(expectedTargetId, out PlayerStateMessage remoteHealthState) &&
                    Mathf.Abs(remoteHealthState.Health - expectedTargetHealth) <= HealthTolerance;

                if (matchingLifecycle && targetHealthReplicated &&
                    !HasBulletObject(_spawnedBulletId.Value))
                {
                    break;
                }

                yield return null;
            }

            PlayerStateMessage targetState = null;
            bool lifecyclePassed =
                _spawnedBulletId.HasValue &&
                _spawnedBulletOwnerId == expectedBulletOwnerId &&
                _destroyedBulletId == _spawnedBulletId &&
                _destroyReason == DestroyReason.HitPlayer &&
                _sawBulletObject &&
                !HasBulletObject(_spawnedBulletId.Value) &&
                _lastStates.TryGetValue(expectedTargetId, out targetState) &&
                Mathf.Abs(targetState.Health - expectedTargetHealth) <= HealthTolerance;

            if (!lifecyclePassed)
            {
                string targetHealth = _lastStates.TryGetValue(expectedTargetId, out PlayerStateMessage state)
                    ? state.Health.ToString("F2")
                    : "missing";
                Finish(false,
                    $"bullet|spawn={_spawnedBulletId?.ToString() ?? "none"}" +
                    $"|owner={_spawnedBulletOwnerId}|expected-owner={expectedBulletOwnerId}" +
                    $"|destroy={_destroyedBulletId?.ToString() ?? "none"}" +
                    $"|reason={_destroyReason?.ToString() ?? "none"}" +
                    $"|object={_sawBulletObject}|target-health={targetHealth}" +
                    $"|expected-health={expectedTargetHealth:F2}");
                yield break;
            }

            Debug.Log(
                $"E2E|BULLET_OK|role={_role}|id={_spawnedBulletId.Value}" +
                $"|owner={_spawnedBulletOwnerId}|reason={_destroyReason}" +
                $"|target-health={targetState.Health:F2}|expected-health={expectedTargetHealth:F2}");

            if (_videoCaptureMode && _role == "client-a")
            {
                yield return SendVideoMovement(localId);
            }
            else
            {
                QueueKeyboardState(Key.W);
                Debug.Log($"E2E|INPUT_BEGIN|role={_role}|local={localId}|key=W");

                float outwardMovementEnd = Time.realtimeSinceStartup + 0.6f;
                while (Time.realtimeSinceStartup < outwardMovementEnd)
                    yield return null;

                QueueKeyboardState(Key.S);
                Debug.Log($"E2E|INPUT_RETURN|role={_role}|local={localId}|key=S");

                float returnMovementEnd = Time.realtimeSinceStartup + 0.6f;
                while (Time.realtimeSinceStartup < returnMovementEnd)
                    yield return null;

                QueueKeyboardState();
                Debug.Log($"E2E|INPUT_END|role={_role}|local={localId}");
            }

            while (Time.realtimeSinceStartup < deadline)
            {
                if (_lastStates.TryGetValue(localId, out PlayerStateMessage localState) &&
                    _lastStates.TryGetValue(remoteId, out PlayerStateMessage remoteState))
                {
                    float localMaxDelta = _maxStateDisplacements.GetValueOrDefault(localId);
                    float remoteMaxDelta = _maxStateDisplacements.GetValueOrDefault(remoteId);
                    float localError = HorizontalDistance(localPlayer.transform.position, localState.Position);
                    float remoteError = HorizontalDistance(remotePlayer.transform.position, remoteState.Position);
                    if (MovementComplete(localState, remoteState, localMaxDelta, remoteMaxDelta, localError, remoteError))
                        break;
                }

                yield return null;
            }

            if (!_lastStates.TryGetValue(localId, out PlayerStateMessage finalLocalState) ||
                !_lastStates.TryGetValue(remoteId, out PlayerStateMessage finalRemoteState))
            {
                Finish(false, "missing-authoritative-state");
                yield break;
            }

            float localAuthoritativeDelta = _maxStateDisplacements.GetValueOrDefault(localId);
            float remoteAuthoritativeDelta = _maxStateDisplacements.GetValueOrDefault(remoteId);
            float localReconciliationError = HorizontalDistance(localPlayer.transform.position, finalLocalState.Position);
            float remoteInterpolationError = HorizontalDistance(remotePlayer.transform.position, finalRemoteState.Position);

            if (!MovementComplete(finalLocalState, finalRemoteState, localAuthoritativeDelta,
                    remoteAuthoritativeDelta, localReconciliationError, remoteInterpolationError))
            {
                Finish(false,
                    $"movement|ack={finalLocalState.LastProcessedInput}" +
                    $"|local-delta={localAuthoritativeDelta:F2}|remote-delta={remoteAuthoritativeDelta:F2}" +
                    $"|local-error={localReconciliationError:F2}|remote-error={remoteInterpolationError:F2}");
                yield break;
            }

            Debug.Log(
                $"E2E|MOVEMENT_OK|role={_role}|local-delta={localAuthoritativeDelta:F2}" +
                $"|remote-delta={remoteAuthoritativeDelta:F2}|local-error={localReconciliationError:F2}" +
                $"|remote-error={remoteInterpolationError:F2}");

            if (_videoCaptureMode)
            {
                float holdSeconds = _role == "client-a" ? 1f : 5f;
                Debug.Log($"E2E|VIDEO_HOLD|role={_role}|seconds={holdSeconds:F1}");
                yield return new WaitForSecondsRealtime(holdSeconds);
            }

            Finish(true,
                $"local={localId}|remote={remoteId}|movement={localAuthoritativeDelta:F2}" +
                $"|bullet={_spawnedBulletId.Value}|reason={_destroyReason}|health={targetState.Health:F2}");
        }

        private IEnumerator SendVideoFireAt(
            Vector3 authoritativeTarget,
            ArenaPlayer localPlayer,
            int remoteId)
        {
            NetworkConfig config = GameInstaller.Container.Resolve<NetworkConfig>();
            Vector3 direction = authoritativeTarget - localPlayer.transform.position;
            direction.y = 0f;

            if (direction.sqrMagnitude < 0.01f)
            {
                Finish(false, $"video-target-overlap={remoteId}");
                yield break;
            }

            direction.Normalize();
            var fire = new FireMessage
            {
                FirePosition = localPlayer.transform.position + direction * 0.5f + Vector3.up,
                FireDirection = direction,
                Damage = config.AttackDamage,
                BulletSpeed = config.BulletSpeed
            };

            _networkService.SendMessage(fire);
            Debug.Log(
                $"E2E|VIDEO_FIRE|role={_role}|target={remoteId}" +
                $"|position={Format(fire.FirePosition)}|direction={Format(direction)}");
            yield return null;
        }

        private IEnumerator CaptureGameFrames()
        {
            float nextCaptureTime = 0f;

            while (!_finished)
            {
                yield return new WaitForEndOfFrame();

                float now = Time.realtimeSinceStartup;
                if (now < nextCaptureTime)
                    continue;

                nextCaptureTime = now + CaptureIntervalSeconds;
                var frame = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);

                try
                {
                    frame.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                    frame.Apply(false, false);

                    string framePath = Path.Combine(
                        _frameDirectory,
                        $"frame-{_capturedFrameCount:D5}.jpg");
                    File.WriteAllBytes(framePath, frame.EncodeToJPG(92));
                    _capturedFrameCount++;
                }
                catch (Exception ex)
                {
                    Destroy(frame);
                    Finish(false, $"frame-capture={Sanitize(ex.Message)}");
                    yield break;
                }

                Destroy(frame);
            }

            Debug.Log($"E2E|FRAME_CAPTURE_END|role={_role}|frames={_capturedFrameCount}");
        }

        private IEnumerator SendVideoMovement(int localId)
        {
            uint seq = 100000;
            Debug.Log($"E2E|VIDEO_INPUT_BEGIN|role={_role}|local={localId}|direction=W");

            float outwardDeadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < outwardDeadline)
            {
                SendVideoInput(Vector2.up, seq++);
                yield return new WaitForSecondsRealtime(0.05f);
            }

            Debug.Log($"E2E|VIDEO_INPUT_RETURN|role={_role}|local={localId}|direction=S");
            float returnDeadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < returnDeadline)
            {
                SendVideoInput(Vector2.down, seq++);
                yield return new WaitForSecondsRealtime(0.05f);
            }

            SendVideoInput(Vector2.zero, seq);
            Debug.Log($"E2E|VIDEO_INPUT_END|role={_role}|local={localId}");
        }

        private void SendVideoInput(Vector2 moveInput, uint seq)
        {
            _networkService.SendMessage(new PlayerInputMessage
            {
                MoveInput = moveInput,
                MouseWorldPosition = Vector3.zero,
                AttackPressed = false,
                SeqNumber = seq
            });
        }

        private IEnumerator AimAndFireAt(Vector3 authoritativeTarget, ArenaPlayer localPlayer, int remoteId)
        {
            Camera camera = Camera.main;
            PlayerInputHandler inputHandler = localPlayer.GetComponent<PlayerInputHandler>();
            if (camera == null || inputHandler == null || _virtualMouse == null)
            {
                Finish(false, "aim-prerequisites-missing");
                yield break;
            }

            Vector3 flatTargetDirection = authoritativeTarget - localPlayer.transform.position;
            flatTargetDirection.y = 0f;
            if (flatTargetDirection.sqrMagnitude < 0.01f)
            {
                Finish(false, $"target-overlap={remoteId}");
                yield break;
            }
            flatTargetDirection.Normalize();

            Vector3 screenPoint = Vector3.zero;
            float facingAngle = float.MaxValue;
            float rotationDeadline = Time.realtimeSinceStartup + 2f;
            while (facingAngle > 1.5f && Time.realtimeSinceStartup < rotationDeadline)
            {
                screenPoint = camera.WorldToScreenPoint(authoritativeTarget);
                if (screenPoint.z <= 0f)
                {
                    Finish(false, $"target-behind-camera={remoteId}");
                    yield break;
                }

                InputSystem.QueueDeltaStateEvent(
                    _virtualMouse.position,
                    new Vector2(screenPoint.x, screenPoint.y));
                yield return null;

                facingAngle = Vector3.Angle(localPlayer.transform.forward, flatTargetDirection);
            }

            float aimError = HorizontalDistance(inputHandler.MouseWorldPosition, authoritativeTarget);
            if (aimError > 1f)
            {
                Finish(false,
                    $"aim-error={aimError:F2}|facing-angle={facingAngle:F2}|target={remoteId}");
                yield break;
            }

            Debug.Log(
                $"E2E|AIM_OK|role={_role}|position={Format(localPlayer.transform.position)}" +
                $"|target={Format(authoritativeTarget)}|forward={Format(localPlayer.transform.forward)}" +
                $"|angle={facingAngle:F2}");

            Vector2 mousePosition = new Vector2(screenPoint.x, screenPoint.y);
            MouseState pressedState = new MouseState
            {
                position = mousePosition
            }.WithButton(MouseButton.Left, true);
            InputSystem.QueueStateEvent(_virtualMouse, pressedState);
            yield return null;
            MouseState releasedState = new MouseState
            {
                position = mousePosition
            }.WithButton(MouseButton.Left, false);
            InputSystem.QueueStateEvent(_virtualMouse, releasedState);
            yield return null;

            Debug.Log($"E2E|FIRE_INPUT|role={_role}|target={remoteId}|aim-error={aimError:F2}");
        }

        private void SubscribeObservers()
        {
            _networkService.OnClientConnected += OnClientConnected;
            _networkService.OnClientDisconnected += OnClientDisconnected;
            _networkService.OnConnectionFailed += OnConnectionFailed;

            IMessageRouter router = _networkService.MessageRouter;
            router.RegisterHandler(MessageType.Handshake, OnHandshake);
            router.RegisterHandler(MessageType.PlayerJoined, OnPlayerJoined);
            router.RegisterHandler(MessageType.PlayerInput, OnPlayerInput);
            router.RegisterHandler(MessageType.PlayerState, OnPlayerState);
            router.RegisterHandler(MessageType.Fire, OnFire);
            router.RegisterHandler(MessageType.BulletSpawn, OnBulletSpawn);
            router.RegisterHandler(MessageType.BulletDestroy, OnBulletDestroy);
        }

        private void UnsubscribeObservers()
        {
            if (_networkService == null)
                return;

            _networkService.OnClientConnected -= OnClientConnected;
            _networkService.OnClientDisconnected -= OnClientDisconnected;
            _networkService.OnConnectionFailed -= OnConnectionFailed;

            IMessageRouter router = _networkService.MessageRouter;
            if (router == null)
                return;

            router.UnregisterHandler(MessageType.Handshake, OnHandshake);
            router.UnregisterHandler(MessageType.PlayerJoined, OnPlayerJoined);
            router.UnregisterHandler(MessageType.PlayerInput, OnPlayerInput);
            router.UnregisterHandler(MessageType.PlayerState, OnPlayerState);
            router.UnregisterHandler(MessageType.Fire, OnFire);
            router.UnregisterHandler(MessageType.BulletSpawn, OnBulletSpawn);
            router.UnregisterHandler(MessageType.BulletDestroy, OnBulletDestroy);
        }

        private void OnClientConnected(int clientId)
        {
            _connectedClientIds.Add(clientId);
            Debug.Log($"E2E|CONNECTED|role={_role}|client={clientId}");
        }

        private void OnClientDisconnected(int clientId)
        {
            _disconnectedClientIds.Add(clientId);
            Debug.Log($"E2E|DISCONNECTED|role={_role}|client={clientId}");
        }

        private void OnConnectionFailed(string reason)
        {
            if (_awaitingServerDisconnect && reason == "Connection to server closed")
            {
                _expectedDisconnectCount++;
                Debug.Log($"E2E|EXPECTED_DISCONNECT|role={_role}|run={_runId}");
                return;
            }
            if (!_finished)
                Finish(false, $"connection={Sanitize(reason)}");
        }

        private void OnHandshake(INetworkMessage message, int senderId)
        {
            if (_role != "server" || message is not HandshakeMessage handshake || senderId <= 0)
                return;

            _handshakeNames[senderId] = handshake.PlayerName;
            Debug.Log($"E2E|HANDSHAKE|role=server|client={senderId}|name={Sanitize(handshake.PlayerName)}");
        }

        private void OnPlayerJoined(INetworkMessage message, int senderId)
        {
            if (_role == "server" || senderId != 0 || message is not PlayerJoinedMessage joined)
                return;

            _joinPositions[joined.PlayerId] = joined.SpawnPosition;
            Debug.Log($"E2E|PLAYER_JOINED|role={_role}|player={joined.PlayerId}|position={Format(joined.SpawnPosition)}");
        }

        private void OnPlayerInput(INetworkMessage message, int senderId)
        {
            if (_role != "server" || message is not PlayerInputMessage input || senderId <= 0)
                return;

            _lastInputSeqBySender[senderId] = input.SeqNumber;
            if (input.MoveInput.sqrMagnitude > 0.1f)
                _movingInputSenders.Add(senderId);
        }

        private void OnPlayerState(INetworkMessage message, int senderId)
        {
            if (_role == "server" || senderId != 0 || message is not PlayerStateMessage state)
                return;

            if (!_firstStatePositions.ContainsKey(state.PlayerId))
                _firstStatePositions[state.PlayerId] = state.Position;

            float displacement = HorizontalDistance(_firstStatePositions[state.PlayerId], state.Position);
            _maxStateDisplacements[state.PlayerId] = Mathf.Max(
                _maxStateDisplacements.GetValueOrDefault(state.PlayerId),
                displacement);
            _lastStates[state.PlayerId] = state;
        }

        private void OnFire(INetworkMessage message, int senderId)
        {
            if (_role != "server" || message is not FireMessage fire || senderId <= 0)
                return;

            _fireMessageCount++;
            _fireSenderId = senderId;
            Debug.Log(
                $"E2E|FIRE_MESSAGE|role=server|sender={senderId}|count={_fireMessageCount}" +
                $"|position={Format(fire.FirePosition)}|direction={Format(fire.FireDirection)}");
        }

        private void OnBulletSpawn(INetworkMessage message, int senderId)
        {
            if (_role == "server" || senderId != 0 || message is not BulletSpawnMessage spawn)
                return;

            if (_spawnedBulletId.HasValue && _spawnedBulletId.Value != spawn.BulletId)
            {
                Finish(false, $"multiple-bullets={_spawnedBulletId.Value},{spawn.BulletId}");
                return;
            }

            _spawnedBulletId = spawn.BulletId;
            _spawnedBulletOwnerId = spawn.OwnerId;
            Debug.Log($"E2E|BULLET_SPAWN|role={_role}|id={spawn.BulletId}|owner={spawn.OwnerId}");
        }

        private void OnBulletDestroy(INetworkMessage message, int senderId)
        {
            if (_role == "server" || senderId != 0 || message is not BulletDestroyMessage destroy)
                return;

            _destroyedBulletId = destroy.BulletId;
            _destroyReason = destroy.Reason;
            Debug.Log($"E2E|BULLET_DESTROY|role={_role}|id={destroy.BulletId}|reason={destroy.Reason}");
        }

        private void CreateVirtualInputDevices()
        {
            InputDevice[] existingPointerDevices = InputSystem.devices
                .Where(device => device is Keyboard || device is Mouse)
                .ToArray();

            _virtualKeyboard = InputSystem.AddDevice<Keyboard>($"ArenaE2EKeyboard-{_role}");
            _virtualMouse = InputSystem.AddDevice<Mouse>($"ArenaE2EMouse-{_role}");

            foreach (InputDevice device in existingPointerDevices)
            {
                if (!device.enabled)
                    continue;

                InputSystem.DisableDevice(device);
                _temporarilyDisabledInputDevices.Add(device);
            }

            _virtualKeyboard.MakeCurrent();
            _virtualMouse.MakeCurrent();
        }

        private void QueueKeyboardState(params Key[] pressedKeys)
        {
            if (_virtualKeyboard != null)
                InputSystem.QueueStateEvent(_virtualKeyboard, new KeyboardState(pressedKeys));
        }

        private void ObserveBulletObject()
        {
            if (_spawnedBulletId.HasValue && HasBulletObject(_spawnedBulletId.Value))
                _sawBulletObject = true;
        }

        private static bool HasBulletObject(uint networkId)
        {
            return FindObjectsByType<Bullet>(FindObjectsInactive.Include)
                .Any(bullet => bullet.NetworkId == networkId);
        }

        private static ArenaPlayer[] FindPlayers()
        {
            return FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude);
        }

        private static Button FindButton(string objectName)
        {
            return FindObjectsByType<Button>(FindObjectsInactive.Include)
                .FirstOrDefault(button => button.gameObject.name == objectName);
        }

        private void Finish(bool success, string details)
        {
            if (_finished)
                return;

            try { _verificationLog?.ThrowIfErrors(); }
            catch (Exception ex) { success = false; details += "|unexpected-error=" + Sanitize(ex.Message); }
            _finished = true;
            string marker = success ? "PASS" : "FAIL";
            Debug.Log($"E2E|{marker}|role={_role}|run={_runId}|{details}");
            StartCoroutine(QuitAfterLogFlush(success ? 0 : 1));
        }

        private IEnumerator QuitAfterLogFlush(int exitCode)
        {
            yield return new WaitForSecondsRealtime(0.25f);
            _networkService?.Disconnect();
            yield return new WaitForSecondsRealtime(0.25f);
            try { _verificationLog?.ThrowIfErrors(); }
            catch (Exception ex)
            {
                exitCode = 1;
                Debug.Log($"E2E|FAIL|role={_role}|run={_runId}|shutdown-error={Sanitize(ex.Message)}");
            }
            Application.Quit(exitCode);
        }

        private static bool MovementComplete(PlayerStateMessage local, PlayerStateMessage remote,
            float localDelta, float remoteDelta, float localError, float remoteError)
        {
            return local.LastProcessedInput > 0 &&
                VerificationChecks.Finite(localDelta) && localDelta >= 2f &&
                VerificationChecks.Finite(remoteDelta) && remoteDelta >= 2f &&
                VerificationChecks.Finite(localError) && localError <= LocalReconciliationTolerance &&
                VerificationChecks.Finite(remoteError) && remoteError <= RemoteInterpolationTolerance &&
                local.Velocity.sqrMagnitude < 0.01f && remote.Velocity.sqrMagnitude < 0.01f;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

        private static string GetArgumentValue(string key)
        {
            string prefix = key + "=";
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return argument.Substring(prefix.Length);
            }

            return null;
        }

        private static string Sanitize(string value)
        {
            return string.IsNullOrEmpty(value)
                ? "none"
                : value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private void OnDestroy()
        {
            _verificationLog?.Dispose();
            UnsubscribeObservers();

            foreach (InputDevice device in _temporarilyDisabledInputDevices)
            {
                if (device != null && device.added)
                    InputSystem.EnableDevice(device);
            }
            _temporarilyDisabledInputDevices.Clear();

            if (_virtualKeyboard != null && _virtualKeyboard.added)
                InputSystem.RemoveDevice(_virtualKeyboard);

            if (_virtualMouse != null && _virtualMouse.added)
                InputSystem.RemoveDevice(_virtualMouse);
        }
    }
}
