using System;
using System.Linq.Expressions;

namespace Ceras.Formatters
{
	sealed class ByteFormatter : IFormatter<byte>
	{
		public void Serialize(ref byte[] buffer, ref int offset, byte value)
		{
			SerializerBinary.WriteByte(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref byte value)
		{
			value = SerializerBinary.ReadByte(buffer, ref offset);
		}
	}

	sealed class SByteFormatter : IFormatter<sbyte>
	{
		public void Serialize(ref byte[] buffer, ref int offset, sbyte value)
		{
			SerializerBinary.WriteByte(ref buffer, ref offset, (byte)value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref sbyte value)
		{
			value = (sbyte)SerializerBinary.ReadByte(buffer, ref offset);
		}
	}

	sealed class BoolFormatter : IFormatter<bool>
	{
		public void Serialize(ref byte[] buffer, ref int offset, bool value)
		{
			SerializerBinary.WriteInt32(ref buffer, ref offset, value ? 1 : 0);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref bool value)
		{
			value = SerializerBinary.ReadInt32(buffer, ref offset) != 0;
		}
	}

	sealed class CharFormatter : IFormatter<char>
	{
		public void Serialize(ref byte[] buffer, ref int offset, char value)
		{
			SerializerBinary.WriteInt16Fixed(ref buffer, ref offset, (short)value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref char value)
		{
			value = (char)SerializerBinary.ReadInt16Fixed(buffer, ref offset);
		}
	}

	sealed class Int16Formatter : IFormatter<short>
	{
		public void Serialize(ref byte[] buffer, ref int offset, short value)
		{
			SerializerBinary.WriteInt16Fixed(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref short value)
		{
			value = SerializerBinary.ReadInt16Fixed(buffer, ref offset);
		}
	}

	sealed class UInt16Formatter : IFormatter<ushort>
	{
		public void Serialize(ref byte[] buffer, ref int offset, ushort value)
		{
			SerializerBinary.WriteInt16Fixed(ref buffer, ref offset, (short)value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref ushort value)
		{
			value = (ushort)SerializerBinary.ReadInt16Fixed(buffer, ref offset);
		}
	}

	sealed class Int32Formatter : IFormatter<int>, IInlineEmitter
	{
		public void Serialize(ref byte[] buffer, ref int offset, int value)
		{
			SerializerBinary.WriteInt32(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref int value)
		{
			value = SerializerBinary.ReadInt32(buffer, ref offset);
		}


		public Expression EmitWrite(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int writtenSize)
		{
			writtenSize = 4;
			return null;
		}

		public Expression EmitRead(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int readSize)
		{
			readSize = 4;

			return null;
		}
	}

	sealed class UInt32Formatter : IFormatter<uint>
	{
		public void Serialize(ref byte[] buffer, ref int offset, uint value)
		{
			SerializerBinary.WriteUInt32(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref uint value)
		{
			value = SerializerBinary.ReadUInt32(buffer, ref offset);
		}
	}

	sealed class FloatFormatter : IFormatter<float>
	{
		public void Serialize(ref byte[] buffer, ref int offset, float value)
		{
			SerializerBinary.WriteFloat32Fixed(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref float value)
		{
			value = SerializerBinary.ReadFloat32Fixed(buffer, ref offset);
		}
	}

	sealed class DoubleFormatter : IFormatter<double>
	{
		public void Serialize(ref byte[] buffer, ref int offset, double value)
		{
			SerializerBinary.WriteDouble64Fixed(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref double value)
		{
			value = SerializerBinary.ReadDouble64Fixed(buffer, ref offset);
		}
	}

	sealed class Int64Formatter : IFormatter<long>
	{
		public void Serialize(ref byte[] buffer, ref int offset, long value)
		{
			SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref long value)
		{
			value = SerializerBinary.ReadInt64Fixed(buffer, ref offset);
		}
	}

	sealed class UInt64Formatter : IFormatter<ulong>
	{
		public void Serialize(ref byte[] buffer, ref int offset, ulong value)
		{
			SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, (long)value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref ulong value)
		{
			value = (ulong)SerializerBinary.ReadInt64Fixed(buffer, ref offset);
		}
	}

	sealed class IntPtrFormatter : IFormatter<IntPtr>
	{
		public void Serialize(ref byte[] buffer, ref int offset, IntPtr IntPtr)
		{
			SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, IntPtr.ToInt64());
		}

		public void Deserialize(byte[] buffer, ref int offset, ref IntPtr value)
		{
			value = new IntPtr(SerializerBinary.ReadInt64Fixed(buffer, ref offset));
		}
	}

	sealed class UIntPtrFormatter : IFormatter<UIntPtr>
	{
		public void Serialize(ref byte[] buffer, ref int offset, UIntPtr IntPtr)
		{
			SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, (long)IntPtr.ToUInt64());
		}

		public void Deserialize(byte[] buffer, ref int offset, ref UIntPtr value)
		{
			value = new UIntPtr((ulong)SerializerBinary.ReadInt64Fixed(buffer, ref offset));
		}
	}

}
