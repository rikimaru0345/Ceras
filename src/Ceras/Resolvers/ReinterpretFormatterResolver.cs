using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;

	class ReinterpretFormatterResolver : IFormatterResolver
	{
		readonly CerasSerializer _ceras;

		public ReinterpretFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;
		}

		public IFormatter GetFormatter(Type type)
		{
			if(!_ceras.Config.UseReinterpretFormatter)
				return null;

			if (!ReflectionHelper.IsUnmanaged(type))
				return null;

			var formatterType = typeof(ReinterpretFormatter<>).MakeGenericType(type);

			return (IFormatter) Activator.CreateInstance(formatterType);
		}
	}
}
