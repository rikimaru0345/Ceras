namespace Ceras
{
	using System;
	using System.Collections.Generic;
	using System.Reflection;
	
	public interface ITypeBinder
	{
		string GetBaseName(Type type);
		Type GetTypeFromBase(string baseTypeName);
		Type GetTypeFromBaseAndAgruments(string baseTypeName, params Type[] genericTypeArguments);
	}

	public class NaiveTypeBinder : ITypeBinder
	{
		public string GetBaseName(Type type)
		{
			return SimpleTypeBinderHelper.GetBaseName(type);
		}

		public Type GetTypeFromBase(string baseTypeName)
		{
			return SimpleTypeBinderHelper.GetTypeFromBase(baseTypeName);
		}

		public Type GetTypeFromBaseAndAgruments(string baseTypeName, params Type[] genericTypeArguments)
		{
			return SimpleTypeBinderHelper.GetTypeFromBaseAndAgruments(baseTypeName, genericTypeArguments);
		}
	}

	
	public static class SimpleTypeBinderHelper
	{
		static readonly HashSet<Assembly> _typeAssemblies = new HashSet<Assembly>();

		static SimpleTypeBinderHelper()
		{
			_typeAssemblies.Add(typeof(int).Assembly);
			_typeAssemblies.Add(typeof(List<>).Assembly);
			_typeAssemblies.Add(Assembly.GetCallingAssembly());
			_typeAssemblies.Add(Assembly.GetEntryAssembly());

			_typeAssemblies.RemoveWhere(a => a == null);
		}

		// given List<int> it would return "System.Collections.List"
		public static string GetBaseName(Type type)
		{
			if (type.IsGenericType)
				return type.GetGenericTypeDefinition().FullName;

			return type.FullName;
		}

		public static Type GetTypeFromBase(string baseTypeName)
		{
			// todo: let the user provide a way!
			// todo: alternatively, search in ALL loaded assemblies... but that is slow as fuck

			foreach (var a in _typeAssemblies)
			{
				var t = a.GetType(baseTypeName);
				if (t != null)
					return t;
			}

			// Oh no... did the user forget to add the right assembly??
			// Lets search in everything that's loaded...
			foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
			{
				var t = a.GetType(baseTypeName);
				if (t != null)
				{
					_typeAssemblies.Add(a);
					return t;
				}
			}

			throw new Exception("Cannot find type " + baseTypeName + " after searching in all user provided assemblies and all loaded assemblies. Is the type in some plugin-module that was not yet loaded? Or did the assembly that contains the type change (ie the type got removed)?");
		}

		public static Type GetTypeFromBaseAndAgruments(string baseTypeName, params Type[] genericTypeArguments)
		{
			var baseType = GetTypeFromBase(baseTypeName);
			return baseType.MakeGenericType(genericTypeArguments);
		}
	}

}