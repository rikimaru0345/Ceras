namespace Ceras.Resolvers
{
	using Formatters;
	using System;
	using System.Collections.Generic;

	/// <summary>
	/// A really really boring resolver that produces formatters for all the "primitives" like bool, int, float, double, ... 
	/// </summary>
	public sealed class PrimitiveResolver : IFormatterResolver
	{
		static Dictionary<Type, IFormatter> _primitiveFormatters = new Dictionary<Type, IFormatter>
		{
			[typeof(bool)] = new BoolFormatter(),

			[typeof(byte)] = new ByteFormatter(),
			[typeof(sbyte)] = new SByteFormatter(),

			[typeof(char)] = new CharFormatter(),

			[typeof(Int16)] = new Int16Formatter(),
			[typeof(UInt16)] = new UInt16Formatter(),

			[typeof(Int32)] = new Int32Formatter(),
			[typeof(UInt32)] = new UInt32Formatter(),

			[typeof(Int64)] = new Int64Formatter(),
			[typeof(UInt64)] = new UInt64Formatter(),

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