using Ceras;
using Ceras.Helpers;
using System;

namespace NetworkExample
{
	using System.Collections.Generic;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;

	class Program
	{
		static void Main(string[] args)
		{
			// Start server on new thread
			new Thread(ServerThread).Start();

		}


		static void ServerThread()
		{
			var listener = new TcpListener(IPAddress.Any, 123456);
			listener.Start();

			while (true)
			{
				var client = listener.AcceptTcpClient();

				new Thread(ServerClient).Start(client);
			}
		}

		static async void ServerClient(object threadParam)
		{
			var client = (TcpClient)threadParam;
			var stream = client.GetStream();

			string clientName = null;

			try
			{
				// 1. We want to keep learned types
				var config = new SerializerConfig();
				config.PersistTypeCache = true;

				var ceras = new CerasSerializer(config);

				// 2. Keep receiving packets from the client and respond to them
				// Eventually when the client disconnects we'll just get an exception and end the thread...
				while (true)
				{
					var obj = await ceras.ReadFromStream(stream);

					if (obj is ClientHello clientHello)
					{
						clientName = clientHello.Name;
						ceras.WriteToStream(stream, $"Hello client I have received your hello/login. Your name is now '{clientName}'");
					}

					if(obj is Person a)
				}
			}
			catch (Exception e)
			{
				Console.WriteLine($"Error while handling client '{client.Client.RemoteEndPoint}': " + e);
			}
		}
	}



	// This is like a login packet
	class ClientHello
	{
		public string Name;
	}

	class ServerResponse
	{
		public string Text;
	}

	class Person
	{
		public string Name;
		public int Age;
		public List<Person> Friends = new List<Person>();
	}
}
