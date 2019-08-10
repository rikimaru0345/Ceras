using System;

namespace CerasAotFormatterGenerator
{
	using System.Collections.Generic;
	using Ceras;
	using System.Linq;
	using System.Reflection;
	using System.Text;
	using Ceras.Formatters;
	using Ceras.Formatters.AotGenerator;
	using System.IO;
    using Ceras.Helpers;

    /*
	 * Ideas for improvement:
	 * 
	 * 1. (Done) Remove roslyn, ignore code formatting, that way we can drop a huge dependency which enables the next steps
	 * 
	 * 2. Turn this into a single .dll or so that can be dropped right into Unity. It would listen to compile events, 
	 *    and then re-generate the formatters automatically! (just need to take care that it doesn't trigger itself by doing that).
	 *    That would be much more comfortable, and that's very important! Comfort is one of the core ideals Ceras is built with.
	 *    
	 * 3. Allow specifying some settings, like:
	 *    - GeneratedOutputFilePath
	 *    - ClassVisibility = public/internal
	 *    - maybe even List<string> TargetTypeNames;
	 *    - ...
	 */

    public class Generator
	{
		static readonly Type Marker = typeof(GenerateFormatterAttribute);

		public static void Generate(IEnumerable<Assembly> asms, StringBuilder output)
		{
			if (!_resolverRegistered)
				throw new InvalidOperationException("please call RegisterAssemblyResolver() first so you can get better error messages when a DLL can't be loaded!");

			var (ceras, targets) = CreateSerializerAndTargets(asms);
			SourceFormatterGenerator.GenerateAll(targets, ceras, output);
		}

		static (CerasSerializer, List<Type>) CreateSerializerAndTargets(IEnumerable<Assembly> asms)
		{
			// Find config method and create a SerializerConfig
			SerializerConfig config = new SerializerConfig();
			var configMethods = asms.SelectMany(a => a.GetTypes())
									.SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
									.Where(m => m.GetCustomAttribute<AotSerializerConfigAttribute>() != null)
									.ToArray();
			if (configMethods.Length > 1)
				throw new Exception("Found more than one method with the CerasAutoGenConfig attribute!");
			if (configMethods.Length == 1)
				config = (SerializerConfig)configMethods[0].Invoke(null, null);

			var ceras = new CerasSerializer(config);


			// Start with KnownTypes and user-marked types...
			HashSet<Type> newTypes = new HashSet<Type>();
			newTypes.AddRange(config.KnownTypes);
			newTypes.AddRange(asms.SelectMany(a => a.GetTypes()).Where(t => !t.IsAbstract && IsMarkedForAot(t)));


			// Go through each type, add all the member-types it wants to serialize as well
			HashSet<Type> processedTypes = new HashSet<Type>();

			while (newTypes.Any())
			{
				// Get first, remove from "to explore" list, and add it to the "done" list.
				var t = newTypes.First();

				if(t.IsArray)
					t = t.GetElementType();

				newTypes.Remove(t);
				processedTypes.Add(t);

				if (CerasSerializer.IsPrimitiveType(t))
					// Skip int, string, Type, ...
					continue;

				if (t.IsAbstract || t.ContainsGenericParameters)
					// Can't explore abstract or open generics
					continue;

				// Explore the type, add all member types
				var schema = ceras.GetTypeMetaData(t).PrimarySchema;

				foreach (var member in schema.Members)
					if (!processedTypes.Contains(member.MemberType))
						newTypes.Add(member.MemberType);
			}


			// Only leave things that use DynamicFormatter, or have the marker attribute
			List<Type> targets = new List<Type>();
			List<Type> debugIgnoreList = new List<Type>(); // list for types that were ignored

			foreach (var t in processedTypes)
			{
				if (CerasSerializer.IsPrimitiveType(t))
					continue; // Skip int, string, Type, ...

				if (t.IsAbstract || t.ContainsGenericParameters)
					continue; // Abstract or open generics can't have instances...

				var formatter = ceras.GetSpecificFormatter(t);
				var formatterType = formatter.GetType();

				if (CerasHelpers.IsDynamicFormatter(formatterType) || CerasHelpers.IsSchemaDynamicFormatter(formatterType))
					targets.Add(t); // handled by Schema/DynamicFormatter; which definitely won't be available later...
				else if (IsMarkedForAot(t))
					targets.Add(t); // explicitly marked by the user
				else
					debugIgnoreList.Add(t);
			}

			return (ceras, targets);
			
			bool IsMarkedForAot(Type t)
			{
				if (t.GetCustomAttributes(true).Any(a => a.GetType().FullName == Marker.FullName))
					return true; // has 'Generate Formatter' attribute
				return false;
			}
		}



		static bool _resolverRegistered = false;
		public static void RegisterAssemblyResolver()
		{
			if (_resolverRegistered)
				return;
			AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
			_resolverRegistered = true;
		}

		static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
		{
			var unityHubDir = @"C:\Program Files\Unity\Hub\Editor";
			if (Directory.Exists(unityHubDir))
			{
				var r = SearchAssembly(unityHubDir, args.Name);
				if (r != null)
					return r;
			}

			Console.WriteLine($"Trying to load assembly '{args.Name}' (requested by '{args.RequestingAssembly.FullName}')");

			return null;
		}

		static Assembly SearchAssembly(string directory, string assemblyFullName)
		{
			foreach (var dllPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
			{
				try
				{
					var asmName = AssemblyName.GetAssemblyName(dllPath);
					if (asmName.FullName == assemblyFullName)
						return Assembly.LoadFrom(dllPath);
				}
				catch (BadImageFormatException badImgEx)
				{
					Console.WriteLine($"Skipping module \"{dllPath}\" (BadImageFormat: probably not a .NET dll)");
				}
			}

			return null;
		}
	}
}
