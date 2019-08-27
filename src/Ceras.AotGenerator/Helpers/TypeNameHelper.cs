using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System
{
	static class TypeNameHelper
	{
		public static string TryGetPrimitiveName(Type type)
		{
			if (type == typeof(bool))  return "bool";

			if (type == typeof(byte))  return "byte";
			if (type == typeof(sbyte))  return "sbyte";			
			
			if (type == typeof(short)) return "short";
			if (type == typeof(ushort)) return "ushort";

			if (type == typeof(int))	return "int";
			if (type == typeof(uint))	return "uint";
			
			if (type == typeof(long))  return "long";
			if (type == typeof(ulong))  return "ulong";

			if (type == typeof(float)) return "float";
			if (type == typeof(double)) return "double";

			if (type == typeof(decimal)) return "decimal";
			
			if (type == typeof(string)) return "string";
			if (type == typeof(char)) return "char";

			return null;
		}

		// returns something like
		// List<int>
		public static string ToFriendlyName(this Type type, bool fullName = false)
		{
			return Ceras.Helpers.ReflectionHelper.FriendlyName(type, fullName);
		}
	
		// returns something like
		// List_int
		public static string ToVariableSafeName(this Type type)
		{
			var primitiveName = TryGetPrimitiveName(type);
			if(primitiveName != null)
				return primitiveName;

			var name = type.Name;
			if (type.IsGenericType)
			{
				return name.Split('`')[0] // base name
					+ "_" 
					+ string.Join("_", type.GetGenericArguments().Select(t => t.ToFriendlyName(false)).ToArray());
			}
			else
			{
				return name;
			}
		}
	}
}
