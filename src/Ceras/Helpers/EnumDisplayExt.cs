using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
	static class EnumDisplayExt
	{
		public static string Singular<T>(this T enumValue)
		{
			if (!typeof(T).IsEnum)
				throw new ArgumentException();

			var str = enumValue.ToString();
			return str.TrimEnd('s');
		}
	}
}
