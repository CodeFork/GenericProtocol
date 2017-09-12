﻿using System;
using System.Net;
using GenericProtocol.Implementation;

namespace GenericProtocolTest {
    public class Program {
        private static ProtoServer<string> _server;


        private static void Main(string[] args) {
            StartServer();
            StartClient();

            while (true) Console.ReadKey();
        }


        private static void StartClient() {
            var client = new ProtoClient<string>(IPAddress.Parse("127.0.0.1"), 1024);
            client.Start().GetAwaiter().GetResult();
            client.ReceivedMessage += ClientMessageReceived;
            client.Send("Hello Server!").GetAwaiter().GetResult();
        }

        private static void StartServer() {
            _server = new ProtoServer<string>(IPAddress.Any, 1024);
            Console.WriteLine("Starting Server...");
            _server.Start().GetAwaiter().GetResult();
            Console.WriteLine("Server started!");
            _server.ClientConnected += ClientConnected;
            _server.ReceivedMessage += ServerMessageReceived;
        }

        private static void ServerMessageReceived(IPAddress sender, string message) {
            Console.WriteLine($"{sender}: {message}");
        }
        private static void ClientMessageReceived(IPAddress sender, string message) {
            Console.WriteLine($"{sender}: {message}");
        }

        private static async void ClientConnected(IPAddress address) {
            await _server.Send($"Hello {address}!", address);
        }
    }
}
