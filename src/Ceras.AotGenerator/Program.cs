using System;

namespace CerasAotFormatterGenerator
{
	using System.Collections.Generic;
	using System.Collections.Immutable;
	using Ceras;
	using System.IO;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.Security.Permissions;
	using System.Text;
	using System.Threading;
	using Ceras.Formatters;
	using Ceras.Formatters.AotGenerator;
	using Microsoft.CodeAnalysis.CSharp;
	using Microsoft.CodeAnalysis.Text;
	using Microsoft.CodeAnalysis;
	using Microsoft.CodeAnalysis.CodeFixes;
	using Microsoft.CodeAnalysis.CSharp.Formatting;
	using Microsoft.CodeAnalysis.Diagnostics;
	using Microsoft.CodeAnalysis.Formatting;
	using Microsoft.CodeAnalysis.MSBuild;
	using Microsoft.CodeAnalysis.Options;

	class Program
	{
		static string[] inputAssemblies;
		static string outputCsFileName;

		static void Main(string[] args)
		{
			if (args.Length < 2)
			{
				var error = "Not enough arguments. The last argument is always the .cs file output path, all arguments before that are the input assemblies (.dll files) of your unity project. Example: \"C:\\MyUnityProject\\Temp\\bin\\Debug\\Assembly-CSharp.dll C:\\MyUnityProject\\Assets\\Scripts\\GeneratedFormatters.cs\"";

				Console.WriteLine(error);
				throw new ArgumentException(error);
			}

			inputAssemblies = args.Reverse().Skip(1).Reverse().ToArray();
			outputCsFileName = args.Reverse().First();


			AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
			
			var asms = inputAssemblies.Select(Assembly.LoadFrom);

			var (ceras, targets) = CreateSerializerAndTargets(asms);

			
			
			StringBuilder fullCode = new StringBuilder(25 * 1000);
			fullCode.AppendLine("using Ceras;");
			fullCode.AppendLine("using Ceras.Formatters;");
			fullCode.AppendLine("namespace Ceras.GeneratedFormatters");
			fullCode.AppendLine("{");

			var setCustomFormatters = targets.Select(t => $"config.ConfigType<{t.FullName}>().CustomFormatter = new {t.Name}Formatter();");
			fullCode.AppendLine($@"
static class GeneratedFormatters
{{
	internal static void UseFormatters(SerializerConfig config)
	{{
		{string.Join("\n", setCustomFormatters)}
	}}
}}
");

			foreach (var t in targets)
				SourceFormatterGenerator.Generate(t, ceras, fullCode);
			fullCode.AppendLine("}");
			
			Console.WriteLine($"Parsing...");

			var syntaxTree = CSharpSyntaxTree.ParseText(fullCode.ToString());
			
			Console.WriteLine($"Formatting...");
			
			var workspace = new AdhocWorkspace();
			var options = workspace.Options
								   .WithChangedOption(CSharpFormattingOptions.IndentBlock, true)
								   .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAccessors, true)
								   .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInControlBlocks, true)
								   .WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInTypes, true)
								   .WithChangedOption(CSharpFormattingOptions.IndentBraces, false);

			syntaxTree = Formatter.Format(syntaxTree.GetRoot(), workspace, options).SyntaxTree;

			Console.WriteLine($"Saving...");

			using (var fs = File.OpenWrite(outputCsFileName))
			using (var w = new StreamWriter(fs))
			{
				fs.SetLength(0);
				w.WriteLine(syntaxTree.ToString());
			}

			

			// todo: maybe we'll generate an assembly instead of source code at some pointlater...
			//GenerateFormattersAssembly(targets);

			Thread.Sleep(300);
			Console.WriteLine($"> Done!");
		}

		static (CerasSerializer, List<Type>) CreateSerializerAndTargets(IEnumerable<Assembly> asms)
		{
			// Set of types we've yet to explore
			HashSet<Type> newTypes = new HashSet<Type>();


			// Create Ceras instance first
			// Find config method and create a SerializerConfig
			SerializerConfig config = new SerializerConfig();
			var configMethods = asms.SelectMany(a=>a.GetTypes())
									.SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
									.Where(m => m.GetCustomAttribute<CerasAutoGenConfigAttribute>() != null)
									.ToArray();
			if (configMethods.Length > 1)
				throw new Exception("Found more than one config method!");
			if (configMethods.Length == 1)
				config = (SerializerConfig)configMethods[0].Invoke(null, null);

			newTypes.AddRange(config.KnownTypes);

			var ceras = new CerasSerializer(config);


			//
			// Also find all types that are marked by the attribute
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

				foreach(var member in schema.Members)
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


		static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
		{
			var unityHubDir = @"C:\Program Files\Unity\Hub\Editor";
			if (Directory.Exists(unityHubDir))
			{
				var r = SearchAssembly(unityHubDir, args.Name);
				if (r != null)
					return r;
			}

			return null;
		}

		static Assembly SearchAssembly(string path, string name)
		{
			foreach (var dllPath in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories))
			{
				var asmName = AssemblyName.GetAssemblyName(dllPath);
				if (asmName.FullName == name)
					return Assembly.LoadFrom(dllPath);
			}

			return null;
		}

	}
}
