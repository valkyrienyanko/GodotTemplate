using ENet;

namespace GodotModules.Netcode.Client
{
    public class GameClient : ENetClient
    {
        public Dictionary<uint, string> Players { get; set; }

        public GameClient()
        {
            Players = new();
        }

        protected override void Connect(Event netEvent)
        {
            Log("Client connected to server");
        }

        protected override void Timeout(Event netEvent)
        {
            HandlePeerLeave(DisconnectOpcode.Timeout);
            Log("Client connection timeout");
        }

        protected override void Disconnect(Event netEvent)
        {
            HandlePeerLeave((DisconnectOpcode)netEvent.Data);
            Log("Client disconnected from server");
        }

        private void HandlePeerLeave(DisconnectOpcode opcode)
        {
            SceneGameServers.ConnectingToLobby = false;
            SceneGameServers.Disconnected = true;
            Connected = 0;
            DisconnectOpcode = (DisconnectOpcode)opcode;
            NetworkManager.GodotCmds.Enqueue(new GodotCmd(GodotOpcode.ChangeScene, "GameServers"));
            CancelTokenSource.Cancel();
        }
    }
}