using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Arena.Combat;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Core.Utilities;
using Arena.Gameplay;
using Arena.Logging;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Player.States;
using UnityEngine;
using UnityEngine.UI;
using ArenaPlayer = Arena.Player.Player;
using Object = UnityEngine.Object;

namespace Arena.Verification
{
    public sealed class GameplayRegressionAgent : MonoBehaviour
    {
        private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _objects = new();
        private readonly VerificationLog _logger = new();
        private VerificationLog _unityLogs;
        private int _passed;
        private int _failed;
        private GameObject _playerPrefab;
        private NetworkConfig _config;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!Environment.GetCommandLineArgs().Contains("-arena-regression")) return;
            new GameObject(nameof(GameplayRegressionAgent)).AddComponent<GameplayRegressionAgent>();
        }

        private IEnumerator Start()
        {
            _unityLogs = new VerificationLog(true);
            yield return null;
            yield return null;
            yield return VerifyDispatcherStartup();
            try
            {
                _config = GameInstaller.Container.Resolve<NetworkConfig>();
                _playerPrefab = Get<GameObject>(FindAnyObjectByType<NetworkTestManager>(), "playerPrefab");
                VerifyAssertions();
                Run("local snapshot health and position reject stale/duplicate", () => VerifySnapshot(true));
                Run("remote snapshot health and target reject stale/duplicate", () => VerifySnapshot(false));
                Run("snapshot wrap and wrong sender/player", VerifySnapshotWrap);
                Run("duplicate installer preserves owner services", () => VerifyInstaller(false));
                Run("owner installer releases its services", () => VerifyInstaller(true));
                Run("server EOF clears players, ID, pending joins and restores UI", VerifyClientDisconnect);
                Run("new handshake recreates players after session cleanup", VerifyNewHandshake);
                Run("failed connection clears gameplay state", VerifyConnectionFailure);
                Run("manual disconnect clears gameplay state", VerifyManualDisconnect);
                Run("server peer disconnect removes only that peer", VerifyPeerDisconnect);
                Run("queued old disconnect cannot clear new session", VerifyQueuedDisconnect);
                Run("plain packets round-trip and retired compression flag is rejected", VerifyPacketFormat);
                Run("fragment creation from 80 concurrent producers", VerifyConcurrentFragments);
                Run("fragment sequence wraps uint", VerifyFragmentWrap);
                Run("same low16 group ID remains separate", VerifyFragmentGroupReuse);
                Run("maximum ushort fragment count terminates", VerifyMaximumFragmentCount);
                Run("destroy-before-spawn and duplicate destroy", VerifyDestroyBeforeSpawn);
                Run("duplicate spawn and lifetime fallback", VerifyBulletLifetime);
                Run("bullet tombstones are bounded and cleared per session", VerifyBulletRetention);
            }
            catch (Exception ex)
            {
                _failed++;
                Debug.LogError($"REGRESSION|FAIL|startup|{ex}");
            }
            foreach (var obj in _objects)
                if (obj != null) Destroy(obj);
            yield return null;
            try { _logger.ThrowIfErrors(); _unityLogs.ThrowIfErrors(); }
            catch (Exception ex) { _failed++; Debug.LogError($"REGRESSION|UNEXPECTED_ERROR|{ex}"); }
            Debug.Log($"REGRESSION|{(_failed == 0 ? "PASS" : "FAIL")}|cases={_passed + _failed}|passed={_passed}|failed={_failed}");
            Application.Quit(_failed == 0 ? 0 : 1);
        }

        private void Run(string name, Action test)
        {
            try { test(); _logger.ThrowIfErrors(); _passed++; Debug.Log($"REGRESSION|CASE_PASS|{name}"); }
            catch (Exception ex) { _failed++; Debug.LogError($"REGRESSION|CASE_FAIL|{name}|{ex}"); }
        }

        private IEnumerator VerifyDispatcherStartup()
        {
            var saved = GameInstaller.Container;
            var property = typeof(GameInstaller).GetProperty("Container");
            var dispatcher = UnityMainThreadDispatcher.Instance;
            var root = NewObject("DispatcherStartup");
            var installer = root.AddComponent<GameInstaller>();
            Set(installer, "networkConfig", saved.Resolve<NetworkConfig>());
            property.SetValue(null, null);
            try
            {
                root.SetActive(true);
                yield return null;
                Run("pre-existing dispatcher preserves installer root", () =>
                {
                    Require(root != null && installer != null, "dispatcher destroyed installer root");
                    Require(GameInstaller.Container != null && !ReferenceEquals(saved, GameInstaller.Container),
                        "installer did not create its container");
                    Require(ReferenceEquals(dispatcher, GameInstaller.Container.Resolve<UnityMainThreadDispatcher>()),
                        "installer registered a duplicate dispatcher");
                    Require(GameInstaller.Container.Resolve<INetworkService>() != null, "network service missing");
                });
            }
            finally
            {
                if (root != null) Destroy(root);
            }
            yield return null;
            property.SetValue(null, saved);
        }

        private GameObject NewObject(string name)
        {
            var obj = new GameObject("Regression_" + name);
            obj.SetActive(false);
            _objects.Add(obj);
            return obj;
        }

        private ArenaPlayer NewPlayer(bool local)
        {
            var player = NewObject("Player").AddComponent<ArenaPlayer>();
            Set(player, "playerId", 7);
            Set(player, "isLocalPlayer", local);
            Set(player, "networkConfig", _config);
            Set(player, "<Health>k__BackingField", 100f);
            Set(player, "<IsAlive>k__BackingField", true);
            Set(player, "_localState", new LocalPlayerState(player, null, _logger, _config));
            Set(player, "_remoteState", new RemotePlayerState(player, _logger, _config));
            Set(player, "_deadState", new DeadPlayerState(player, _logger));
            return player;
        }

        private static PlayerStateMessage Snapshot(uint seq, float health, float x, bool alive = true)
        {
            return new PlayerStateMessage { PlayerId = 7, TransportSeqNumber = seq, Health = health,
                Position = new Vector3(x, 0, 0), Rotation = Quaternion.identity, IsAlive = alive };
        }

        private static void Apply(ArenaPlayer player, PlayerStateMessage state, int sender = 0)
        {
            Call(player, "OnPlayerStateMessage", state, sender);
        }

        private void VerifySnapshot(bool local, bool invalidateGuard = false)
        {
            var player = NewPlayer(local);
            Apply(player, Snapshot(20, 75, 10));
            Require(player.Health == 75 && player.transform.position.x == 10, "fresh state not applied");
            if (invalidateGuard) Set(player, "_hasAcceptedSnapshot", false);
            Apply(player, Snapshot(19, 100, -10));
            Apply(player, Snapshot(20, 0, -20, false));
            Require(player.Health == 75 && player.IsAlive, "stale health/death was applied");
            Require(player.transform.position.x == 10, "stale position was applied");
            if (!local)
                Require(Get<Vector3>(Get<RemotePlayerState>(player, "_remoteState"), "_targetPosition").x == 10,
                    "stale interpolation target was applied");
            Apply(player, Snapshot(21, 50, 12));
            Require(player.Health == 50 && player.transform.position.x == (local ? 12 : 10), "next state rejected");
        }

        private void VerifySnapshotWrap()
        {
            var player = NewPlayer(false);
            Apply(player, Snapshot(uint.MaxValue, 75, 10));
            Apply(player, Snapshot(0, 50, 20));
            Apply(player, Snapshot(uint.MaxValue, 100, 0));
            Apply(player, Snapshot(2, 1, 0), 9);
            var other = Snapshot(2, 1, 0); other.PlayerId = 8;
            Apply(player, other);
            Require(player.Health == 50 && player.transform.position.x == 20, "wrap or source guard failed");
            Apply(player, Snapshot(1, 0, 20, false));
            Require(!player.IsAlive && player.Health == 0, "new death not applied");
        }

        private void VerifyInstaller(bool owner)
        {
            var saved = GameInstaller.Container;
            var container = new DIContainer();
            var service = new TestService(_logger);
            container.RegisterSingleton<INetworkService, TestService>(service);
            var property = typeof(GameInstaller).GetProperty("Container");
            var installer = NewObject("Installer").AddComponent<GameInstaller>();
            try
            {
                property.SetValue(null, container);
                if (owner) Set(installer, "_ownedContainer", container);
                else Call(installer, "Awake");
                Call(installer, "OnDestroy");
                Require(owner ? GameInstaller.Container == null : ReferenceEquals(GameInstaller.Container, container),
                    "container ownership failed");
                Require(service.DisconnectCount == (owner ? 1 : 0), "wrong service disconnected");
            }
            finally { property.SetValue(null, saved); }
        }

        private NetworkTestManager NewManager(out TestService service)
        {
            var manager = NewObject("NetworkTestManager").AddComponent<NetworkTestManager>();
            service = new TestService(_logger) { IsConnected = true, LocalClientId = 7 };
            Set(manager, "_networkService", service);
            Set(manager, "_logger", _logger);
            Set(manager, "_localPlayerId", 7);
            Set(manager, "_wasConnected", true);
            Set(manager, "playerPrefab", _playerPrefab);
            Set(manager, "connectPanel", NewObject("ConnectPanel"));
            var serverButton = NewObject("ServerButton").AddComponent<Button>();
            var clientButton = NewObject("ClientButton").AddComponent<Button>();
            serverButton.interactable = clientButton.interactable = false;
            Set(manager, "serverButton", serverButton);
            Set(manager, "clientButton", clientButton);
            Players(manager).Add(7, NewObject("OldLocal"));
            Players(manager).Add(8, NewObject("OldRemote"));
            Get<List<PlayerJoinedMessage>>(manager, "_pendingPlayerJoins").Add(new PlayerJoinedMessage { PlayerId = 9 });
            Call(manager, "SubscribeToNetworkEvents");
            return manager;
        }

        private static Dictionary<int, GameObject> Players(NetworkTestManager manager) =>
            Get<Dictionary<int, GameObject>>(manager, "_spawnedPlayers");

        private static void CheckReset(NetworkTestManager manager)
        {
            Require(Players(manager).Count == 0 && Get<int>(manager, "_localPlayerId") == -1, "old players/ID remain");
            Require(Get<List<PlayerJoinedMessage>>(manager, "_pendingPlayerJoins").Count == 0, "pending joins remain");
            Require(Get<GameObject>(manager, "connectPanel").activeSelf, "connect UI hidden");
            Require(Get<Button>(manager, "serverButton").interactable && Get<Button>(manager, "clientButton").interactable,
                "connect buttons disabled");
        }

        private static void FlushMainThread() => Call(UnityMainThreadDispatcher.Instance, "Update");

        private void VerifyClientDisconnect()
        {
            var manager = NewManager(out var service);
            service.IsConnected = false;
            service.RaiseDisconnected(0);
            FlushMainThread();
            CheckReset(manager);
        }

        private void VerifyNewHandshake()
        {
            var manager = NewManager(out var service);
            service.RaiseDisconnected(0); FlushMainThread();
            service.LocalClientId = 17;
            Get<List<PlayerJoinedMessage>>(manager, "_pendingPlayerJoins").Add(
                new PlayerJoinedMessage { PlayerId = 18, SpawnPosition = Vector3.right });
            service.MessageRouter.RouteMessage(new HandshakeMessage { TargetId = 17 }, 0);
            Require(Get<int>(manager, "_localPlayerId") == 17, "new local ID not assigned");
            Require(Players(manager).Count == 2 && Players(manager).ContainsKey(17) && Players(manager).ContainsKey(18),
                "new players not created");
            Require(Players(manager)[17].GetComponent<ArenaPlayer>().IsLocalPlayer, "local role not restored");
            Call(manager, "ResetSession");
        }

        private void VerifyConnectionFailure()
        {
            var manager = NewManager(out var service);
            service.RaiseFailure(); FlushMainThread(); CheckReset(manager);
            _logger.ConsumeExpected("NetworkTest: Connection failed: regression-injected failure", 1);
        }

        private void VerifyManualDisconnect()
        {
            var manager = NewManager(out var service);
            service.Disconnect(); Call(manager, "Update"); CheckReset(manager);
        }

        private void VerifyPeerDisconnect()
        {
            var manager = NewManager(out var service);
            service.IsServer = true; service.RaiseDisconnected(8); FlushMainThread();
            Require(Players(manager).Count == 1 && Players(manager).ContainsKey(7), "unrelated peer removed");
            Require(Get<int>(manager, "_localPlayerId") == 7, "server peer event reset whole session");
            Call(manager, "ResetSession");
        }

        private void VerifyQueuedDisconnect()
        {
            var manager = NewManager(out var service);
            service.RaiseDisconnected(0);
            Call(manager, "ResetSession");
            Set(manager, "_localPlayerId", 17);
            Players(manager).Add(17, NewObject("NewSessionPlayer"));
            FlushMainThread();
            Require(Get<int>(manager, "_localPlayerId") == 17 && Players(manager).ContainsKey(17), "old callback reset new session");
            Call(manager, "ResetSession");
        }

        private HandshakeMessage LargeMessage(int id) => new HandshakeMessage
        { PlayerName = $"producer-{id}:" + new string((char)('A' + id % 26), 9000), Timestamp = id };

        private void VerifyConcurrentFragments()
        {
            var processor = new PacketProcessor(_logger);
            var originals = Enumerable.Range(0, 80).Select(LargeMessage).ToArray();
            var groups = new NetworkPacket[originals.Length][];
            Parallel.For(0, groups.Length, i => groups[i] = processor.CreatePackets(originals[i]));
            var seen = new HashSet<uint>();
            for (int i = 0; i < groups.Length; i++)
            {
                for (int j = 0; j < groups[i].Length; j++)
                {
                    var packet = groups[i][j];
                    Require(seen.Add(packet.Header.SeqNumber), "duplicate transport seq");
                    Require(unchecked((ushort)(packet.Header.SeqNumber - packet.Header.FragmentId)) == j + 1,
                        "non-contiguous fragment index");
                }
                VerifyReassembly(processor, groups[i], originals[i]);
            }
        }

        private void VerifyFragmentWrap()
        {
            var processor = new PacketProcessor(_logger);
            Set(processor, "_seqNumber", uint.MaxValue - 3);
            var original = LargeMessage(2);
            var packets = processor.CreatePackets(original);
            Require(packets.Any(packet => packet.Header.SeqNumber == 0), "wrap not exercised");
            VerifyReassembly(processor, packets, original);
        }

        private void VerifyFragmentGroupReuse()
        {
            var processor = new PacketProcessor(_logger);
            var first = LargeMessage(1); var second = LargeMessage(2);
            var a = processor.CreatePackets(first);
            Set(processor, "_seqNumber", 65536u);
            var b = processor.CreatePackets(second);
            Require(a[0].Header.FragmentId == b[0].Header.FragmentId, "low16 reuse not exercised");
            Require(processor.ProcessPacket(a[0]) == null && processor.ProcessPacket(b[0]) == null, "premature completion");
            VerifyReassembly(processor, a.Skip(1).ToArray(), first, false);
            VerifyReassembly(processor, b.Skip(1).ToArray(), second, false);
        }

        private static void VerifyReassembly(PacketProcessor processor, NetworkPacket[] packets,
            HandshakeMessage expected, bool duplicateLast = true)
        {
            INetworkMessage completed = null;
            foreach (var packet in packets.Reverse())
            {
                var result = processor.ProcessPacket(packet);
                if (result != null) { Require(completed == null, "multiple completions"); completed = result; }
                if (duplicateLast && packet == packets[packets.Length - 1] && completed == null)
                    Require(processor.ProcessPacket(packet) == null, "duplicate last completed early");
            }
            Require(completed != null && completed.Serialize().SequenceEqual(expected.Serialize()), "reassembled bytes differ");
        }

        private void VerifyMaximumFragmentCount()
        {
            var processor = new PacketProcessor(_logger);
            var expected = LargeMessage(3);
            var payload = expected.Serialize();
            INetworkMessage completed = null;
            for (int i = 1; i <= ushort.MaxValue; i++)
            {
                var result = processor.ProcessPacket(new NetworkPacket
                {
                    Header = new PacketHeader { ClientId = 1, FragmentId = 1, SeqNumber = (uint)i + 1,
                        Flags = PacketFlags.Fragment | (i == ushort.MaxValue ? PacketFlags.LastFragment : PacketFlags.None) },
                    Payload = i == 1 ? payload : Array.Empty<byte>()
                });
                if (result != null) completed = result;
            }
            Require(completed != null && completed.Serialize().SequenceEqual(payload), "maximum group failed");
        }

        private void VerifyPacketFormat()
        {
            var processor = new PacketProcessor(_logger);
            var original = new HandshakeMessage { PlayerName = "packet-format", Timestamp = 42 };
            var packets = processor.CreatePackets(original);
            Require(packets.Length == 1, "small message was fragmented");
            Require(packets[0].Payload.SequenceEqual(original.Serialize()), "payload was transformed");
            var restored = processor.ProcessPacket(packets[0]);
            Require(restored != null && restored.Serialize().SequenceEqual(original.Serialize()), "plain packet round-trip failed");

            packets[0].Header.Flags |= PacketFlags.ReservedCompression;
            Require(processor.ProcessPacket(packets[0]) == null, "retired flag accepted on single packet");
            var large = LargeMessage(4);
            var fragments = processor.CreatePackets(large);
            foreach (var fragment in fragments)
            {
                Require(!fragment.Header.Flags.HasFlag(PacketFlags.ReservedCompression), "retired flag emitted");
                fragment.Header.Flags |= PacketFlags.ReservedCompression;
                Require(processor.ProcessPacket(fragment) == null, "retired flag accepted on fragment");
                fragment.Header.Flags &= ~PacketFlags.ReservedCompression;
            }
            VerifyReassembly(processor, fragments, large);
        }

        private ClientBulletManager NewBulletManager()
        {
            var manager = NewObject("BulletManager").AddComponent<ClientBulletManager>();
            var prefab = NewObject("BulletPrefab");
            var bullet = prefab.AddComponent<Bullet>();
            Set(bullet, "rb", prefab.GetComponent<Rigidbody>());
            Set(manager, "bulletPrefab", prefab);
            Set(manager, "_logger", _logger);
            Set(manager, "_networkConfig", _config);
            return manager;
        }

        private static BulletSpawnMessage Spawn(uint id) => new BulletSpawnMessage
        { BulletId = id, OwnerId = 7, Direction = Vector3.forward, Speed = 20, Damage = 25 };

        private static Dictionary<uint, GameObject> Bullets(ClientBulletManager manager) =>
            Get<Dictionary<uint, GameObject>>(manager, "_networkBullets");

        private void VerifyDestroyBeforeSpawn()
        {
            var manager = NewBulletManager();
            Call(manager, "HandleBulletDestroy", new BulletDestroyMessage { BulletId = 4 });
            Call(manager, "HandleBulletDestroy", new BulletDestroyMessage { BulletId = 4 });
            Call(manager, "HandleBulletSpawn", Spawn(4));
            Require(Bullets(manager).Count == 0 && Get<HashSet<uint>>(manager, "_processedDestroys").Count == 1,
                "late spawn resurrected bullet or duplicate destroy changed set");
        }

        private void VerifyBulletLifetime()
        {
            var manager = NewBulletManager();
            Call(manager, "HandleBulletSpawn", Spawn(5));
            var spawned = Bullets(manager)[5]; _objects.Add(spawned);
            Call(manager, "HandleBulletSpawn", Spawn(5));
            Require(Bullets(manager).Count == 1 && Bullets(manager)[5] == spawned, "duplicate spawn replaced bullet");
            Get<Dictionary<uint, float>>(manager, "_bulletExpireAt")[5] = Time.time - 1;
            Call(manager, "ExpireStaleBullets");
            Call(manager, "HandleBulletSpawn", Spawn(5));
            Require(Bullets(manager).Count == 0 && !spawned.activeSelf, "expired bullet remained or respawned");
        }

        private void VerifyBulletRetention()
        {
            var manager = NewBulletManager();
            for (uint i = 0; i < 2049; i++) Call(manager, "TryMarkDestroyed", i);
            var records = Get<HashSet<uint>>(manager, "_processedDestroys");
            Require(records.Count == 2048 && !records.Contains(0) && records.Contains(2048), "tombstone bound failed");
            Call(manager, "ClearSessionState");
            Require(records.Count == 0, "session retained bullet IDs");
        }

        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Fields).Invoke(target, args);
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class TestService : INetworkService
        {
            public bool IsConnected { get; set; }
            public bool IsServer { get; set; }
            public int LocalClientId { get; set; }
            public int DisconnectCount { get; private set; }
            public IMessageRouter MessageRouter { get; }
            public event Action<int> OnClientConnected;
            public event Action<int> OnClientDisconnected;
            public event Action<string> OnConnectionFailed;
            public TestService(IGameLogger logger) { MessageRouter = new MessageRouter(logger); }
            public Task StartServerAsync(int port) { IsServer = true; IsConnected = true; return Task.CompletedTask; }
            public Task ConnectToServerAsync(string ip, int port)
            { IsConnected = true; OnClientConnected?.Invoke(0); return Task.CompletedTask; }
            public void SendMessage(INetworkMessage message) { }
            public void Disconnect() { IsConnected = false; DisconnectCount++; }
            public void RaiseDisconnected(int id) => OnClientDisconnected?.Invoke(id);
            public void RaiseFailure() => OnConnectionFailed?.Invoke("regression-injected failure");
        }

        private void VerifyAssertions()
        {
            VerificationChecks.MustReject(() => VerifySnapshot(true, true), "snapshot guard invalidated");
            using var probe = new VerificationLog();
            probe.LogException(new InvalidOperationException("injected"), "oracle");
            VerificationChecks.MustReject(probe.ThrowIfErrors, "swallowed exception");
            probe.ThrowIfErrors();
            Debug.Log("REGRESSION|ORACLE_PASS|snapshot guard invalidation and exception detection");
        }

        private void OnDestroy() => _unityLogs?.Dispose();
    }
}
