using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Core.Utilities;
using Arena.Logging;
using Arena.Network.Messages;

namespace Arena.Network
{
    public class NetworkTestManager : MonoBehaviour
    {
        [Header("Network Settings")]
        [SerializeField] private string serverIP = "127.0.0.1";
        [SerializeField] private int serverPort = 7777;
        
        [Header("UI References")]
        [SerializeField] private Button serverButton;
        [SerializeField] private Button clientButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private GameObject networkTestUI;
        [SerializeField] private GameObject connectPanel;
        
        [Header("Player Prefab")]
        [SerializeField] private GameObject playerPrefab;

        private INetworkService _networkService;
        private IGameLogger _logger;
        private int _localPlayerId = -1;
        private int _sessionGeneration;
        private bool _wasConnected;
        
        private readonly Dictionary<int, GameObject> _spawnedPlayers = new();
        private readonly List<PlayerJoinedMessage> _pendingPlayerJoins = new();

        private void Start()
        {
            InitializeDependencies();
            SetupUI();
            SubscribeToNetworkEvents();
            
            UpdateStatus("Ready - Choose Server or Client");
        }

        private void InitializeDependencies()
        {
            var container = GameInstaller.Container;
            
            if (container == null)
            {
                Debug.LogError("[NetworkTestManager] DIContainer not found!");
                return;
            }

            _networkService = container.Resolve<INetworkService>();
            _logger = container.Resolve<IGameLogger>();
        }

        private void Update()
        {
            bool isConnected = _networkService?.IsConnected == true;
            if (_wasConnected && !isConnected)
            {
                ResetSession();
                UpdateStatus("Disconnected - Choose Server or Client");
            }
            _wasConnected = isConnected;
        }

        private void SetupUI()
        {
            if (serverButton != null)
                serverButton.onClick.AddListener(StartServer);
            
            if (clientButton != null)
                clientButton.onClick.AddListener(StartClient);
        }

        private void SubscribeToNetworkEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.RegisterHandler(MessageType.Handshake, OnHandshakeMessage);
                _networkService.MessageRouter.RegisterHandler(MessageType.PlayerJoined, OnPlayerJoinedMessage);
                _networkService.MessageRouter.RegisterHandler(MessageType.PlayerLeft, OnPlayerLeftMessage);
        
                _networkService.OnClientConnected += HandleClientConnected;
                _networkService.OnClientDisconnected += HandleClientDisconnected;
                _networkService.OnConnectionFailed += HandleConnectionFailed;
            }
        }

        private void UnsubscribeFromNetworkEvents()
        {
            if (_networkService != null)
            {
                _networkService.MessageRouter.UnregisterHandler(MessageType.Handshake, OnHandshakeMessage);
                _networkService.MessageRouter.UnregisterHandler(MessageType.PlayerJoined, OnPlayerJoinedMessage);
                _networkService.MessageRouter.UnregisterHandler(MessageType.PlayerLeft, OnPlayerLeftMessage);
        
                _networkService.OnClientConnected -= HandleClientConnected;
                _networkService.OnClientDisconnected -= HandleClientDisconnected;
                _networkService.OnConnectionFailed -= HandleConnectionFailed;
            }
        }

        private void OnHandshakeMessage(INetworkMessage message, int senderId)
        {
            if (message is HandshakeMessage handshake)
                HandleHandshake(handshake, senderId);
        }

        private async void StartServer()
        {
            if (_networkService == null)
            {
                _logger?.Log(LogLevel.Error, "NetworkTest", "NetworkService not available!");
                return;
            }
            try
            {
                DisableButtons();
                UpdateStatus("Starting server...");
                _logger?.Log(LogLevel.Info, "NetworkTest", "Starting server...");

                await _networkService.StartServerAsync(serverPort);

                UpdateStatus($"Server running on port {serverPort}");
                _logger?.Log(LogLevel.Info, "NetworkTest", "Server started on port {0}", serverPort);
            }
            catch (Exception ex)
            {
                UpdateStatus("Failed to start server");
                _logger?.Log(LogLevel.Error, "NetworkTest", "Server start failed: {0}", ex.Message);
                EnableButtons();
            }
        }

        private async void StartClient()
        {
            if (_networkService == null)
            {
                _logger?.Log(LogLevel.Error, "NetworkTest", "NetworkService not available!");
                return;
            }

            try
            {
                ResetSession();
                DisableButtons();
                UpdateStatus("Connecting to server...");
                _logger?.Log(LogLevel.Info, "NetworkTest", "Connecting to {0}:{1}...", serverIP, serverPort);

                await _networkService.ConnectToServerAsync(serverIP, serverPort);

                if (!_networkService.IsConnected)
                    throw new InvalidOperationException("Connection closed before handshake");

                _wasConnected = true;

                UpdateStatus($"Connected to {serverIP}:{serverPort}");
                _logger?.Log(LogLevel.Info, "NetworkTest", "Connected!");

                SendHandshake();
                if (connectPanel != null)
                {
                    connectPanel.SetActive(false);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("Failed to connect");
                _logger?.Log(LogLevel.Error, "NetworkTest", "Connection failed: {0}", ex.Message);
                ResetSession();
            }
        }

        private void SendHandshake()
        {
            var handshake = new HandshakeMessage
            {
                ClientVersion = "1.0.0",
                PlayerName = $"Player_{UnityEngine.Random.Range(100, 999)}"
            };

            _networkService.SendMessage(handshake);
            _logger?.Log(LogLevel.Info, "NetworkTest", "Sent handshake as {0}", handshake.PlayerName);
        }

        private void HandleClientConnected(int clientId)
        {
            int generation = _sessionGeneration;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (this == null || generation != _sessionGeneration) return;
                UpdateStatus($"Client {clientId} connected");
                _logger?.Log(LogLevel.Info, "NetworkTest", "Client {0} connected", clientId);
            });
        }

        private void HandleClientDisconnected(int clientId)
        {
            int generation = _sessionGeneration;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (this == null || generation != _sessionGeneration) return;
                UpdateStatus($"Client {clientId} disconnected");
                _logger?.Log(LogLevel.Info, "NetworkTest", "Client {0} disconnected", clientId);
                if (clientId == 0)
                    ResetSession();
                else
                    RemovePlayer(clientId);
            });
        }

        private void HandleConnectionFailed(string reason)
        {
            int generation = _sessionGeneration;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (this == null || generation != _sessionGeneration) return;
                UpdateStatus($"Connection failed: {reason}");
                _logger?.Log(LogLevel.Error, "NetworkTest", "Connection failed: {0}", reason);
                ResetSession();
            });
        }

        private void HandleHandshake(HandshakeMessage handshake, int senderId)
        {
            _logger?.Log(LogLevel.Info, "NetworkTest", "Handshake from Player {0}: {1}", 
                senderId, handshake.PlayerName);

            if (_networkService.IsServer)
            {
                var response = new HandshakeMessage
                {
                    TargetId = senderId,
                    ClientVersion = "1.0.0",
                    PlayerName = "Server"
                };
                _networkService.SendMessage(response);
            }
            else
            {
                if (senderId == 0 && handshake.TargetId != -1)
                {
                    if (_localPlayerId == -1)
                    {
                        _localPlayerId = handshake.TargetId;
                        _logger?.Log(LogLevel.Info, "NetworkTest", "Assigned Player ID: {0}", _localPlayerId);
            
                        if (_spawnedPlayers.ContainsKey(_localPlayerId))
                        {
                            RemovePlayer(_localPlayerId);
                        }
            
                        SpawnPlayer(_localPlayerId, true);
                        FlushPendingPlayerJoins();
                    }
                }
            }
        }

        private void SpawnPlayer(int playerId, bool isLocal, Vector3? requestedSpawnPosition = null)
        {
            _logger?.Log(LogLevel.Debug, "NetworkTest", "SpawnPlayer: id={0}, isLocal={1}", playerId, isLocal);
            
            if (_spawnedPlayers.ContainsKey(playerId))
            {
                _logger?.Log(LogLevel.Warning, "NetworkTest", "Player {0} already spawned!", playerId);
                return;
            }

            if (playerPrefab == null)
            {
                _logger?.Log(LogLevel.Error, "NetworkTest", "Player prefab not assigned!");
                return;
            }

            Vector3 spawnPos = requestedSpawnPosition
                ?? GameConstants.Map.SpawnPositions[playerId % GameConstants.Map.SpawnPositions.Length];

            GameObject playerObj = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            
            var player = playerObj.GetComponent<Player.Player>();

            if (player != null)
            {
                player.SetPlayerId(playerId);
                player.SetLocalPlayer(isLocal);
                
                _spawnedPlayers[playerId] = playerObj;
                
                _logger?.Log(LogLevel.Info, "NetworkTest", "{0} Player {1} spawned at {2}", 
                    isLocal ? "LOCAL" : "REMOTE", playerId, spawnPos);
            }
            else
            {
                _logger?.Log(LogLevel.Error, "NetworkTest", "Player component not found on prefab!");
                Destroy(playerObj);
            }
        }

        private void RemovePlayer(int playerId)
        {
            if (_spawnedPlayers.TryGetValue(playerId, out var playerObj))
            {
                if (playerObj != null)
                {
                    Destroy(playerObj);
                }
                _spawnedPlayers.Remove(playerId);
                
                _logger?.Log(LogLevel.Info, "NetworkTest", "Player {0} removed", playerId);
            }
        }
        
        private void OnPlayerJoinedMessage(INetworkMessage message, int senderId)
        {
            if (message is PlayerJoinedMessage joinMsg)
                HandlePlayerJoined(joinMsg);
        }

        private void OnPlayerLeftMessage(INetworkMessage message, int senderId)
        {
            if (message is PlayerLeftMessage leftMsg)
                HandlePlayerLeft(leftMsg);
        }

        private void HandlePlayerJoined(PlayerJoinedMessage joinMsg)
        {
            _logger?.Log(LogLevel.Info, "NetworkTest", "Player {0} joined at {1}", 
                joinMsg.PlayerId, joinMsg.SpawnPosition);
    
            if (_networkService.LocalClientId == -1)
            {
                if (!_pendingPlayerJoins.Exists(pending => pending.PlayerId == joinMsg.PlayerId))
                {
                    _pendingPlayerJoins.Add(joinMsg);
                }

                _logger?.Log(LogLevel.Debug, "NetworkTest",
                    "Queued PlayerJoined for {0} until LocalClientId is assigned", joinMsg.PlayerId);
                return;
            }
    
            if (joinMsg.PlayerId == _networkService.LocalClientId)
            {
                return;
            }
    
            if (_spawnedPlayers.ContainsKey(joinMsg.PlayerId))
            {
                _logger?.Log(LogLevel.Warning, "NetworkTest", "Player {0} already exists!", joinMsg.PlayerId);
                return;
            }
    
            SpawnPlayer(joinMsg.PlayerId, false, joinMsg.SpawnPosition);
        }

        private void FlushPendingPlayerJoins()
        {
            if (_networkService == null || _networkService.LocalClientId == -1)
                return;

            foreach (var pendingJoin in _pendingPlayerJoins)
            {
                if (pendingJoin.PlayerId == _networkService.LocalClientId)
                    continue;

                if (!_spawnedPlayers.ContainsKey(pendingJoin.PlayerId))
                {
                    SpawnPlayer(pendingJoin.PlayerId, false, pendingJoin.SpawnPosition);
                }
            }

            _pendingPlayerJoins.Clear();
        }

        private void HandlePlayerLeft(PlayerLeftMessage leftMsg)
        {
            _logger?.Log(LogLevel.Info, "NetworkTest", "Player {0} left", leftMsg.PlayerId);
            RemovePlayer(leftMsg.PlayerId);
        }

        private void UpdateStatus(string status)
        {
            if (statusText != null)
            {
                statusText.text = $"Status: {status}";
            }
            
            _logger?.Log(LogLevel.Info, "NetworkTest", status);
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromNetworkEvents();
            ClearPlayers();
        }

        private void ResetSession()
        {
            _sessionGeneration++;
            _wasConnected = false;
            ClearPlayers();
            if (connectPanel != null) connectPanel.SetActive(true);
            EnableButtons();
        }

        private void ClearPlayers()
        {
            foreach (var kvp in _spawnedPlayers)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.SetActive(false);
                    Destroy(kvp.Value);
                }
            }
            _spawnedPlayers.Clear();
            _pendingPlayerJoins.Clear();
            _localPlayerId = -1;
        }
        
        private void DisableButtons()
        {
            if (serverButton != null) serverButton.interactable = false;
            if (clientButton != null) clientButton.interactable = false;
        }

        private void EnableButtons()
        {
            if (serverButton != null) serverButton.interactable = true;
            if (clientButton != null) clientButton.interactable = true;
        }
    }
}
