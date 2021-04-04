﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using HKMP.Networking.Packet;

namespace HKMP.Networking.Server {
    /**
     * Server that manages connection with clients 
     */
    public class NetServer {
        private readonly object _lock = new object();
        
        private readonly PacketManager _packetManager;
        
        private readonly Dictionary<ushort, NetServerClient> _clients;

        private TcpListener _tcpListener;
        private UdpClient _udpClient;

        private byte[] _leftoverData;

        private event Action<ushort> OnHeartBeat;
        private event Action OnShutdownEvent;

        public bool IsStarted { get; private set; }

        public NetServer(PacketManager packetManager) {
            _packetManager = packetManager;

            _clients = new Dictionary<ushort, NetServerClient>();
        }

        public void RegisterOnClientHeartBeat(Action<ushort> onHeartBeat) {
            OnHeartBeat += onHeartBeat;
        }

        public void RegisterOnShutdown(Action onShutdown) {
            OnShutdownEvent += onShutdown;
        }

        /**
         * Starts the server on the given port
         */
        public void Start(int port) {
            Logger.Info(this, $"Starting NetServer on port {port}");

            IsStarted = true;

            // Initialize TCP listener and UDP client
            _tcpListener = new TcpListener(IPAddress.Any, port);
            _udpClient = new UdpClient(port);

            // Start and begin receiving data on both protocols
            _tcpListener.Start();
            _tcpListener.BeginAcceptTcpClient(OnTcpConnection, null);
            _udpClient.BeginReceive(OnUdpReceive, null);
        }

        /**
         * Callback for when a TCP connection is accepted
         */
        private void OnTcpConnection(IAsyncResult result) {
            // Retrieve the TCP client from the incoming connection
            var tcpClient = _tcpListener.EndAcceptTcpClient(result);

            // Check whether  there already exists a client with the given IP and store its ID
            ushort id = 0;
            var idFound = false;
            foreach (var clientPair in _clients) {
                var netServerClient = clientPair.Value;

                if (netServerClient.HasAddress((IPEndPoint) tcpClient.Client.RemoteEndPoint)) {
                    Logger.Info(this, "A client with the same IP and port already exists, overwriting NetServerClient");

                    // Since it already exists, we now have to disconnect the old one
                    netServerClient.Disconnect();

                    id = clientPair.Key;
                    idFound = true;
                    break;
                }
            }

            // Create new NetServerClient instance
            // If we found an existing ID for the incoming IP-port combination, we use that existing ID and overwrite the old one
            NetServerClient newClient;
            if (idFound) {
                newClient = new NetServerClient(id, tcpClient, _udpClient);
            } else {
                newClient = new NetServerClient(tcpClient, _udpClient);
            }

            newClient.UpdateManager.StartUdpUpdates();
            _clients[newClient.GetId()] = newClient;

            Logger.Info(this,
                $"Accepted TCP connection from {tcpClient.Client.RemoteEndPoint}, assigned ID {newClient.GetId()}");

            // Start listening for new clients again
            _tcpListener.BeginAcceptTcpClient(OnTcpConnection, null);
        }

        /**
         * Callback for when UDP traffic is received
         */
        private void OnUdpReceive(IAsyncResult result) {
            // Initialize default IPEndPoint for reference in data receive method
            var endPoint = new IPEndPoint(IPAddress.Any, 0);

            byte[] receivedData = { };
            try {
                receivedData = _udpClient.EndReceive(result, ref endPoint);
            } catch (Exception e) {
                Logger.Warn(this, $"UDP Receive exception: {e.Message}");
            }
            
            // Immediately start receiving data again
            _udpClient.BeginReceive(OnUdpReceive, null);

            // Figure out which client ID this data is from
            ushort id = 0;
            var idFound = false;
            foreach (var client in _clients.Values) {
                if (client.HasAddress(endPoint)) {
                    id = client.GetId();
                    idFound = true;
                    break;
                }
            }

            if (!idFound) {
                Logger.Warn(this,
                    $"Received UDP data from {endPoint.Address}, but there was no matching known client");

                return;
            }
            
            List<Packet.Packet> packets;

            // Lock the leftover data array for synchronous data handling
            // This makes sure that from another asynchronous receive callback we don't
            // read/write to it in different places
            lock (_lock) {
                packets = PacketManager.HandleReceivedData(receivedData, ref _leftoverData);
            }

            // We received packets from this client, which means they are still alive
            OnHeartBeat?.Invoke(id);

            foreach (var packet in packets) {
                // Create a server update packet from the raw packet instance
                var serverUpdatePacket = new ServerUpdatePacket(packet);
                serverUpdatePacket.ReadPacket();

                _clients[id].UpdateManager.OnReceivePacket(serverUpdatePacket);
                
                // Let the packet manager handle the received data
                _packetManager.HandleServerPacket(id, serverUpdatePacket);
            }
        }

        /**
         * Sends a packet to the client with the given ID over TCP
         */
        public void SendTcp(ushort id, Packet.Packet packet) {
            if (!_clients.ContainsKey(id)) {
                Logger.Info(this, $"Could not find ID {id} in clients, could not send TCP packet");
                return;
            }

            // Make sure that we use a clean packet object every time
            var newPacket = new Packet.Packet(packet.ToArray());
            // Send the newly constructed packet to the client
            _clients[id].SendTcp(newPacket);
        }

        /**
         * Sends a packet to all connected clients over TCP
         */
        public void BroadcastTcp(Packet.Packet packet) {
            foreach (var idClientPair in _clients) {
                // Make sure that we use a clean packet object every time
                var newPacket = new Packet.Packet(packet.ToArray());
                // Send the newly constructed packet to the client
                idClientPair.Value.SendTcp(newPacket);
            }
        }

        /**
         * Stops the server
         */
        public void Stop() {
            // Clean up existing clients
            foreach (var idClientPair in _clients) {
                idClientPair.Value.Disconnect();
            }

            _clients.Clear();
            
            _tcpListener.Stop();
            _udpClient.Close();

            _tcpListener = null;
            _udpClient = null;
            _leftoverData = null;

            IsStarted = false;

            // Invoke the shutdown event to notify all registered parties of the shutdown
            OnShutdownEvent?.Invoke();
        }

        public void OnClientDisconnect(ushort id) {
            if (!_clients.ContainsKey(id)) {
                Logger.Warn(this, $"Disconnect packet received from ID {id}, but client is not in client list");
                return;
            }

            _clients[id].Disconnect();
            _clients.Remove(id);

            Logger.Info(this, $"Client {id} disconnected");
        }

        public ServerUpdateManager GetUpdateManagerForClient(ushort id) {
            if (!_clients.TryGetValue(id, out var netServerClient)) {
                return null;
            }

            return netServerClient.UpdateManager;
        }

        public void SetDataForAllClients(Action<ServerUpdateManager> dataAction) {
            foreach (var netServerClient in _clients.Values) {
                dataAction(netServerClient.UpdateManager);
            }
        }
    }
}