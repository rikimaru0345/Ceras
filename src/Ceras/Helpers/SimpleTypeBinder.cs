namespace Ceras
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    /// <summary>
    /// A type binder is simple. It is responsible to converts a type to a string and back.
    /// For generic types it must do so by deconstructing the type though. So giving <see cref="List{T}"/> would return "System.Collections.List".
    /// </summary>
    public interface ITypeBinder
    {
        /// <summary>
        /// The stable name <paramref name="type"/> is written under, which
        /// <see cref="GetTypeFromBase"/> resolves back to the type.
        /// <para>
        /// The return is not nullable: a payload stores a type name so the reader can resolve it, so a
        /// type that has no name is an error. A binder that cannot name one throws
        /// <see cref="Ceras.Exceptions.CerasException"/> instead of returning null.
        /// </para>
        /// </summary>
        string GetBaseName(Type type);

        /// <summary>The type <paramref name="baseTypeName"/> names.</summary>
        Type GetTypeFromBase(string baseTypeName);

        /// <summary>The closed generic type named by the definition and its arguments.</summary>
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

            // Search in user code. Some hosts have no entry assembly, which is a normal state: the
            // search then falls through to the custom assemblies and to everything that is loaded.
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null)
                _searchAssemblies.Add(entryAssembly);
        }

        public string GetBaseName(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            // A closed generic is named by its definition, so the reader can rebuild it from that name
            // plus the arguments recorded next to it (TypeFormatter.Deserialize). An open generic
            // definition is its own definition, and names itself the same way.
            var name = type.IsGenericType
                ? type.GetGenericTypeDefinition().FullName
                : type.FullName;

            // A generic type parameter, for one, has no metadata name at all. Returning null here used
            // to be written out as a null name marker, so the save was produced successfully and only
            // failed later, as corrupt data that named neither the value nor the fix.
            if (name == null)
            {
                throw new Exceptions.CerasException(
                    $"Ceras cannot name {DescribeUnnameable(type)}, so it will not be written to a payload. " +
                    "A System.Type value is stored as the name a type binder resolves it back from, and this type has no such name. " +
                    "It is not stored as a null name either: the reader would then have nothing to resolve, and the file would fail to load as corrupt. " +
                    "Close the generic before storing it, or store the name in a string member if the name itself is what the payload needs.");
            }

            return name;
        }

        static string DescribeUnnameable(Type type)
            => type.IsGenericParameter
                ? $"the generic type parameter '{type}'" + (type.DeclaringType != null
                    ? $" of '{type.DeclaringType}'"
                    : type.DeclaringMethod != null ? " of a method" : "")
                : $"the type '{type}'";

                public Type GetTypeFromBase(string baseTypeName)
                {
                    if (baseTypeName == null)
                        throw new ArgumentNullException(nameof(baseTypeName), "The type name to resolve is null. This usually happens when a polymorphic field contains an object whose type is not marked with [CerasSerializable].");

                    try
                    {
                        var t = Type.GetType(baseTypeName);
                        if (t != null) return t;
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Error during initial Type.GetType('{baseTypeName}'): {e.Message}", e);
                    }
        
                    foreach (var a in _searchAssemblies)
                    {
                        try
                        {
                            var t = a.GetType(baseTypeName);
                            if (t != null)
                                return t;
                        }
                        catch (Exception e)
                        {
                            throw new Exception($"Error resolving type '{baseTypeName}' in assembly '{a.FullName}': {e.Message}", e);
                        }
                    }
        
                    foreach (var a in CustomSearchAssemblies)
                    {
                        if (_searchAssemblies.Contains(a))
                            continue; // We've already searched there
        
                        try
                        {
                            var t = a.GetType(baseTypeName);
                            if (t != null)
                            {
                                _searchAssemblies.Add(a);
                                return t;
                            }
                        }
                        catch (Exception e)
                        {
                            throw new Exception($"Error resolving type '{baseTypeName}' in custom search assembly '{a.FullName}': {e.Message}", e);
                        }
                    }
        
                    // Oh no... did the user forget to add the right assembly??
                    // Lets search in everything that's loaded...
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (_searchAssemblies.Contains(a) || CustomSearchAssemblies.Contains(a))
                            continue; // We've already searched there
        
                        try
                        {
                            var t = a.GetType(baseTypeName);
                            if (t != null)
                            {
                                _searchAssemblies.Add(a);
                                return t;
                            }
                        }
                        catch (Exception)
                        {
                            // Some assemblies might throw when GetType is called (e.g. dynamic assemblies or invalid names)
                            // We log this but keep searching.
                            // System.Diagnostics.Debug.WriteLine($"[Ceras] Warning: failed to search assembly '{a.FullName}' for type '{baseTypeName}': {e.Message}");
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