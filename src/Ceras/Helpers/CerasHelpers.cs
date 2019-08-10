using Ceras.Formatters;
using System;

namespace Ceras.Helpers
{
	public static class CerasHelpers
	{
		public static bool IsDynamicFormatter(Type formatterType)
		{
			if (formatterType.IsGenericType)
				if (formatterType.GetGenericTypeDefinition().Name == typeof(DynamicFormatter<int>).GetGenericTypeDefinition().Name)
					return true;

			return false;
		}

		public static bool IsSchemaDynamicFormatter(Type formatterType)
		{
			if (formatterType.IsGenericType)
				if (formatterType.GetGenericTypeDefinition().Name == typeof(SchemaDynamicFormatter<int>).GetGenericTypeDefinition().Name)
					return true;

			return false;
		}
	}
}
