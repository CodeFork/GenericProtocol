﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using ZeroFormatter;

namespace GenericProtocol.Implementation
{
    public class ProtoClient<T> : IClient<T> {
        #region Properties

        public const int ReceiveBufferSize = 1024;
        
        public event ReceivedHandler<T> ReceivedMessage;

        /// <summary>
        /// Automatically reconnect to the Server on Connection interruptions
        /// </summary>
        public bool AutoReconnect { get; set; }

        private IPEndPoint EndPoint { get; }
        private Socket Socket { get; }

        #endregion

        #region ctor
        /// <summary>
        /// Create a new instance of the <see cref="ProtoClient{T}"/>
        /// with the default <see cref="AddressFamily"/> and <see cref="SocketType"/>.
        /// Use <see cref="Start"/> to start and connect the socket.
        /// </summary>
        /// <param name="address">The server's <see cref="IPAddress"/> to connect to</param>
        /// <param name="port">The server's Port to connect to</param>
        public ProtoClient(IPAddress address, int port) :
            this(address, port, AddressFamily.InterNetwork, SocketType.Stream) { }

        /// <summary>
        /// Create a new instance of the <see cref="ProtoClient{T}"/>
        /// Use <see cref="Start"/> to start and connect the socket.
        /// </summary>
        /// <param name="family">The <see cref="AddressFamily"/> 
        /// this <see cref="System.Net.Sockets.Socket"/> should use</param>
        /// <param name="type">The <see cref="SocketType"/> this 
        /// <see cref="System.Net.Sockets.Socket"/> should use</param>
        /// <param name="address">The server's <see cref="IPAddress"/> to connect to</param>
        /// <param name="port">The server's Port to connect to</param>
        public ProtoClient(IPAddress address, int port, AddressFamily family, SocketType type) {
            EndPoint = new IPEndPoint(address, port);
            Socket = new Socket(family, type, ProtocolType.Tcp);
        }
        #endregion

        /// <summary>
        /// Start and Connect to the Server with the set IP Address.
        /// </summary>
        public async Task Start() {
            await Socket.ConnectAsync(EndPoint);
            StartReceiving();
        }

        // Endless Start reading loop
        private async void StartReceiving() {
            // Loop theoretically infinetly
            while (true) {
                try {
                    byte[] bytes = new byte[ReceiveBufferSize];
                    ArraySegment<byte> segment = new ArraySegment<byte>(bytes);
                    int read = await Socket.ReceiveAsync(segment, SocketFlags.None);

                    var message = ZeroFormatterSerializer.Deserialize<T>(segment.Array);

                    ReceivedMessage?.Invoke(EndPoint?.Address, message); // call event
                } catch (SocketException ex) {
                    Console.WriteLine(ex.ErrorCode);
                }
                // Listen again after client connected
            }
        }

        /// <summary>
        /// Shutdown the server and all active clients
        /// </summary>
        public void Stop() {
            Socket.Disconnect(false);
            Socket.Close();
            Socket.Dispose();
        }

        public async Task Send(T message) {
            if (message == null) throw new ArgumentNullException(nameof(message));

            byte[] bytes = ZeroFormatterSerializer.Serialize(message);
            ArraySegment<byte> segment = new ArraySegment<byte>(bytes);
            int written = await Socket.SendAsync(segment, SocketFlags.None);
        }

        public void Dispose() {
            Stop();
        }
    }
}
