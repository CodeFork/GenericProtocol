﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ZeroFormatter;

namespace GenericProtocol.Implementation {
    public class ProtoServer<T> : IServer<T> {
        #region Properties

        public int MaxConnectionsBacklog = Constants.MaxConnectionsBacklog;
        public int ReceiveBufferSize = Constants.ReceiveBufferSize;

        public event ConnectionContextHandler ClientConnected;
        public event ConnectionContextHandler ClientDisconnected;
        public event ReceivedHandler<T> ReceivedMessage;

        private IPEndPoint EndPoint { get; }
        private Socket Socket { get; }
        private IDictionary<IPEndPoint, Socket> Clients { get; }
        #endregion

        #region ctor
        /// <summary>
        /// Create a new instance of the <see cref="ProtoServer{T}"/>
        /// with the default <see cref="AddressFamily"/> and <see cref="SocketType"/>.
        /// Use <see cref="Start"/> to bind and start the socket.
        /// </summary>
        /// <param name="address">The <see cref="IPAddress"/> to start this Protocol on</param>
        /// <param name="port">The Port to start this Protocol on</param>
        public ProtoServer(IPAddress address, int port) :
            this(address, port, AddressFamily.InterNetwork, SocketType.Stream) { }

        /// <summary>
        /// Create a new instance of the <see cref="ProtoServer{T}"/>.
        /// Use <see cref="Start"/> to bind and start the socket.
        /// </summary>
        /// <param name="family">The <see cref="AddressFamily"/> 
        /// this <see cref="System.Net.Sockets.Socket"/> should use</param>
        /// <param name="type">The <see cref="SocketType"/> this 
        /// <see cref="System.Net.Sockets.Socket"/> should use</param>
        /// <param name="address">The <see cref="IPAddress"/> to start this Protocol on</param>
        /// <param name="port">The Port to start this Protocol on</param>
        public ProtoServer(IPAddress address, int port, AddressFamily family, SocketType type) {
            Clients = new Dictionary<IPEndPoint, Socket>();
            EndPoint = new IPEndPoint(address, port);
            Socket = new Socket(family, type, ProtocolType.Tcp);
        }
        #endregion

        #region Functions
        /// <summary>
        /// Bind and Start the Server to the set IP Address.
        /// </summary>
        public void Start(bool seperateThread = false) {
            Socket.Bind(EndPoint);

            if (seperateThread) { // Launch on a new Thread
                new Thread(StartListening).Start();
            } else { // Use Tasks
                StartListening();
            }
        }

        /// <summary>
        /// Shutdown the server and all active clients
        /// </summary>
        public void Stop() {
            foreach (KeyValuePair<IPEndPoint, Socket> kvp in Clients) {
                try {
                    DisconnectClient(kvp.Value);
                } catch {
                    // could not disconnect client
                }
            }
        }

        public async Task Send(T message, IPEndPoint to) {
            if (message == null) throw new ArgumentNullException(nameof(message));

            // Build a byte array of the serialized data
            byte[] bytes = ZeroFormatterSerializer.Serialize(message);
            ArraySegment<byte> segment = new ArraySegment<byte>(bytes);

            // Find socket
            var socket = Clients.FirstOrDefault(c => c.Key.Equals(to)).Value;
            if (socket == null) throw new Exception($"The IP Address {to} could not be found!");

            int size = bytes.Length;
            await SendLeading(size, socket); // send leading size

            // Write buffered
            int written = 0;
            while (written < size) {
                int send = size - written; // current buffer size
                if (send > ReceiveBufferSize)
                    send = ReceiveBufferSize; // max size

                var slice = segment.Slice(written, send); // buffered portion of array
                written = await socket.SendAsync(slice, SocketFlags.None);
            }

            if (written < 1) throw new TransferException($"{written} bytes were sent!");
        }

        public async Task Broadcast(T message) {
            // Build list of Send(..) tasks
            List<Task> tasks = Clients.Select(client => Send(message, client.Key)).ToList();
            // await all
            await Task.WhenAll(tasks);
        }

        public void Dispose() {
            Stop();
            Socket?.Dispose();
        }
        #endregion

        #region Privates

        // Endless Start listening loop
        private async void StartListening() {
            Socket.Listen(10);
            // Loop theoretically infinetly
            while (true) {
                try {
                    var client = await Socket.AcceptAsync(); // Block until accept
                    var endpoint = client.RemoteEndPoint as IPEndPoint; // Get remote endpoint
                    Clients.Add(endpoint, client); // Add client to dictionary

                    StartReading(client); // Start listening for data
                    KeepAlive(client); // Keep client alive and ping

                    ClientConnected?.Invoke(endpoint); // call event
                } catch (SocketException ex) {
                    Console.WriteLine(ex.ErrorCode);
                    //return;
                }
                // Listen again after client connected
            }
        }

        // Endless Start reading loop
        private async void StartReading(Socket client) {
            var endpoint = client.RemoteEndPoint as IPEndPoint; // Get remote endpoint

            // Loop theoretically infinetly
            while (true) {
                try {
                    int size = await ReadLeading(client); // leading "byte"

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

                    if (read < 1) throw new TransferException($"{read} bytes were read!");

                    var message = ZeroFormatterSerializer.Deserialize<T>(segment.Array);

                    ReceivedMessage?.Invoke(endpoint, message); // call event
                } catch (SocketException ex) {
                    Console.WriteLine(ex.ErrorCode);
                    bool success = DisconnectClient(client); // try to disconnect
                    if (success) // Exit Reading loop once successfully disconnected
                        return;
                }
                // Listen again after client connected
            }
        }

        // Keep a Client alive by pinging
        private async void KeepAlive(Socket client) {
            while (true) {
                await Task.Delay(Constants.PingDelay);

                bool isAlive = client.Ping();
                if (isAlive) continue; // Client responded

                // Client does not respond, disconnect & exit
                DisconnectClient(client);
                return;
            }
        }

        // Disconnect a client; returns true if successful
        private bool DisconnectClient(Socket client) {
            IEnumerable<KeyValuePair<IPEndPoint, Socket>> filtered = Clients.Where(c => c.Value == client);
            foreach (KeyValuePair<IPEndPoint, Socket> kvp in filtered) {
                try {
                    kvp.Value.Disconnect(false); // Gracefully disconnect socket
                    kvp.Value.Close();
                    kvp.Value.Dispose();

                    Clients.Remove(kvp.Key); // Remove from collection
                    ClientDisconnected?.Invoke(kvp.Key); // Event
                } catch {
                    // could not disconnect socket
                    return false;
                }
            }
            return true;
        }

        // Read the prefix from a message (number of following bytes)
        private async Task<int> ReadLeading(Socket client) {
            byte[] bytes = new byte[sizeof(int)];
            ArraySegment<byte> segment = new ArraySegment<byte>(bytes);
            // read leading bytes
            int read = await client.ReceiveAsync(segment, SocketFlags.None);

            if (read < 1) throw new TransferException($"{read} lead-bytes were read!");

            // size of the following byte[]
            int size = ZeroFormatterSerializer.Deserialize<int>(segment.Array);
            return size;
        }

        // Send the prefix from a message (number of following bytes)
        private async Task SendLeading(int size, Socket client) {
            // build byte[] out of size
            byte[] bytes = ZeroFormatterSerializer.Serialize(size);
            ArraySegment<byte> segment = new ArraySegment<byte>(bytes);
            // send leading bytes
            int sent = await client.SendAsync(segment, SocketFlags.None);

            if (sent < 1) throw new TransferException($"{sent} lead-bytes were sent!");
        }
        #endregion
    }
}
