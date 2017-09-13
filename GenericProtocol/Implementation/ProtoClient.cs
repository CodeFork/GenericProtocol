﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ZeroFormatter;

namespace GenericProtocol.Implementation {
    public class ProtoClient<T> : IClient<T> {
        #region Properties

        public int ReceiveBufferSize = Constants.ReceiveBufferSize;

        public event ReceivedHandler<T> ReceivedMessage;
        public event ConnectionContextHandler ConnectionLost;

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
        /// <param name="seperateThread">True, if the <see cref="ProtoClient{T}"/>
        /// should be operating on a seperate Thread.</param>
        public async Task Start(bool seperateThread = false) {
            await Socket.ConnectAsync(EndPoint);

            if (seperateThread) { // Launch on a new Thread
                new Thread(StartReceiving).Start();
                new Thread(KeepAlive).Start();
            } else { // Use Tasks
                StartReceiving();
                KeepAlive();
            }
        }

        // Endless Start reading loop
        private async void StartReceiving() {
            // Loop theoretically infinetly
            while (true) {
                try {
                    // Read the leading "byte"
                    int size = await ReadLeading();

                    byte[] bytes = new byte[size];
                    ArraySegment<byte> segment = new ArraySegment<byte>(bytes);
                    // read until all data is read
                    int read = 0;
                    while (read < size) {
                        int receive = size - read; // current buffer size
                        if (receive > ReceiveBufferSize)
                            receive = ReceiveBufferSize; // max size

                        var slice = segment.Slice(read, receive); // get buffered portion of array
                        read += await Socket.ReceiveAsync(slice, SocketFlags.None);
                    }

                    var message = ZeroFormatterSerializer.Deserialize<T>(segment.Array);

                    ReceivedMessage?.Invoke(EndPoint, message); // call event
                } catch (SocketException ex) {
                    Console.WriteLine(ex.ErrorCode);
                }
                // Listen again after client connected
            }
        }

        // Read the prefix from a message (number of following bytes)
        private async Task<int> ReadLeading() {
            byte[] bytes = new byte[sizeof(int)];
            ArraySegment<byte> segment = new ArraySegment<byte>(bytes);
            // read leading bytes
            int read = await Socket.ReceiveAsync(segment, SocketFlags.None);

            if (read < 1) throw new TransferException($"{read} lead-bytes were read!");

            // size of the following byte[]
            int size = ZeroFormatterSerializer.Deserialize<int>(segment.Array);
            return size;
        }

        // Send the prefix from a message (number of following bytes)
        private async Task SendLeading(int size) {
            // build byte[] out of size
            byte[] bytes = ZeroFormatterSerializer.Serialize(size);
            ArraySegment<byte> segment = new ArraySegment<byte>(bytes);
            // send leading bytes
            int sent = await Socket.SendAsync(segment, SocketFlags.None);

            if (sent < 1) throw new TransferException($"{sent} lead-bytes were sent!");
        }

        /// <summary>
        /// Shutdown the server and all active clients
        /// </summary>
        public void Stop() {
            Socket.Disconnect(false);
            Socket.Close();
            Socket.Dispose();
        }

        // Reconnect the Socket connection
        private async Task Reconnect() {
            Socket.Disconnect(true);
            await Start();
        }

        public async Task Send(T message) {
            if (message == null) throw new ArgumentNullException(nameof(message));

            try {
                // build byte[] out of message (serialize with ZeroFormatter)
                byte[] bytes = ZeroFormatterSerializer.Serialize(message);
                ArraySegment<byte> segment = new ArraySegment<byte>(bytes);

                int size = bytes.Length;
                await SendLeading(size); // Send receiver the byte count

                int written = 0;
                while (written < size) {
                    int send = size - written; // current buffer size
                    if (send > ReceiveBufferSize)
                        send = ReceiveBufferSize; // max size

                    var slice = segment.Slice(written, send); // buffered portion of array
                    written = await Socket.SendAsync(slice, SocketFlags.None);
                }

                if (written < 1) throw new TransferException($"{written} bytes were sent!");
            } catch (SocketException ex) {
                Console.WriteLine(ex.ErrorCode);
            }
        }


        private async void KeepAlive() {
            while (true) {
                await Task.Delay(Constants.PingDelay);

                bool isAlive = Socket.Ping();
                if (isAlive) continue; // Client responded

                ConnectionLost?.Invoke(EndPoint);
                // Client does not respond, try reconnecting, or disconnect & exit
                if (AutoReconnect) {
                    await Reconnect();
                } else {
                    Stop();
                }
                return;
            }
        }

        public void Dispose() {
            Stop();
        }
    }
}
