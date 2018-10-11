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
			IFormatter formatter;

			if (!_dynamicFormatters.TryGetValue(type, out formatter))
			{
				var dynamicFormatterType = typeof(DynamicObjectFormatter<>).MakeGenericType(type);
				formatter = (IFormatter)Activator.CreateInstance(dynamicFormatterType, _serializer);

				// todo: maybe allow a setting where we can disable caching completely, that way we don't have to clear buffers, and we won't use more bytes than needed. Could be cool for networking. But no more circular references, and also no de-duplication (if a list contains the same class multiple times...)
				// Dynamic formatter without caching doesn't help us much
				// As soon as there are circular references, self-references or any other sort of loops in the object graph
				// we'll get a stack-overflow

				if (!type.IsValueType)
				{
					formatter = WrapInCache(type, formatter, _serializer);
				}

				_dynamicFormatters[type] = formatter;
			}

			return formatter;
		}

		public static IFormatter WrapInCache(Type typeToBeFormatted, IFormatter innerFormatter, CerasSerializer serializer)
		{
			// Only do this for reference types, since value types obviously cannot be "cached"
			if (typeToBeFormatted.IsValueType)
				throw new InvalidOperationException("Cannot create a cache-wrapper for value-types, they cannot be cached by definition");

			var cacheFormatterType = typeof(CacheFormatter<>).MakeGenericType(typeToBeFormatted);
			return (IFormatter)Activator.CreateInstance(cacheFormatterType, innerFormatter, serializer, serializer.GetObjectCache());
		}
	}
}