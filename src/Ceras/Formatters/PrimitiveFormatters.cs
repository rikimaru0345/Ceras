using Ceras.Helpers;
using System;
using System.Linq.Expressions;

namespace Ceras.Formatters
{
	// Marker
	// - Used when T is blittable
	// - DynamicFormatter will replace this formatter with a call to 'ReinterpretFormatter<T>.Read/Write'
	// - The DynamicFormatter will automatically merge EnsureCapacity and offset adjustments
	interface IIsReinterpretFormatter { }

	// A formatter can implement this to help DynamicFormatter
	// - implementing class will customize the Read/Write calls
	// - implementing class takes care of EnsureCapacity and offset manually
	interface ICallInliner
	{
		Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target);
		Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target);
	}


	//
	// 1 Byte Fixed
	sealed class ByteFormatter : IFormatter<byte>, IIsReinterpretFormatter
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

	sealed class SByteFormatter : IFormatter<sbyte>, IIsReinterpretFormatter
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

	sealed class BoolFormatter : IFormatter<bool>, IIsReinterpretFormatter
	{
		public void Serialize(ref byte[] buffer, ref int offset, bool value)
		{
			byte byteValue;

			if (value)
				byteValue = 1;
			else
				byteValue = 0;

			SerializerBinary.WriteByte(ref buffer, ref offset, byteValue);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref bool value)
		{
			value = SerializerBinary.ReadByte(buffer, ref offset) != 0;
		}
	}


	//
	// 2 Byte Fixed
	sealed class CharFormatter : IFormatter<char>, IIsReinterpretFormatter
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

	sealed class Int16FixedFormatter : IFormatter<short>, IIsReinterpretFormatter
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

	sealed class UInt16FixedFormatter : IFormatter<ushort>, IIsReinterpretFormatter
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


	//
	// 4 Byte Fixed
	sealed class Int32FixedFormatter : IFormatter<int>, IIsReinterpretFormatter
	{
		public void Serialize(ref byte[] buffer, ref int offset, int value)
		{
			SerializerBinary.WriteInt32Fixed(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref int value)
		{
			value = SerializerBinary.ReadInt32Fixed(buffer, ref offset);
		}
	}

	sealed class UInt32FixedFormatter : IFormatter<uint>, IIsReinterpretFormatter
	{
		public void Serialize(ref byte[] buffer, ref int offset, uint value)
		{
			SerializerBinary.WriteUInt32Fixed(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref uint value)
		{
			value = SerializerBinary.ReadUInt32Fixed(buffer, ref offset);
		}
	}

	sealed class FloatFormatter : IFormatter<float>, IIsReinterpretFormatter
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


	//
	// 8 Byte Fixed
	sealed class Int64FixedFormatter : IFormatter<long>, IIsReinterpretFormatter
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

	sealed class UInt64FixedFormatter : IFormatter<ulong>, IIsReinterpretFormatter
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

	sealed class DoubleFormatter : IFormatter<double>, IIsReinterpretFormatter
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


	//
	// VarInt
	sealed class Int32Formatter : IFormatter<int>, ICallInliner
	{
		public void Serialize(ref byte[] buffer, ref int offset, int value)
		{
			SerializerBinary.WriteInt32(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref int value)
		{
			value = SerializerBinary.ReadInt32(buffer, ref offset);
		}


		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteInt32));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}

		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadInt32));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}
	}

	sealed class UInt32Formatter : IFormatter<uint>, ICallInliner
	{
		public void Serialize(ref byte[] buffer, ref int offset, uint value)
		{
			SerializerBinary.WriteUInt32(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref uint value)
		{
			value = SerializerBinary.ReadUInt32(buffer, ref offset);
		}

		
		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteUInt32));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}

		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadUInt32));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}
	}


	sealed class Int64Formatter : IFormatter<long>, ICallInliner
	{
		public void Serialize(ref byte[] buffer, ref int offset, long value)
		{
			SerializerBinary.WriteInt64(ref buffer, ref offset, value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref long value)
		{
			value = SerializerBinary.ReadInt64(buffer, ref offset);
		}
		
		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteInt64));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}

		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadInt64));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}
	}

	sealed class UInt64Formatter : IFormatter<ulong>, ICallInliner
	{
		public void Serialize(ref byte[] buffer, ref int offset, ulong value)
		{
			SerializerBinary.WriteInt64(ref buffer, ref offset, (long)value);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref ulong value)
		{
			value = (ulong)SerializerBinary.ReadInt64(buffer, ref offset);
		}

		
		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.WriteUInt64));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}

		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.ReadUInt64));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}
	}


	//
	// IntPtr/UIntPtr
	sealed class IntPtrFormatter : IFormatter<IntPtr>, ICallInliner
	{
		public void Serialize(ref byte[] buffer, ref int offset, IntPtr value) => Write(ref buffer, ref offset, value);
		public void Deserialize(byte[] buffer, ref int offset, ref IntPtr value) => Read(buffer, ref offset, ref value);
		
		
		public static void Write(ref byte[] buffer, ref int offset, IntPtr value)
		{
			SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, value.ToInt64());
		}

		public static void Read(byte[] buffer, ref int offset, ref IntPtr value)
		{
			value = new IntPtr(SerializerBinary.ReadInt64Fixed(buffer, ref offset));
		}
		
		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(IntPtrFormatter).GetMethod(nameof(IntPtrFormatter.Write));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}

		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(IntPtrFormatter).GetMethod(nameof(IntPtrFormatter.Read));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}
	}

	sealed class UIntPtrFormatter : IFormatter<UIntPtr>, ICallInliner
	{
		public void Serialize(ref byte[] buffer, ref int offset, UIntPtr value) => Write(ref buffer, ref offset, value);
		public void Deserialize(byte[] buffer, ref int offset, ref UIntPtr value) => Read(buffer, ref offset, ref value);
		

		public static void Write(ref byte[] buffer, ref int offset, UIntPtr value)
		{
			SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, (long)value.ToUInt64());
		}

		public static void Read(byte[] buffer, ref int offset, ref UIntPtr value)
		{
			value = new UIntPtr((ulong)SerializerBinary.ReadInt64Fixed(buffer, ref offset));
		}
		
		public Expression EmitSerialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(UIntPtrFormatter).GetMethod(nameof(UIntPtrFormatter.Write));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}

		public Expression EmitDeserialize(ParameterExpression bufferArg, ParameterExpression offsetArg, Expression target)
		{
			var method = typeof(UIntPtrFormatter).GetMethod(nameof(UIntPtrFormatter.Read));
			return Expression.Call(method, bufferArg, offsetArg, target);
		}
	}

}
