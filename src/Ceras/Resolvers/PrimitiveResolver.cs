namespace Ceras.Resolvers
{
	using Formatters;
	using System;
	using System.Collections.Generic;
	using System.Linq.Expressions;

	sealed class PrimitiveResolver : IFormatterResolver
	{
		readonly CerasSerializer _serializer;

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


		public PrimitiveResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (_primitiveFormatters.TryGetValue(type, out var f))
				return f;

			if (type.IsEnum)
				return (IFormatter)Activator.CreateInstance(typeof(EnumFormatter<>).MakeGenericType(type), _serializer);

			return null;
		}

	}
}