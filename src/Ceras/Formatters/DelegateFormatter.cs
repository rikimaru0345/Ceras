using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	class DelegateFormatter : IFormatter<MulticastDelegate>
	{
		public void Deserialize(byte[] buffer, ref int offset, ref MulticastDelegate value)
		{
			throw new NotImplementedException();
		}

		public void Serialize(ref byte[] buffer, ref int offset, MulticastDelegate value)
		{
			throw new NotImplementedException();
			//value.Target
		}
	}
}
