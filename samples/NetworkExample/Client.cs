namespace NetworkExample
{
    using Ceras;
    using Ceras.Helpers; // This is where the ceras.WriteToStream extensions are in
    using System;
    using System.Collections.Generic;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    class Client
    {
        TcpClient _client;
        NetworkStream _netStream;
        CerasFastSerializer _sendCeras;
        CerasFastSerializer _receiveCeras;

        public void Start()
        {
            // Create network connection    
            _client = new TcpClient();
            _client.Connect("localhost", 43210);
            // And use a network stream, much more comfortable to use
            _netStream = _client.GetStream();


            // Now we need our serializer.
            //
            // !! Important: CerasFastSerializer writes no schema header and its wire format is NOT
            // !! stable across builds, so both ends must use the same build of your types.
            // !!
            // !! The setting this used to configure (c.Advanced.PersistTypeCache) does not affect the
            // !! wire: the transient type cache is per-operation state, cleared after every
            // !! Serialize/Deserialize, so a Type is written by name again in each message rather
            // !! than being "learned" once. Neither side configures anything.
            _sendCeras    = new CerasFastSerializer();
            _receiveCeras = new CerasFastSerializer();

            // Start a thread that receives and reacts to messages from the server
            StartReceiving();

            SendExampleObjects();
        }

        void SendExampleObjects()
        {
            // First thing is sending our login message, and then some random stuff
            Send(new ClientLogin { Name = "riki", Password = "123" });

            // Strings can be sent directly without wrapping them in a message type.
            // (Not *every* type is supported: delegates, reflection metadata, enumerators and
            // engine-native objects are refused, as are ReadOnlyCollection<T>/ReadOnlyDictionary<K,V>
            // and any List<T>/Dictionary<K,V> subclass. See the README's "When should I not use
            // this?" for the full list.)
            Send("Hello I'm the client, sending a string directly! :)");
            // And we can even send numbers and stuff...
            Send(500.678);
            Send(DateTime.Now);
            Send((short)435);

            // Now lets try a more complicated object
            var bob = new Person { Name = "Bob", Age = 20 };
            var alice = new Person { Name = "Alice", Age = 21 };
            bob.Friends.Add(alice);
            alice.Friends.Add(bob);

            Send(bob);

            // And finally some example for polymorphic types (inheritance)
            List<ISpell> spells = new List<ISpell>();
            spells.Add(new Lightning());
            spells.Add(new Fireball());

            Send(spells);
        }

        void StartReceiving()
        {
            Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        // Read until we received the next message from the server
                        var obj = await _receiveCeras.ReadFromStream(_netStream);
                        HandleMessage(obj);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Client error while receiving: " + e);
                }
            });
        }

        void HandleMessage(object obj)
        {
            Console.WriteLine($"[Client] Received a '{obj.GetType().Name}': {obj}");
        }

        // A little helper function that sends any object to the server
        void Send(object obj) => _sendCeras.WriteToStream(_netStream, obj);
    }
}
