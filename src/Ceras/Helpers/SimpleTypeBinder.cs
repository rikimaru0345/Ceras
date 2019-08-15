namespace Ceras
{
	using System;
	using System.Collections.Generic;
	using System.Reflection;

	/// <summary>
	/// A type binder is simple. It is responsible to converts a type to a string and back.
	/// For generic types it must do so by deconstructing the type though. So giving <see cref="List{int}"/> would return "System.Collections.List".
	/// </summary>
	public interface ITypeBinder
	{
		string GetBaseName(Type type);
		Type GetTypeFromBase(string baseTypeName);
		Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments);
	}

	/// <summary>
	/// This simple type binder does two things:
	/// <para>- does the basic ITypeBinder thing (converting types to names, and back)</para> 
	/// <para>- allows the user to add assemblies that will be searched for types</para> 
	/// </summary>
	public class SimpleTypeBinder : ITypeBinder
	{
		readonly HashSet<Assembly> _searchAssemblies = new HashSet<Assembly>();

		/// <summary>
		/// Put your own assemblies in here for Ceras to discover them. If you don't and a type is not found, Ceras will have to look in all loaded assemblies (which is slow)
		/// </summary>
		public HashSet<Assembly> CustomSearchAssemblies { get; } = new HashSet<Assembly>();

		public SimpleTypeBinder()
		{
			// Search in framework
			foreach (var frameworkAsm in CerasSerializer._frameworkAssemblies)
				_searchAssemblies.Add(frameworkAsm);

			// Search in user code
			_searchAssemblies.Add(Assembly.GetEntryAssembly());

			_searchAssemblies.RemoveWhere(a => a == null);
		}


		public string GetBaseName(Type type)
		{
			if (type.IsGenericType)
				return type.GetGenericTypeDefinition().FullName;

			return type.FullName;
		}

		public Type GetTypeFromBase(string baseTypeName)
		{
			foreach (var a in _searchAssemblies)
			{
				var t = a.GetType(baseTypeName);
				if (t != null)
					return t;
			}

			foreach (var a in CustomSearchAssemblies)
			{
				if (_searchAssemblies.Contains(a))
					continue; // We've already searched there

				var t = a.GetType(baseTypeName);
				if (t != null)
				{
					_searchAssemblies.Add(a);
					return t;
				}
			}

			// Oh no... did the user forget to add the right assembly??
			// Lets search in everything that's loaded...
			foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (_searchAssemblies.Contains(a) || CustomSearchAssemblies.Contains(a))
					continue; // We've already searched there

				var t = a.GetType(baseTypeName);
				if (t != null)
				{
					_searchAssemblies.Add(a);
					return t;
				}
			}

			throw new Exception("Cannot find type " + baseTypeName + " after searching in all user provided assemblies and all loaded assemblies. Is the type in some plugin-module that was not yet loaded? Or did the assembly that contains the type change (ie the type got removed)?");
		}

		public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
		{
			var baseType = GetTypeFromBase(baseTypeName);
			return baseType.MakeGenericType(genericTypeArguments);
		}
	}
}