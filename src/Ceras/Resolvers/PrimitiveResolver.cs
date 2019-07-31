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

			[typeof(short)] = new Int16Formatter(),
			[typeof(ushort)] = new UInt16Formatter(),

			[typeof(int)] = new Int32Formatter(),
			[typeof(uint)] = new UInt32Formatter(),

			[typeof(long)] = new Int64Formatter(),
			[typeof(ulong)] = new UInt64Formatter(),

			[typeof(float)] = new FloatFormatter(),
			[typeof(double)] = new DoubleFormatter(),

			[typeof(IntPtr)] = new IntPtrFormatter(),
			[typeof(UIntPtr)] = new UIntPtrFormatter(),
		};

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