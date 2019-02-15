using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
	static class MiscHelpers
	{
		public static string Singular<T>(this T enumValue)
		{
			if (!typeof(T).IsEnum)
				throw new ArgumentException();

			var str = enumValue.ToString();
			return str.TrimEnd('s');
		}

		
		public static string CleanMemberName(string name)
		{
			if (name.StartsWith("m_"))
				return name.Remove(0, 2);
			if (name.StartsWith("_"))
				return name.Remove(0, 1);

			return name;
		}
	}
}
