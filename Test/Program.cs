using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Test
{
	using Ceras;

	class Program
	{
		static void Main(string[] args)
		{
			var _sendSerializer = new CerasSerializer(new SerializerConfig { PersistTypeCache = true });

			var obj = new CursorPositionUpdate { X = 501, Y = 223 };

			byte[] data = null;
			int written = _sendSerializer.Serialize(obj, ref data);


			var _recvSerializer = new CerasSerializer(new SerializerConfig { PersistTypeCache = true });

			var copy = _recvSerializer.DeserializeObject(data);


			// 2

			
			int written2 = _sendSerializer.Serialize(obj, ref data);
			var copy2 = _recvSerializer.DeserializeObject(data);

		}
	}


	class CursorPositionUpdate
	{
		public short X;
		public short Y;
	}
}
