namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;
	using System;

	// Special resolver that creates instances of "DynamicObjectFormatter<T>" so unknown objects can be handled
	class DynamicObjectFormatterResolver : IFormatterResolver
	{
		CerasSerializer _serializer;
		TypeDictionary<IFormatter> _dynamicFormatters = new TypeDictionary<IFormatter>();

		public DynamicObjectFormatterResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			ref var formatter = ref _dynamicFormatters.GetOrAddValueRef(type);
			if (formatter != null)
				return formatter;

			var dynamicFormatterType = typeof(DynamicObjectFormatter<>).MakeGenericType(type);
			formatter = (IFormatter)Activator.CreateInstance(dynamicFormatterType, _serializer);
			
			return formatter;
		}
	}
}