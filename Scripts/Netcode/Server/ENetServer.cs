using LiteNetLib;
using LiteNetLib.Utils;
using Thread = System.Threading.Thread;

namespace GodotModules.Netcode.Server
{
    using Event = ENet.Event;

    public abstract class ENetServer
    {
        protected static readonly Dictionary<ClientPacketOpcode, APacketClient> HandlePacket = ReflectionUtils.LoadInstances<ClientPacketOpcode, APacketClient>("CPacket");

        // thread safe props
        public bool HasSomeoneConnected => Interlocked.Read(ref _someoneConnected) == 1;
        public bool IsRunning => Interlocked.Read(ref _running) == 1;
        public readonly ConcurrentQueue<ENetServerCmd> ENetCmds = new();
        private readonly ConcurrentQueue<ServerPacket> _outgoing = new();

        protected readonly Dictionary<int, NetPeer> Peers = new();
        protected CancellationTokenSource CancellationTokenSource = new();
        protected bool _queueRestart { get; set; }

        private long _someoneConnected = 0;
        private long _running = 0;
        private readonly Net _networkManager;

        public ENetServer(Net networkManager)
        {
            _networkManager = networkManager;
        }

        public async Task StartAsync(ushort port, int maxClients, CancellationTokenSource cts)
        {
            try
            {
                if (IsRunning)
                {
                    Logger.Log("Server is running already");
                    return;
                }

                _running = 1;
                CancellationTokenSource = cts;

                using var task = Task.Run(() => ENetThreadWorker(port, maxClients), CancellationTokenSource.Token);
                await task;
            }
            catch (Exception e)
            {
                Logger.LogErr(e, "Server");
            }
        }

        public void KickAll(DisconnectOpcode opcode)
        {
            Peers.Values.ForEach(peer => peer.Disconnect());
            Peers.Clear();
        }

        public void Kick(int id, DisconnectOpcode opcode)
        {
            Peers[id].Disconnect();
            Peers.Remove(id);
        }

        public void Stop() => ENetCmds.Enqueue(new ENetServerCmd(ENetServerOpcode.Stop));
        public async Task StopAsync()
        {
            Stop();

            while (IsRunning)
                await Task.Delay(1);
        }
        public void Restart() => ENetCmds.Enqueue(new ENetServerCmd(ENetServerOpcode.Restart));

        public void Send(ServerPacketOpcode opcode, params NetPeer[] peers) => Send(opcode, null, DeliveryMethod.ReliableOrdered, peers);
        public void Send(ServerPacketOpcode opcode, APacket data, params NetPeer[] peers) => Send(opcode, data, DeliveryMethod.ReliableOrdered, peers);
        public void Send(ServerPacketOpcode opcode, DeliveryMethod flags = DeliveryMethod.ReliableOrdered, params NetPeer[] peers) => Send(opcode, null, flags, peers);
        public void Send(ServerPacketOpcode opcode, APacket data, DeliveryMethod flags = DeliveryMethod.ReliableOrdered, params NetPeer[] peers) => _outgoing.Enqueue(new ServerPacket((byte)opcode, flags, data, peers));

        protected NetPeer[] GetOtherPeers(int id)
        {
            var otherPeers = new Dictionary<int, NetPeer>(Peers);
            otherPeers.Remove(id);
            return otherPeers.Values.ToArray();
        }

        protected virtual void Started(ushort port, int maxClients) { }
        protected virtual void Connect(ref Event netEvent) { }
        protected virtual void Received(NetPeer peer, PacketReader packetReader, ClientPacketOpcode opcode) { }
        protected virtual void Disconnect(ref Event netEvent) { }
        protected virtual void Timeout(ref Event netEvent) { }
        protected virtual void Leave(ref Event netEvent) { }
        protected virtual void Stopped() { }
        protected virtual void ServerCmds() { }

        private Task ENetThreadWorker(ushort port, int maxClients)
        {
            Log("Starting server");

            var listener = new EventBasedNetListener();
            var server = new NetManager(listener)
            {
                IPv6Enabled = IPv6Mode.Disabled
            };

            server.Start(port);

            listener.ConnectionRequestEvent += request =>
            {
                if (server.ConnectedPeersCount < maxClients)
                    request.AcceptIfKey("SomeConnectionKey");
                else
                    request.Reject();
            };

            listener.PeerConnectedEvent += peer =>
            {
                Log($"We got connection: {peer.EndPoint}");
                //Send(ServerPacketOpcode.Lobby, new SPacketLobby {}, DeliveryMethod.ReliableOrdered, peer);
            };

            while (!CancellationTokenSource.IsCancellationRequested)
            {
                // Server cmds from Godot
                while (ENetCmds.TryDequeue(out ENetServerCmd cmd))
                {
                    switch (cmd.Opcode)
                    {
                        case ENetServerOpcode.Stop:
                            if (CancellationTokenSource.IsCancellationRequested)
                            {
                                Log("Server is in the middle of stopping");
                                break;
                            }

                            KickAll(DisconnectOpcode.Stopping);
                            CancellationTokenSource.Cancel();
                            break;

                        case ENetServerOpcode.Restart:
                            if (CancellationTokenSource.IsCancellationRequested)
                            {
                                Log("Server is in the middle of restarting");
                                break;
                            }

                            KickAll(DisconnectOpcode.Restarting);
                            CancellationTokenSource.Cancel();
                            _queueRestart = true;
                            break;
                    }
                }

                // Outgoing packets to clients
                while (_outgoing.TryDequeue(out ServerPacket packet))
                {
                    foreach (var peer in packet.Peers)
                    {
                        peer.Send(packet.NetDataWriter, packet.DeliveryMethod);
                    }
                }

                server.PollEvents();
                Thread.Sleep(15);
            }
            
            server.Stop();
            _running = 0;
            Log("Server stopped");

            return Task.FromResult(1);

            /*using var server = new Host();
            var address = new Address
            {
                Port = port
            };

            try
            {
                server.Create(address, maxClients);
            }
            catch (InvalidOperationException e)
            {
                var message = $"A server is running on port {port} already! {e.Message}";
                Logger.LogWarning(message);
                Cleanup();
                return Task.FromResult(1);
            }

            Started(port, maxClients);

            while (!CancellationTokenSource.IsCancellationRequested)
            {
                var polled = false;

                // ENet Cmds
                ServerCmds();

                // Outgoing
                while (_outgoing.TryDequeue(out ServerPacket packet))
                    packet.Peers.ForEach(peer => Send(packet, peer));

                while (!polled)
                {
                    if (server.CheckEvents(out Event netEvent) <= 0)
                    {
                        if (server.Service(15, out netEvent) <= 0)
                            break;

                        polled = true;
                    }

                    var peer = netEvent.Peer;
                    var eventType = netEvent.Type;

                    switch (eventType)
                    {
                        case EventType.Receive:
                            var packet = netEvent.Packet;
                            if (packet.Length > GamePacket.MaxSize)
                            {
                                Logger.LogWarning($"Tried to read packet from client of size {packet.Length} when max packet size is {GamePacket.MaxSize}");
                                packet.Dispose();
                                continue;
                            }

                            var packetReader = new PacketReader(packet);
                            var opcode = (ClientPacketOpcode)packetReader.ReadByte();
                            Received(netEvent.Peer, packetReader, opcode);

                            packetReader.Dispose();
                            break;

                        case EventType.Connect:
                            _someoneConnected = 1;
                            Peers[netEvent.Peer.ID] = netEvent.Peer;
                            Connect(ref netEvent);
                            break;

                        case EventType.Disconnect:
                            Peers.Remove(netEvent.Peer.ID);
                            Disconnect(ref netEvent);
                            Leave(ref netEvent);
                            break;

                        case EventType.Timeout:
                            Peers.Remove(netEvent.Peer.ID);
                            Timeout(ref netEvent);
                            Leave(ref netEvent);
                            break;
                    }
                }
            }

            server.Flush();
            Cleanup();

            if (_queueRestart)
            {
                _queueRestart = false;
                _networkManager.StartServer(port, maxClients, CancellationTokenSource);
            }

            return Task.FromResult(1);*/
        }

        public void Log(object obj) => Logger.Log($"[Server]: {obj}", ConsoleColor.Green);

        private void Cleanup()
        {
            _running = 0;
            Stopped();
        }
    }

    public class ENetServerCmd
    {
        public ENetServerOpcode Opcode { get; set; }
        public object Data { get; set; }

        public ENetServerCmd(ENetServerOpcode opcode, object data = null)
        {
            Opcode = opcode;
            Data = data;
        }
    }

    public enum ENetServerOpcode
    {
        Stop,
        Restart
    }
}