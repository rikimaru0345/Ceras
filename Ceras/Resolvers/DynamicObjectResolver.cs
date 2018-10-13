namespace Ceras.Resolvers
{
	using System;
	using System.Collections.Generic;
	using Formatters;
	using Helpers;

	class DynamicObjectFormatterResolver : IFormatterResolver
	{
		CerasSerializer _serializer;
		Dictionary<Type, IFormatter> _dynamicFormatters = new Dictionary<Type, IFormatter>();

		public DynamicObjectFormatterResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (!_dynamicFormatters.TryGetValue(type, out var formatter))
			{
				var dynamicFormatterType = typeof(DynamicObjectFormatter<>).MakeGenericType(type);
				formatter = (IFormatter)Activator.CreateInstance(dynamicFormatterType, _serializer);
				
				_dynamicFormatters[type] = formatter;
			}

			return formatter;
		}

		public static IFormatter WrapInCache(Type typeToBeFormatted, CerasSerializer serializer)
		{
			// Only do this for reference types, since value types obviously cannot be "cached"
			if (typeToBeFormatted.IsValueType)
				throw new InvalidOperationException("Cannot create a cache-wrapper for value-types, they cannot be cached by definition");

			var cacheFormatterType = typeof(ReferenceFormatter<>).MakeGenericType(typeToBeFormatted);
			return (IFormatter)Activator.CreateInstance(cacheFormatterType, serializer);
		}
	}
}