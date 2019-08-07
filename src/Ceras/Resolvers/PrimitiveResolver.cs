namespace Ceras.Resolvers
{
	using Formatters;
	using System;
	using System.Collections.Generic;

	/// <summary>
	/// A really boring resolver that produces formatters for all the "primitives" like bool, int, float, double, ... 
	/// </summary>
	public sealed class PrimitiveResolver : IFormatterResolver
	{
		static Dictionary<Type, IFormatter> _primitiveFormatters = new Dictionary<Type, IFormatter>
		{
			[typeof(bool)] = new BoolFormatter(),

			[typeof(byte)] = new ByteFormatter(),
			[typeof(sbyte)] = new SByteFormatter(),

			[typeof(char)] = new CharFormatter(),
			
			[typeof(IntPtr)] = new IntPtrFormatter(),
			[typeof(UIntPtr)] = new UIntPtrFormatter(),
			
			[typeof(float)] = new FloatFormatter(),
			[typeof(double)] = new DoubleFormatter(),


			//
			// VarInt Formatters
			[typeof(short)] = new Int16Formatter(),
			[typeof(ushort)] = new UInt16Formatter(),

			[typeof(int)] = new Int32Formatter(),
			[typeof(uint)] = new UInt32Formatter(),

			[typeof(long)] = new Int64Formatter(),
			[typeof(ulong)] = new UInt64Formatter(),
		};

		readonly CerasSerializer _ceras;

		public PrimitiveResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public IFormatter GetFormatter(Type type)
		{
			if(_ceras.Config.IntegerEncoding == IntegerEncoding.ForceReinterpret)
				if(IsVarIntInteger(type))
					return null;

			if (_primitiveFormatters.TryGetValue(type, out var f))
				return f;

			if (type.IsEnum)
				return (IFormatter)Activator.CreateInstance(typeof(ReinterpretFormatter<>).MakeGenericType(type));

			return null;
		}

		static bool IsVarIntInteger(Type type)
		{
			if(type == typeof(int)
				|| type == typeof(uint)
				|| type == typeof(short)
				|| type == typeof(ushort)
				|| type == typeof(long)
				|| type == typeof(ulong))
				return true;
			return false;
		}
	}
}