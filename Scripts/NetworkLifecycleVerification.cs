using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Arena.Logging;
using Arena.Network;
using UnityEditor;
using UnityEngine;

namespace Arena.EditorTools
{
    public static class NetworkLifecycleVerification
    {
        private const string ConfigPath = "Assets/_SOs/NetConfig.asset";
        private const int TimeoutMilliseconds = 5000;
        private const int ProducerCount = 8;
        private const int FramesPerProducer = 1000;
        private const int ImmediateCloseCycles = 20;
        private const uint UdpRegistrationMagic = 0x47525055;

        [MenuItem("Tools/Verification/Network Lifecycle")]
        public static void RunFromMenu()
        {
            Debug.Log(RunSynchronously());
        }

        public static void RunBatch()
        {
            Debug.Log(RunSynchronously());
        }

        private static string RunSynchronously()
        {
            VerifyPayloadAssertions();
            var config = AssetDatabase.LoadAssetAtPath<NetworkConfig>(ConfigPath);
            if (config == null)
                throw new InvalidOperationException($"NetworkConfig not found at {ConfigPath}");

            return Task.Run(() => RunAllAsync(config)).GetAwaiter().GetResult();
        }

        private static async Task<string> RunAllAsync(NetworkConfig config)
        {
            var passedCases = new List<string>();
            var failures = new List<string>();

            await RunCaseAsync(
                "partial server start releases TCP port",
                passedCases,
                failures,
                () => VerifyPartialStartCleanupAsync(config));

            await RunCaseAsync(
                "server shutdown then client transport reuse",
                passedCases,
                failures,
                () => VerifyServerToClientReuseAsync(config));

            await RunCaseAsync(
                "immediate close preserves connected-disconnected ordering",
                passedCases,
                failures,
                () => VerifyImmediateCloseEventOrderingAsync(config));

            await RunCaseAsync(
                "TCP challenge binds one UDP endpoint and rejects replay",
                passedCases,
                failures,
                () => VerifyUdpRegistrationAsync(config));

            await RunCaseAsync(
                "ClientSession concurrent frames remain parseable",
                passedCases,
                failures,
                VerifyConcurrentFrameWritesAsync);

            await RunCaseAsync(
                "ConnectionManager concurrent client frames remain parseable",
                passedCases,
                failures,
                () => VerifyConcurrentManagerWritesAsync(config));

            await RunCaseAsync(
                "bounded TCP queue rejects without caller-side socket blocking",
                passedCases,
                failures,
                VerifyBoundedTcpQueueAsync);

            await RunCaseAsync(
                "ConnectionManager TCP backpressure disconnects without caller blocking",
                passedCases,
                failures,
                () => VerifyBoundedManagerTcpQueueAsync(config));

            await RunCaseAsync(
                "graceful remote close signals one disconnect",
                passedCases,
                failures,
                () => VerifyRemoteCloseAsync(config));

            await RunCaseAsync(
                "manual shutdown does not signal remote disconnect",
                passedCases,
                failures,
                () => VerifyManualShutdownAsync(config));

            await RunCaseAsync(
                "twenty same-port server rebind cycles",
                passedCases,
                failures,
                () => VerifyRestartCyclesAsync(config));

            if (failures.Count > 0)
            {
                foreach (string failure in failures)
                    Debug.LogError($"[NetworkLifecycleVerification] {failure}");

                throw new InvalidOperationException(
                    $"[NetworkLifecycleVerification] FAIL cases={failures.Count}: " +
                    string.Join(" | ", failures));
            }

            return
                $"[NetworkLifecycleVerification] PASS cases={passedCases.Count} " +
                $"sessionFrames={ProducerCount * FramesPerProducer} " +
                $"managerFrames={ProducerCount * FramesPerProducer} " +
                $"eventCycles={ImmediateCloseCycles} udpRegistration=1 " +
                $"backpressure=2 rebinds=20";
        }

        private static async Task VerifyPartialStartCleanupAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var udpBlocker = new UdpClient(AddressFamily.InterNetwork);
            TcpListener tcpProbe = null;

            try
            {
                udpBlocker.Client.ExclusiveAddressUse = true;
                int blockedPort = FindFreeTcpAndUdpPort();
                udpBlocker.Client.Bind(new IPEndPoint(IPAddress.Any, blockedPort));
                tcpProbe = new TcpListener(IPAddress.Any, blockedPort);
                tcpProbe.Start();
                tcpProbe.Stop();
                tcpProbe = null;

                bool startFailed = false;
                try
                {
                    await manager.StartServerAsync(blockedPort, CancellationToken.None);
                }
                catch (NetworkException)
                {
                    startFailed = true;
                }

                Require(startFailed, "server start unexpectedly succeeded with UDP port occupied");
                logger.Clear();

                tcpProbe = new TcpListener(IPAddress.Any, blockedPort);
                tcpProbe.Start();
                tcpProbe.Stop();
                tcpProbe = null;

                udpBlocker.Close();
                using var retryCts = new CancellationTokenSource();
                await manager.StartServerAsync(blockedPort, retryCts.Token);
                await Task.Delay(60);
                retryCts.Cancel();
                manager.Shutdown();
                logger.ThrowIfErrors("partial-start recovery");
            }
            finally
            {
                tcpProbe?.Stop();
                udpBlocker.Close();
                manager.Shutdown();
            }
        }

        private static async Task VerifyServerToClientReuseAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var serverCts = new CancellationTokenSource();
            TcpListener externalServer = null;
            UdpClient externalUdpServer = null;
            TcpClient acceptedClient = null;

            try
            {
                int firstPort = FindFreeTcpAndUdpPort();
                await manager.StartServerAsync(firstPort, serverCts.Token);
                serverCts.Cancel();
                manager.Shutdown();

                externalServer = new TcpListener(IPAddress.Loopback, 0);
                externalServer.Start();
                int externalPort = ((IPEndPoint)externalServer.LocalEndpoint).Port;
                externalUdpServer = new UdpClient(
                    new IPEndPoint(IPAddress.Loopback, externalPort));
                Task<TcpClient> acceptTask = externalServer.AcceptTcpClientAsync();

                await manager.ConnectToServerAsync(
                    IPAddress.Loopback.ToString(),
                    externalPort,
                    CancellationToken.None);
                acceptedClient = await WithTimeoutAsync(acceptTask, TimeoutMilliseconds);

                byte[] udpChallenge = CreateUdpRegistrationPayload(
                    clientId: 701,
                    token: 0x1020304050607080UL);
                await WriteFrameAsync(acceptedClient.GetStream(), udpChallenge);
                UdpReceiveResult registration = await WithTimeoutAsync(
                    externalUdpServer.ReceiveAsync(),
                    TimeoutMilliseconds);
                Require(ByteArraysEqual(registration.Buffer, udpChallenge),
                    "client did not echo the TCP UDP-registration challenge over UDP");
                externalUdpServer.Send(
                    registration.Buffer,
                    registration.Buffer.Length,
                    registration.RemoteEndPoint);

                var packet = new NetworkPacket
                {
                    Header = new PacketHeader
                    {
                        Type = PacketType.Handshake,
                        ClientId = 17,
                        TargetClientId = -1,
                        SeqNumber = 42
                    },
                    Payload = new byte[] { 7, 11, 13, 17 }
                };

                manager.SendPackets(new[] { packet });
                byte[] receivedFrame = await ReadFrameAsync(
                    acceptedClient.GetStream(),
                    TimeoutMilliseconds);
                NetworkPacket receivedPacket = NetworkPacket.Deserialize(
                    receivedFrame,
                    receivedFrame.Length);

                Require(receivedPacket.Header.SeqNumber == 42,
                    $"expected sequence 42, actual {receivedPacket.Header.SeqNumber}");
                Require(receivedPacket.Payload?.Length == 4,
                    "reused client transport did not deliver the expected payload");
                logger.ThrowIfErrors("server-to-client transport reuse");
            }
            finally
            {
                manager.Shutdown();
                acceptedClient?.Close();
                externalUdpServer?.Close();
                externalServer?.Stop();
                serverCts.Dispose();
            }
        }

        private static async Task VerifyImmediateCloseEventOrderingAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var events = new ConcurrentQueue<long>();
            var lifecycleStates = new ConcurrentDictionary<int, int>();
            var lifecycleErrors = new ConcurrentQueue<string>();
            using var serverCts = new CancellationTokenSource();

            manager.OnClientConnected += clientId =>
            {
                if (!lifecycleStates.TryAdd(clientId, 1))
                    lifecycleErrors.Enqueue($"duplicate Connected for Client {clientId}");
                events.Enqueue(((long)clientId << 1) | 0L);
            };
            manager.OnClientDisconnected += clientId =>
            {
                if (!lifecycleStates.TryUpdate(clientId, 2, 1))
                    lifecycleErrors.Enqueue($"Disconnected without one Connected for Client {clientId}");
                events.Enqueue(((long)clientId << 1) | 1L);
            };

            try
            {
                int port = FindFreeTcpAndUdpPort();
                await manager.StartServerAsync(port, serverCts.Token);

                for (int cycle = 0; cycle < ImmediateCloseCycles; cycle++)
                {
                    using var client = new TcpClient { NoDelay = true };
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    client.Close();

                    int expectedEventCount = (cycle + 1) * 2;
                    await WaitUntilAsync(
                        () => events.Count >= expectedEventCount,
                        TimeoutMilliseconds);
                }

                await Task.Delay(200);

                long[] ordered = events.ToArray();
                Require(ordered.Length == ImmediateCloseCycles * 2,
                    $"expected {ImmediateCloseCycles * 2} lifecycle events, actual {ordered.Length}");

                for (int cycle = 0; cycle < ImmediateCloseCycles; cycle++)
                {
                    long connected = ordered[cycle * 2];
                    long disconnected = ordered[cycle * 2 + 1];
                    int connectedId = (int)(connected >> 1);
                    int disconnectedId = (int)(disconnected >> 1);

                    Require((connected & 1L) == 0L,
                        $"cycle {cycle + 1} began with Disconnected");
                    Require((disconnected & 1L) == 1L,
                        $"cycle {cycle + 1} did not end with Disconnected");
                    Require(connectedId == disconnectedId,
                        $"cycle {cycle + 1} id mismatch: {connectedId} then {disconnectedId}");
                }

                Require(lifecycleErrors.IsEmpty,
                    $"invalid lifecycle transition: {string.Join(" | ", lifecycleErrors.ToArray())}");
                Require(lifecycleStates.Count == ImmediateCloseCycles,
                    $"expected {ImmediateCloseCycles} lifecycle states, actual {lifecycleStates.Count}");

                foreach (var state in lifecycleStates)
                {
                    Require(state.Value == 2,
                        $"Client {state.Key} did not reach Disconnected exactly once");
                }
            }
            finally
            {
                serverCts.Cancel();
                manager.Shutdown();
            }
        }

        private static async Task VerifyUdpRegistrationAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var connectedClient = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedSeqs = new ConcurrentQueue<uint>();
            using var serverCts = new CancellationTokenSource();
            TcpClient tcpClient = null;
            UdpClient registeredUdp = null;
            UdpClient replayUdp = null;

            manager.OnClientConnected += clientId => connectedClient.TrySetResult(clientId);
            manager.OnPacketReceived += (packet, senderId) =>
            {
                if (senderId > 0)
                    receivedSeqs.Enqueue(packet.Header.SeqNumber);
            };

            try
            {
                int port = FindFreeTcpAndUdpPort();
                await manager.StartServerAsync(port, serverCts.Token);

                tcpClient = new TcpClient { NoDelay = true };
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                int clientId = await WithTimeoutAsync(
                    connectedClient.Task,
                    TimeoutMilliseconds);

                byte[] challenge = await ReadFrameAsync(
                    tcpClient.GetStream(),
                    TimeoutMilliseconds);
                Require(challenge.Length == 16,
                    $"expected 16-byte UDP challenge, actual {challenge.Length}");
                Require(BitConverter.ToUInt32(challenge, 0) == UdpRegistrationMagic,
                    "UDP challenge magic mismatch");
                Require(BitConverter.ToInt32(challenge, 4) == clientId,
                    "UDP challenge client id mismatch");
                Require(BitConverter.ToUInt64(challenge, 8) != 0,
                    "UDP challenge token was zero");

                registeredUdp = new UdpClient(0);
                registeredUdp.Connect(IPAddress.Loopback, port);
                replayUdp = new UdpClient(0);
                replayUdp.Connect(IPAddress.Loopback, port);

                byte[] invalidChallenge = (byte[])challenge.Clone();
                invalidChallenge[8] ^= 0x01;
                replayUdp.Send(invalidChallenge, invalidChallenge.Length);
                await WaitUntilAsync(
                    () => logger.CountEntriesContaining("Rejected UDP registration") >= 1,
                    TimeoutMilliseconds);

                byte[] invalidEndpointPacket = CreateUdpVerificationPacket(clientId, 99);
                replayUdp.Send(invalidEndpointPacket, invalidEndpointPacket.Length);
                await WaitUntilAsync(
                    () => logger.CountEntriesContaining("UDP packet from unregistered endpoint") >= 1,
                    TimeoutMilliseconds);

                registeredUdp.Send(challenge, challenge.Length);
                UdpReceiveResult confirmation = await WithTimeoutAsync(
                    registeredUdp.ReceiveAsync(),
                    TimeoutMilliseconds);
                Require(ByteArraysEqual(confirmation.Buffer, challenge),
                    "server did not confirm the registered UDP endpoint");
                await WaitUntilAsync(
                    () => logger.CountEntriesContaining("Registered UDP endpoint") >= 1,
                    TimeoutMilliseconds);

                byte[] firstPacket = CreateUdpVerificationPacket(clientId, 1);
                registeredUdp.Send(firstPacket, firstPacket.Length);
                await WaitUntilAsync(
                    () => receivedSeqs.Count >= 1,
                    TimeoutMilliseconds);

                replayUdp.Send(challenge, challenge.Length);
                await WaitUntilAsync(
                    () => logger.CountEntriesContaining("Rejected UDP registration") >= 2,
                    TimeoutMilliseconds);
                byte[] forgedPacket = CreateUdpVerificationPacket(clientId, 2);
                replayUdp.Send(forgedPacket, forgedPacket.Length);
                await WaitUntilAsync(
                    () => logger.CountEntriesContaining("UDP packet from unregistered endpoint") >= 2,
                    TimeoutMilliseconds);

                byte[] secondPacket = CreateUdpVerificationPacket(clientId, 3);
                registeredUdp.Send(secondPacket, secondPacket.Length);
                await WaitUntilAsync(
                    () => receivedSeqs.Count >= 2,
                    TimeoutMilliseconds);

                uint[] observed = receivedSeqs.ToArray();
                Require(observed.Length == 2,
                    $"expected two registered UDP packets, actual {observed.Length}");
                Require(Array.IndexOf(observed, 1U) >= 0 && Array.IndexOf(observed, 3U) >= 0,
                    "registered endpoint packets were not both delivered");
                Require(Array.IndexOf(observed, 2U) < 0,
                    "replayed UDP endpoint was able to inject a packet");
                logger.ThrowIfErrors("UDP registration");
            }
            finally
            {
                serverCts.Cancel();
                manager.Shutdown();
                replayUdp?.Close();
                registeredUdp?.Close();
                tcpClient?.Close();
            }
        }

        private static async Task VerifyConcurrentFrameWritesAsync()
        {
            var logger = new VerificationLogger();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient sendingClient = null;
            TcpClient receivingClient = null;
            ClientSession session = null;

            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

                sendingClient = new TcpClient { NoDelay = true };
                await sendingClient.ConnectAsync(IPAddress.Loopback, port);
                receivingClient = await WithTimeoutAsync(acceptTask, TimeoutMilliseconds);
                receivingClient.NoDelay = true;
                session = new ClientSession(1, sendingClient, logger);

                Task<HashSet<long>> readerTask = ReadAndValidateFramesAsync(
                    receivingClient.GetStream(),
                    ProducerCount * FramesPerProducer);

                var writers = new Task[ProducerCount];
                for (int producer = 0; producer < ProducerCount; producer++)
                {
                    int producerId = producer;
                    writers[producer] = Task.Run(() =>
                    {
                        for (int seq = 0; seq < FramesPerProducer; seq++)
                        {
                            session.SendTcpFrame(CreateVerificationFrame(producerId, seq));
                        }
                    });
                }

                await WithTimeoutAsync(Task.WhenAll(writers), TimeoutMilliseconds * 2);
                HashSet<long> receivedKeys = await WithTimeoutAsync(
                    readerTask,
                    TimeoutMilliseconds * 2);

                int expectedCount = ProducerCount * FramesPerProducer;
                Require(receivedKeys.Count == expectedCount,
                    $"expected {expectedCount} unique frames, actual {receivedKeys.Count}");
                logger.ThrowIfErrors("ClientSession concurrent writes");
            }
            finally
            {
                session?.Dispose();
                receivingClient?.Close();
                sendingClient?.Close();
                listener.Stop();
            }
        }

        private static async Task VerifyConcurrentManagerWritesAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient acceptedClient = null;

            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

                await manager.ConnectToServerAsync(
                    IPAddress.Loopback.ToString(),
                    port,
                    CancellationToken.None);
                acceptedClient = await WithTimeoutAsync(acceptTask, TimeoutMilliseconds);
                acceptedClient.NoDelay = true;

                Task<HashSet<long>> readerTask = ReadAndValidateNetworkPacketsAsync(
                    acceptedClient.GetStream(),
                    ProducerCount * FramesPerProducer);

                var writers = new Task[ProducerCount];
                for (int producer = 0; producer < ProducerCount; producer++)
                {
                    int producerId = producer;
                    writers[producer] = Task.Run(() =>
                    {
                        for (int seq = 0; seq < FramesPerProducer; seq++)
                        {
                            manager.SendPackets(new[]
                            {
                                CreateVerificationPacket(producerId, seq)
                            });
                        }
                    });
                }

                await WithTimeoutAsync(Task.WhenAll(writers), TimeoutMilliseconds * 2);
                HashSet<long> receivedKeys = await WithTimeoutAsync(
                    readerTask,
                    TimeoutMilliseconds * 2);

                int expectedCount = ProducerCount * FramesPerProducer;
                Require(receivedKeys.Count == expectedCount,
                    $"expected {expectedCount} manager frames, actual {receivedKeys.Count}");
                logger.ThrowIfErrors("ConnectionManager concurrent client writes");
            }
            finally
            {
                manager.Shutdown();
                acceptedClient?.Close();
                listener.Stop();
            }
        }

        private static async Task VerifyBoundedTcpQueueAsync()
        {
            var logger = new VerificationLogger();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient sendingClient = null;
            TcpClient nonReadingClient = null;
            ClientSession session = null;

            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

                sendingClient = new TcpClient { NoDelay = true };
                sendingClient.Client.SendBufferSize = 8 * 1024;
                await sendingClient.ConnectAsync(IPAddress.Loopback, port);
                nonReadingClient = await WithTimeoutAsync(acceptTask, TimeoutMilliseconds);
                nonReadingClient.Client.ReceiveBufferSize = 8 * 1024;
                session = new ClientSession(99, sendingClient, logger);

                byte[] largeFrame = new byte[64 * 1024];
                BitConverter.GetBytes(largeFrame.Length - 4).CopyTo(largeFrame, 0);
                TcpFrameQueueResult result = TcpFrameQueueResult.Queued;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                for (int attempt = 0;
                     attempt < 10000 && result == TcpFrameQueueResult.Queued;
                     attempt++)
                {
                    result = session.TryQueueTcpFrame(largeFrame);
                }

                stopwatch.Stop();
                Require(result == TcpFrameQueueResult.QueueFull,
                    $"expected QueueFull from non-reading peer, actual {result}");
                Require(stopwatch.ElapsedMilliseconds < 2000,
                    $"caller-side queueing took {stopwatch.ElapsedMilliseconds} ms");

                session.Dispose();
                nonReadingClient.Close();
                nonReadingClient = null;
                await WithTimeoutAsync(
                    session.WaitForTcpSendQueueAsync(),
                    TimeoutMilliseconds);

                logger.ConsumeExpectedErrorsContaining(
                    "TCP send queue full",
                    expectedCount: 1);
                logger.ThrowIfErrors("bounded ClientSession TCP queue");
            }
            finally
            {
                session?.Dispose();
                nonReadingClient?.Close();
                sendingClient?.Close();
                listener.Stop();
            }
        }

        private static async Task VerifyBoundedManagerTcpQueueAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            var disconnected = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            TcpClient acceptedClient = null;

            manager.OnClientDisconnected += clientId =>
            {
                if (clientId == 0)
                    disconnected.TrySetResult(true);
            };

            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

                await manager.ConnectToServerAsync(
                    IPAddress.Loopback.ToString(),
                    port,
                    CancellationToken.None);
                acceptedClient = await WithTimeoutAsync(acceptTask, TimeoutMilliseconds);
                acceptedClient.Client.ReceiveBufferSize = 8 * 1024;

                var packet = new NetworkPacket
                {
                    Header = new PacketHeader
                    {
                        Type = PacketType.Handshake,
                        ClientId = 41,
                        TargetClientId = -1,
                        SeqNumber = 1
                    },
                    Payload = new byte[1300]
                };

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                for (int attempt = 0;
                     attempt < 20000 && !disconnected.Task.IsCompleted;
                     attempt++)
                {
                    packet.Header.SeqNumber = (uint)(attempt + 1);
                    manager.SendPackets(new[] { packet });
                }

                await WithTimeoutAsync(disconnected.Task, TimeoutMilliseconds);
                stopwatch.Stop();
                Require(stopwatch.ElapsedMilliseconds < 2000,
                    $"ConnectionManager caller-side queueing took {stopwatch.ElapsedMilliseconds} ms");

                await Task.Delay(100);
                logger.ConsumeExpectedErrorsContaining(
                    "TCP send queue full",
                    expectedCount: 1);
                logger.ThrowIfErrors("bounded ConnectionManager TCP queue");
            }
            finally
            {
                manager.Shutdown();
                acceptedClient?.Close();
                listener.Stop();
            }
        }

        private static async Task VerifyRemoteCloseAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient acceptedClient = null;
            int disconnectCount = 0;

            manager.OnClientDisconnected += clientId =>
            {
                if (clientId == 0)
                    Interlocked.Increment(ref disconnectCount);
            };

            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

                await manager.ConnectToServerAsync(
                    IPAddress.Loopback.ToString(),
                    port,
                    CancellationToken.None);
                acceptedClient = await WithTimeoutAsync(acceptTask, TimeoutMilliseconds);
                acceptedClient.Client.Shutdown(SocketShutdown.Both);
                acceptedClient.Close();
                acceptedClient = null;

                await WaitUntilAsync(
                    () => Volatile.Read(ref disconnectCount) > 0,
                    TimeoutMilliseconds);

                manager.Shutdown();
                await Task.Delay(200);

                Require(Volatile.Read(ref disconnectCount) == 1,
                    $"expected one disconnect event, actual {disconnectCount}");
                logger.ThrowIfErrors("graceful remote close");
            }
            finally
            {
                manager.Shutdown();
                acceptedClient?.Close();
                listener.Stop();
            }
        }

        private static async Task VerifyManualShutdownAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient acceptedClient = null;
            int disconnectCount = 0;

            manager.OnClientDisconnected += clientId =>
            {
                if (clientId == 0)
                    Interlocked.Increment(ref disconnectCount);
            };

            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

                await manager.ConnectToServerAsync(
                    IPAddress.Loopback.ToString(),
                    port,
                    CancellationToken.None);
                acceptedClient = await WithTimeoutAsync(acceptTask, TimeoutMilliseconds);

                manager.Shutdown();
                await Task.Delay(200);

                Require(Volatile.Read(ref disconnectCount) == 0,
                    $"manual shutdown emitted {disconnectCount} remote disconnect events");
                logger.ThrowIfErrors("manual client shutdown");
            }
            finally
            {
                manager.Shutdown();
                acceptedClient?.Close();
                listener.Stop();
            }
        }

        private static async Task VerifyRestartCyclesAsync(NetworkConfig config)
        {
            var logger = new VerificationLogger();
            var manager = new ConnectionManager(config, logger);
            int port = FindFreeTcpAndUdpPort();

            try
            {
                for (int cycle = 0; cycle < 20; cycle++)
                {
                    using var cycleCts = new CancellationTokenSource();
                    await manager.StartServerAsync(port, cycleCts.Token);
                    await Task.Delay(60);
                    cycleCts.Cancel();
                    manager.Shutdown();
                    await Task.Delay(10);
                    logger.ThrowIfErrors($"server rebind cycle {cycle + 1}");
                }
            }
            finally
            {
                manager.Shutdown();
            }
        }

        private static byte[] CreateVerificationFrame(int producerId, int seq)
        {
            int payloadLength = 16 + seq % 37;
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(BitConverter.GetBytes(producerId), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(producerId ^ seq ^ 0x5A5A5A5A), 0, payload, 8, 4);

            for (int index = 12; index < payload.Length; index++)
                payload[index] = (byte)(producerId * 31 + seq * 17 + index);

            var frame = new byte[4 + payload.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, frame, 0, 4);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        private static NetworkPacket CreateVerificationPacket(int producerId, int seq)
        {
            int payloadLength = 16 + seq % 37;
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(BitConverter.GetBytes(producerId), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(producerId ^ seq ^ 0x5A5A5A5A), 0, payload, 8, 4);

            for (int index = 12; index < payload.Length; index++)
                payload[index] = (byte)(producerId * 31 + seq * 17 + index);

            return new NetworkPacket
            {
                Header = new PacketHeader
                {
                    Type = PacketType.Handshake,
                    ClientId = 23,
                    TargetClientId = -1,
                    SeqNumber = (uint)(producerId * FramesPerProducer + seq + 1)
                },
                Payload = payload
            };
        }

        private static byte[] CreateUdpRegistrationPayload(int clientId, ulong token)
        {
            var payload = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(UdpRegistrationMagic), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(clientId), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(token), 0, payload, 8, 8);
            return payload;
        }

        private static byte[] CreateUdpVerificationPacket(int clientId, uint seq)
        {
            var packet = new NetworkPacket
            {
                Header = new PacketHeader
                {
                    Type = PacketType.GameData,
                    ClientId = clientId,
                    TargetClientId = 0,
                    SeqNumber = seq
                },
                Payload = BitConverter.GetBytes(seq)
            };

            return packet.Serialize();
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, frame, 0, 4);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            await stream.WriteAsync(frame, 0, frame.Length);
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }

            return true;
        }

        private static async Task<HashSet<long>> ReadAndValidateFramesAsync(
            NetworkStream stream,
            int expectedCount)
        {
            var received = new HashSet<long>();

            for (int frameIndex = 0; frameIndex < expectedCount; frameIndex++)
            {
                byte[] payload = await ReadFrameAsync(stream, TimeoutMilliseconds);
                ValidatePayload(payload);

                int producerId = BitConverter.ToInt32(payload, 0);
                int seq = BitConverter.ToInt32(payload, 4);
                int marker = BitConverter.ToInt32(payload, 8);

                Require(producerId >= 0 && producerId < ProducerCount,
                    $"invalid producer id {producerId}");
                Require(seq >= 0 && seq < FramesPerProducer,
                    $"invalid producer sequence {seq}");
                Require(marker == (producerId ^ seq ^ 0x5A5A5A5A),
                    $"corrupt frame marker for producer={producerId}, sequence={seq}");

                for (int index = 12; index < payload.Length; index++)
                {
                    byte expected = (byte)(producerId * 31 + seq * 17 + index);
                    Require(payload[index] == expected,
                        $"corrupt frame payload for producer={producerId}, sequence={seq}");
                }

                long key = ((long)producerId << 32) | (uint)seq;
                Require(received.Add(key),
                    $"duplicate frame producer={producerId}, sequence={seq}");
            }

            return received;
        }

        private static async Task<HashSet<long>> ReadAndValidateNetworkPacketsAsync(
            NetworkStream stream,
            int expectedCount)
        {
            var received = new HashSet<long>();

            for (int frameIndex = 0; frameIndex < expectedCount; frameIndex++)
            {
                byte[] frame = await ReadFrameAsync(stream, TimeoutMilliseconds);
                NetworkPacket packet = NetworkPacket.Deserialize(frame, frame.Length);
                byte[] payload = packet.Payload;

                ValidatePayload(payload);

                int producerId = BitConverter.ToInt32(payload, 0);
                int seq = BitConverter.ToInt32(payload, 4);
                int marker = BitConverter.ToInt32(payload, 8);

                Require(producerId >= 0 && producerId < ProducerCount,
                    $"invalid manager producer id {producerId}");
                Require(seq >= 0 && seq < FramesPerProducer,
                    $"invalid manager producer sequence {seq}");
                Require(marker == (producerId ^ seq ^ 0x5A5A5A5A),
                    $"corrupt manager marker for producer={producerId}, sequence={seq}");

                uint expectedPacketSeq =
                    (uint)(producerId * FramesPerProducer + seq + 1);
                Require(packet.Header.SeqNumber == expectedPacketSeq,
                    $"packet sequence mismatch for producer={producerId}, sequence={seq}");

                long key = ((long)producerId << 32) | (uint)seq;
                Require(received.Add(key),
                    $"duplicate manager frame producer={producerId}, sequence={seq}");
            }

            return received;
        }

        private static void ValidatePayload(byte[] payload)
        {
            Require(payload != null && payload.Length >= 16, "missing stress payload");
            int producer = BitConverter.ToInt32(payload, 0);
            int seq = BitConverter.ToInt32(payload, 4);
            Require(producer >= 0 && producer < ProducerCount && seq >= 0 && seq < FramesPerProducer,
                "invalid stress payload identity");
            Require(payload.Length == 16 + seq % 37, "stress payload length differs");
            Require(BitConverter.ToInt32(payload, 8) == (producer ^ seq ^ 0x5A5A5A5A), "stress marker differs");
            for (int index = 12; index < payload.Length; index++)
                Require(payload[index] == (byte)(producer * 31 + seq * 17 + index),
                    $"stress payload differs at byte {index}, producer={producer}, seq={seq}");
        }

        private static void VerifyPayloadAssertions()
        {
            byte[] payload = CreateVerificationPacket(1, 7).Payload;
            ValidatePayload(payload);
            payload[payload.Length - 1] ^= 1;
            Arena.Verification.VerificationChecks.MustReject(() => ValidatePayload(payload), "corrupt payload suffix");
            payload[payload.Length - 1] ^= 1;
            Array.Resize(ref payload, payload.Length - 1);
            Arena.Verification.VerificationChecks.MustReject(() => ValidatePayload(payload), "truncated payload suffix");
            Debug.Log("[TcpPayloadOracle] PASS corrupt and truncated payload negative controls");
        }

        private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, int timeoutMilliseconds)
        {
            var lengthBytes = new byte[4];
            await ReadExactAsync(stream, lengthBytes, timeoutMilliseconds);
            int length = BitConverter.ToInt32(lengthBytes, 0);
            Require(length > 0 && length <= 1024 * 1024,
                $"invalid TCP frame length {length}");

            var payload = new byte[length];
            await ReadExactAsync(stream, payload, timeoutMilliseconds);
            return payload;
        }

        private static async Task ReadExactAsync(
            NetworkStream stream,
            byte[] buffer,
            int timeoutMilliseconds)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await WithTimeoutAsync(
                    stream.ReadAsync(buffer, offset, buffer.Length - offset),
                    timeoutMilliseconds);
                if (read == 0)
                    throw new EndOfStreamException("TCP stream closed before a full frame was received");

                offset += read;
            }
        }

        private static int FindFreeTcpAndUdpPort()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            UdpClient udp = null;

            try
            {
                tcp.Start();
                int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ExclusiveAddressUse = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            finally
            {
                udp?.Close();
                tcp.Stop();
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Condition was not met before the verification timeout");

                await Task.Delay(10);
            }
        }

        private static async Task<T> WithTimeoutAsync<T>(Task<T> task, int timeoutMilliseconds)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeoutMilliseconds));
            if (completed != task)
            {
                ObserveFault(task);
                throw new TimeoutException($"Operation exceeded {timeoutMilliseconds} ms");
            }

            return await task;
        }

        private static async Task WithTimeoutAsync(Task task, int timeoutMilliseconds)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeoutMilliseconds));
            if (completed != task)
            {
                ObserveFault(task);
                throw new TimeoutException($"Operation exceeded {timeoutMilliseconds} ms");
            }

            await task;
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task RunCaseAsync(
            string name,
            ICollection<string> passedCases,
            ICollection<string> failures,
            Func<Task> test)
        {
            try
            {
                await test();
                passedCases.Add(name);
            }
            catch (Exception ex)
            {
                failures.Add($"- {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private sealed class VerificationLogger : IGameLogger
        {
            private readonly ConcurrentQueue<string> _errors = new();
            private readonly ConcurrentQueue<string> _entries = new();

            public void Log(LogLevel level, string category, string message)
            {
                _entries.Enqueue($"{level}/{category}: {message}");
                if (level >= LogLevel.Error)
                    _errors.Enqueue($"{level}/{category}: {message}");
            }

            public void Log(LogLevel level, string category, string message, params object[] args)
            {
                string formatted = string.Format(message, args);
                _entries.Enqueue($"{level}/{category}: {formatted}");
                if (level >= LogLevel.Error)
                    _errors.Enqueue($"{level}/{category}: {formatted}");
            }

            public void LogException(Exception exception, string context = "")
            {
                string formatted = $"{context}: {exception.GetType().Name}: {exception.Message}";
                _entries.Enqueue(formatted);
                _errors.Enqueue(formatted);
            }

            public int CountEntriesContaining(string fragment)
            {
                int count = 0;
                foreach (string entry in _entries.ToArray())
                {
                    if (entry.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                        count++;
                }

                return count;
            }

            public void ConsumeExpectedErrorsContaining(string fragment, int expectedCount)
            {
                int matched = 0;
                var unexpected = new List<string>();

                while (_errors.TryDequeue(out string error))
                {
                    if (error.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                        matched++;
                    else
                        unexpected.Add(error);
                }

                foreach (string error in unexpected)
                    _errors.Enqueue(error);

                if (matched != expectedCount)
                {
                    throw new InvalidOperationException(
                        $"expected {expectedCount} errors containing '{fragment}', actual {matched}");
                }
            }

            public void Clear()
            {
                while (_errors.TryDequeue(out _)) { }
                while (_entries.TryDequeue(out _)) { }
            }

            public void ThrowIfErrors(string context)
            {
                if (_errors.IsEmpty)
                    return;

                var errors = new List<string>();
                while (_errors.TryDequeue(out string error))
                    errors.Add(error);

                throw new InvalidOperationException(
                    $"{context} logged background errors: {string.Join(" | ", errors)}");
            }
        }
    }
}
