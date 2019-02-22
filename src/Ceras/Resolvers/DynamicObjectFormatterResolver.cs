namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;
	using System;

	/// <summary>
	/// This resolver creates instances of <see cref="DynamicFormatter{T}"/>, which can handle pretty much every complex object (assuming it has a correct TypeConfig to work with). It is always used last because it *always* returns a result.
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
			if (_ceras.Config.Advanced.AotMode == AotMode.Enabled)
			{
				throw new InvalidOperationException($"No formatter for the Type '{type.FullName}' was found. Ceras is trying to fall back to the DynamicFormatter, but that formatter will never work in on AoT compiled platforms. Use the code generator tool to automatically generate a formatter for this type.");
			}


			ref var formatter = ref _dynamicFormatters.GetOrAddValueRef(type);
			if (formatter != null)
				return formatter;

			var dynamicFormatterType = typeof(DynamicFormatter<>).MakeGenericType(type);
			formatter = (IFormatter)Activator.CreateInstance(dynamicFormatterType, _ceras);
			
			return formatter;
		}
	}
}