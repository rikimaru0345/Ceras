using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;

	/// <summary>
	/// Creates super-fast formatters for "blittable" types. Only returns results when IntegerEncoding isn't set to ForceVarInt
	/// </summary>
	public sealed class ReinterpretFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _ceras;

		public ReinterpretFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (!ReflectionHelper.IsBlittableType(type))
				return null;

			if(_ceras.Config.IntegerEncoding == IntegerEncoding.ForceVarInt)
				return null;

			if(_ceras.Config.VersionTolerance.Mode != VersionToleranceMode.Disabled)
				return null; // reinterpret is not compatible with the idea of version tolerance

			var formatterType = typeof(ReinterpretFormatter<>).MakeGenericType(type);

			return (IFormatter) Activator.CreateInstance(formatterType);
		}
	}
}
