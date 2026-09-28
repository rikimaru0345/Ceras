using System;

namespace NetworkExample
{
    using System.Collections.Generic;
    using Ceras;
    using Ceras.Helpers;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    class ServerClient
    {
        readonly TcpClient _tcpClient;
        readonly NetworkStream _netStream;
        readonly CerasFastSerializer _sendCeras;
        readonly CerasFastSerializer _receiveCeras;

        string _clientName;


        public ServerClient(TcpClient tcpClient)
        {
            _tcpClient = tcpClient;
            _netStream = tcpClient.GetStream();

            // "Learned" types are not a thing any more. The type cache is per-operation state, cleared
            // after every Serialize/Deserialize, so a Type the other side sends is written by name
            // again in each message; and the setting that used to be configured here
            // (c.Advanced.PersistTypeCache) does not affect the wire. What does matter, per
            // CerasRawConfig, is that both ends use the same build of your types.
            _sendCeras    = new CerasFastSerializer();
            _receiveCeras = new CerasFastSerializer();

            StartReceivingMessages();
        }

        void StartReceivingMessages()
        {
            Task.Run(async () =>
            {
                try
                {
                    // Keep receiving packets from the client and respond to them
                    // Eventually when the client disconnects we'll just get an exception and end the thread...
                    while (true)
                    {
                        var obj = await _receiveCeras.ReadFromStream(_netStream);
                        HandleMessage(obj);
                    }
                }
                catch (Exception e)
                {
                    Log($"Error while handling client '{_tcpClient.Client.RemoteEndPoint}': {e}");
                }
            });
        }

        void HandleMessage(object obj)
        {
            if (obj is ClientLogin clientHello)
            {
                _clientName = clientHello.Name;
                Send($"Login ok, your name is now '{_clientName}'");
                return;
            }

            if (obj is Person p)
            {
                Log($"Got a person: {p.Name} ({p.Friends.Count} friends)");
                return;
            }

            if (obj is List<ISpell> spells)
            {
                Log($"We have received {spells.Count} spells:");
                foreach (var spell in spells)
                    Log($"  {spell.GetType().Name}: {spell.Cast()}");
                
                return;
            }

            // If we have no clue how to handle something, we
            // just print it out to the console
            Log($"Received a '{obj.GetType().Name}': {obj}");
        }

        void Log(string text) => Console.WriteLine("[Server] " + text);

        void Send(object obj) => _sendCeras.WriteToStream(_netStream, obj);
    }


    static class Server
    {
        public static void Start()
        {
            new Thread(AcceptClients).Start();
        }

        static void AcceptClients()
        {
            var listener = new TcpListener(IPAddress.Any, 43210);
            listener.Start();

            while (true)
            {
                var tcpClient = listener.AcceptTcpClient();

                var serverClientHandler = new ServerClient(tcpClient);
            }
        }


    }
}
