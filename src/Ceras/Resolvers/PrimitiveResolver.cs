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

			[typeof(short)] = new Int16FixedFormatter(),
			[typeof(ushort)] = new UInt16FixedFormatter(),

			[typeof(int)] = new Int32FixedFormatter(),
			[typeof(uint)] = new UInt32FixedFormatter(),

			[typeof(long)] = new Int64FixedFormatter(),
			[typeof(ulong)] = new UInt64FixedFormatter(),

			[typeof(float)] = new FloatFormatter(),
			[typeof(double)] = new DoubleFormatter(),

			[typeof(IntPtr)] = new IntPtrFormatter(),
			[typeof(UIntPtr)] = new UIntPtrFormatter(),
		};

		readonly CerasSerializer _ceras;



		public PrimitiveResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (_primitiveFormatters.TryGetValue(type, out var f))
				return f;

			if (type.IsEnum)
				return (IFormatter)Activator.CreateInstance(typeof(ReinterpretFormatter<>).MakeGenericType(type));

			return null;
		}

	}
}