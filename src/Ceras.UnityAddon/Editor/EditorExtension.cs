namespace Ceras.Editor
{
	using System.IO;
	using System.Linq;
	using System.Reflection;
	using System.Text;
	using UnityEditor;
	using UnityEditor.Callbacks;
	using CerasAotFormatterGenerator;
	using System;
    using System.Collections.Generic;
    using UnityEngine;

    public static class CerasUnityEditorTools
	{
		public static string OutputCsFileName = "Assets/Scripts/CerasAotFormattersGenerated.cs";
		public static bool ShowMessages = true;

		[DidReloadScripts, MenuItem("Tools/Ceras/Generate AOT Formatters")]
		public static void GenerateAotFormatters()
		{
			// Prepare AotGenerator
			Generator.RegisterAssemblyResolver();

			// Load all assemblies
			var assemblies = GetProjectAssemblies();

			// Generate source-code for all formatters
			var sb = new StringBuilder(25 * 1000);
			Generator.Generate(assemblies, sb);
			var output = sb.ToString();

			// Check if the output is already exactly the same
			if (File.Exists(OutputCsFileName) && File.ReadAllText(OutputCsFileName) == output)
				return; // File didn't change at all, no need to write

			// Ensure target directory exists
			var dir = Path.GetDirectoryName(OutputCsFileName);
			Directory.CreateDirectory(dir);

			// Write code to .cs file
			File.WriteAllText(OutputCsFileName, output);

			// Tell Unity to reload the file
			AssetDatabase.ImportAsset(OutputCsFileName);
		}

		static List<Assembly> GetProjectAssemblies()
		{
			List<Assembly> assemblies = new List<Assembly>();

			var playerAssemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player);

			foreach(var unityAsm in playerAssemblies)
			{
				var name = Path.GetFileName(unityAsm.outputPath);

				if(name.StartsWith("Ceras."))
					continue; // Ceras.dll, Ceras.AotGenerator.dll, ...

				if(name.StartsWith("Unity.") || name.StartsWith("UnityEditor.") || name.StartsWith("UnityEngine."))
					continue;

				var path = Path.GetFullPath(unityAsm.outputPath);
				var asm = Assembly.LoadFrom(path);

				assemblies.Add(asm);
			}

			if(ShowMessages)
				Debug.Log("[CerasAot] Inspected Assemblies: " + string.Join(", ", assemblies.Select(a => a.GetName().Name)));
			
			return assemblies;
		}
	}
}
