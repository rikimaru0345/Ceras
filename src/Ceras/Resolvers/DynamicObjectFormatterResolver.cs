namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;
	using System;

	/// <summary>
	/// This resolver creates instances of <see cref="DynamicObjectFormatter{T}"/>, which can handle pretty much every complex object (assuming it has a correct TypeConfig to work with). It is always used last because it *always* returns a result.
	/// </summary>
	public class DynamicObjectFormatterResolver : IFormatterResolver
	{
		CerasSerializer _ceras;
		TypeDictionary<IFormatter> _dynamicFormatters = new TypeDictionary<IFormatter>();

		public DynamicObjectFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public IFormatter GetFormatter(Type type)
		{
			ref var formatter = ref _dynamicFormatters.GetOrAddValueRef(type);
			if (formatter != null)
				return formatter;

			var dynamicFormatterType = typeof(DynamicObjectFormatter<>).MakeGenericType(type);
			formatter = (IFormatter)Activator.CreateInstance(dynamicFormatterType, _ceras);
			
			return formatter;
		}
	}
}