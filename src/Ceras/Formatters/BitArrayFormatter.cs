using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	using System.Collections;

	class BitArrayFormatter : IFormatter<BitArray>
	{
		[CerasNoReference]
		IFormatter<int[]> _intFormatter;

		public BitArrayFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(BitArray));
		}

		public void Serialize(ref byte[] buffer, ref int offset, BitArray value)
		{
			int bits = value.Count;

			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)bits);

			var ints = (bits / 32) + 1;

			int[] ar = new int[ints];
			value.CopyTo(ar, 0);

			_intFormatter.Serialize(ref buffer, ref offset, ar);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref BitArray value)
		{
			int bits = (int)SerializerBinary.ReadUInt32(buffer, ref offset);

			int[] ar = null;
			_intFormatter.Deserialize(buffer, ref offset, ref ar);

			value = new BitArray(ar);
			value.Length = bits;
		}
	}
}
