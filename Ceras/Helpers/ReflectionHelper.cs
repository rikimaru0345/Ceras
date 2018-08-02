namespace Ceras.Helpers
{
	using System;

	static class ReflectionHelper
	{
		public static Type FindClosedType(Type type, Type openGeneric)
		{
			if (openGeneric.IsInterface)
			{
				var collectionInterfaces = type.FindInterfaces((f, o) =>
				{
					if (!f.IsGenericType)
						return false;
					return f.GetGenericTypeDefinition() == openGeneric;
				}, null);

				if (collectionInterfaces.Length > 0)
				{
					return collectionInterfaces[0];
				}
			}
			else
			{
				// Go up through the hierarchy until we find that open generic
				var t = type;
				while (t != null)
				{
					if (t.IsGenericType)
						if (t.GetGenericTypeDefinition() == openGeneric)
							return t;

					t = t.BaseType;
				}
			}


			return null;
		}
	}
}