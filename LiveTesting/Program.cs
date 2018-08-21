using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveTesting
{
	using System.Diagnostics;
	using Ceras;

	class Program
	{
		static void Main(string[] args)
		{
			var config = new SerializerConfig
			{
				PersistTypeCache = true,
			};
			config.KnownTypes.Add(typeof(SetName));
			config.KnownTypes.Add(typeof(NewPlayer));
			config.KnownTypes.Add(typeof(LongEnum));
			config.KnownTypes.Add(typeof(ByteEnum));

			var msg = new SetName
			{
				Name = "abc",
				Type = SetName.SetNameType.Join
			};

			CerasSerializer sender = new CerasSerializer(config);
			CerasSerializer receiver = new CerasSerializer(config);

			Console.WriteLine("Hash: " + sender.ProtocolChecksum.Checksum);

			var data = sender.Serialize(msg);
			PrintData(data);
			data = sender.Serialize(msg);
			PrintData(data);

			var obj = receiver.Deserialize<object>(data);
			var clone = (SetName)obj;
			Console.WriteLine(clone.Type);
			Console.WriteLine(clone.Name);

			data = sender.Serialize(new NewPlayer());
			PrintData(data);

			var g = Guid.NewGuid();
			int offset = 0;
			Console.WriteLine("GUID: " + g);
			SerializerBinary.WriteGuid(ref data, ref offset, g);
			Debug.Assert(offset == 16);
			PrintData(data);
			offset = 0;
			var guidClone = SerializerBinary.ReadGuid(data, ref offset);
			Console.WriteLine("GUID: " + guidClone + " (clone)");

			//
			// Enums
			//
			var longEnum = LongEnum.b;
			var byteEnum = ByteEnum.b;
			var longEnumData = sender.Serialize(longEnum);
			var byteEnumData = sender.Serialize(byteEnum);



			Console.ReadLine();
		}

		static void PrintData(byte[] data)
		{
			var text = Encoding.ASCII.GetString(data).Replace("\0", "");
			Console.WriteLine(data.Length + " bytes: " + text);
		}
	}
	
	public enum LongEnum : long
	{
		a = 1,
		b = long.MaxValue - 500
	}

	public enum ByteEnum : byte
	{
		a = 1,
		b = 200,
	}

	class SetName
	{
		public SetNameType Type;
		public string Name;

		public enum SetNameType
		{
			Initial, Change, Join
		}
	}

	class NewPlayer
	{
		public string Guid;
	}
}
