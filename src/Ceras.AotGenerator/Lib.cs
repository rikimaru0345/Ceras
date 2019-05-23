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

	public class Lib
	{
		public static void Generate(IEnumerable<Assembly> asms, StringBuilder output)
		{
			var (ceras, targets) = CreateSerializerAndTargets(asms);
			SourceFormatterGenerator.GenerateAll(targets, ceras, output);
		}

		static (CerasSerializer, List<Type>) CreateSerializerAndTargets(IEnumerable<Assembly> asms)
		{
			// Find config method and create a SerializerConfig
			SerializerConfig config = new SerializerConfig();
			var configMethods = asms.SelectMany(a => a.GetTypes())
									.SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
									.Where(m => m.GetCustomAttribute<CerasAutoGenConfigAttribute>() != null)
									.ToArray();
			if (configMethods.Length > 1)
				throw new Exception("Found more than one method with the CerasAutoGenConfig attribute!");
			if (configMethods.Length == 1)
				config = (SerializerConfig)configMethods[0].Invoke(null, null);

			config.VersionTolerance.Mode = VersionToleranceMode.Disabled; // ensure VersionTolerance is off so we don't accidentally get 'SchemaDynamicFormatter'
			var ceras = new CerasSerializer(config);


			// Start with KnownTypes...
			HashSet<Type> newTypes = new HashSet<Type>();
			newTypes.AddRange(config.KnownTypes);

			// And also include all marked types
			var marker = typeof(CerasAutoGenFormatterAttribute);

			bool HasMarker(Type t) => t.GetCustomAttributes(true)
									   .Any(a => a.GetType().FullName == marker.FullName);

			var markedTargets = asms.SelectMany(a => a.GetTypes())
									.Where(t => !t.IsAbstract && HasMarker(t));

			newTypes.AddRange(markedTargets);


			// Go through each type, add all the member-types it wants to serialize as well
			HashSet<Type> allTypes = new HashSet<Type>();

			while (newTypes.Any())
			{
				// Get first, remove from "to explore" list, and add it to the "done" list.
				var t = newTypes.First();
				newTypes.Remove(t);
				allTypes.Add(t);


				if (CerasSerializer.IsPrimitiveType(t))
					// Skip int, string, Type, ...
					continue;

				if (t.IsAbstract || t.ContainsGenericParameters)
					// Can't explore abstract or open generics
					continue;

				// Explore the type, add all member types
				var schema = ceras.GetTypeMetaData(t).PrimarySchema;

				foreach (var member in schema.Members)
					if (!allTypes.Contains(member.MemberType))
						newTypes.Add(member.MemberType);
			}


			// Only leave things that use DynamicFormatter, or have the marker attribute
			List<Type> targets = new List<Type>();

			foreach (var t in allTypes)
			{
				var f = ceras.GetSpecificFormatter(t);
				var fType = f.GetType();

				if (fType.IsGenericType && fType.GetGenericTypeDefinition().Name == typeof(DynamicFormatter<int>).GetGenericTypeDefinition().Name)
					targets.Add(t);
				else if (HasMarker(t))
					targets.Add(t);
			}

			return (ceras, targets);
		}
	}
}
