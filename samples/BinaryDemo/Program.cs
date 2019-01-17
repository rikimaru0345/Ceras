using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BinaryDemo
{
	using Ceras;

	class Program
	{
		static void Main(string[] args)
		{
			var ceras = new CerasSerializer();

			//
			// Setup
			var a = new Person();
			a.Name = "Alice";
			a.Anything = 5;

			var b = new Person();
			b.Name = "Bob";
			b.Anything = typeof(Person);

			a.Friends.Add(b); // Alice -> Bob
			b.Friends.Add(a); // Bob -> Alice
			a.Friends.Add(a); // Alice their own friend as well!


			//
			// Serialize and Visualize
			var data = ceras.Serialize(a);
			VisualizePrint(data);


			//
			// Repeat with KnownTypes
			var config = new SerializerConfig();
			config.KnownTypes.Add(typeof(int));
			config.KnownTypes.Add(typeof(Person));
			ceras = new CerasSerializer(config);
			
			data = ceras.Serialize(a);
			VisualizePrint(data);
		}

		public static void VisualizePrint(byte[] bytes)
		{
			// Pseudo ASCII
			var charArray = Encoding.ASCII.GetString(bytes).Replace("\0", " ").Select(c => char.IsControl(c) ? '_' : c).ToArray();
			var pseudoAscii = new string(charArray);
			
			// Hex
			Console.WriteLine(string.Join("", bytes.Select(b => b.ToString("x2"))));

			// Print the pseudo ascii but align the symbols so they are directly below the hex-bytes
			Console.WriteLine(string.Join(" ", pseudoAscii.ToCharArray()));
			
			Console.WriteLine();
		}
	}

	class Person
	{
		public string Name;

		public List<Person> Friends = new List<Person>();

		public object Anything;
	}
}
