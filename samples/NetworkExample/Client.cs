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
		CerasSerializer _sendCeras;
		CerasSerializer _receiveCeras;

		public void Start()
		{
			// Create network connection	
			_client = new TcpClient();
			_client.Connect("localhost", 43210);
			// And use a network stream, much more comfortable to use
			_netStream = _client.GetStream();


			// Now we need our serializer
			// !! Important:
			// !! The settings of the serializers for client and server must be the same!
			var config = new SerializerConfig();
			config.Advanced.PersistTypeCache = true;
			
			_sendCeras = new CerasSerializer(config);
			_receiveCeras = new CerasSerializer(config);

			// Start a thread that receives and reacts to messages from the server
			StartReceiving();

			SendExampleObjects();
		}

		void SendExampleObjects()
		{
			// First thing is sending our login message, and then some random stuff
			Send(new ClientLogin { Name = "riki", Password = "123" });

			// Since every type of object is supported we could also send strings directly 
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
