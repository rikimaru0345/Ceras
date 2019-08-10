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
		VersionToleranceMode _versionToleranceMode;

		public DynamicObjectFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
			_versionToleranceMode = ceras.Config.VersionTolerance.Mode;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (_ceras.Config.Advanced.AotMode == AotMode.Enabled)			
				if (_ceras.Config.Warnings.ExceptionWhenUsingDynamicFormatterInAotMode)
					throw new InvalidOperationException($"No formatter for the Type '{type.FullName}' was found. Ceras is trying to fall back to the DynamicFormatter, but that formatter will never work in on AoT compiled platforms. Use the code generator tool to automatically generate a formatter for this type.");
			

			var meta = _ceras.GetTypeMetaData(type);

			if (meta.IsPrimitive)
				throw new InvalidOperationException("DynamicFormatter is not allowed to serialize serialization-primitives.");


			if ((_versionToleranceMode == VersionToleranceMode.Standard && !meta.IsFrameworkType) ||
				(_versionToleranceMode == VersionToleranceMode.Extended && meta.IsFrameworkType))
			{
				// SchemaFormatter will automatically adjust itself to the schema when it's read
				var formatterType = typeof(SchemaDynamicFormatter<>).MakeGenericType(type);
				return (IFormatter)Activator.CreateInstance(formatterType, args: new object[] { _ceras, meta.PrimarySchema, false });
			}
			else
			{
				var formatterType = typeof(DynamicFormatter<>).MakeGenericType(type);
				return (IFormatter)Activator.CreateInstance(formatterType, new object[] { _ceras, false });
			}
		}
	}
}