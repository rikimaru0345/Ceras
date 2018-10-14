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
	}
}