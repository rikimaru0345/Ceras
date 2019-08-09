namespace Ceras.Editor
{
	using System.IO;
	using System.Linq;
	using System.Reflection;
	using System.Text;
	using UnityEditor;
	using UnityEditor.Callbacks;
	using CerasAotFormatterGenerator;

	public static class CerasUnityEditorTools
	{
		const string outputCsFileName = "Assets/Scripts/CerasAotFormattersGenerated.cs";

		[DidReloadScripts, MenuItem("Tools/Ceras/Generate AOT Formatters")]
		public static void GenerateAotFormatters()
		{
			// Prepare AotGenerator
			Generator.RegisterAssemblyResolver();

			// Load all assemblies
			var executingAssembly = Assembly.GetExecutingAssembly();
			var asms = executingAssembly.GetReferencedAssemblies().Select(Assembly.Load).Append(executingAssembly);

			// Generate source-code for all formatters
			var sb = new StringBuilder(25 * 1000);
			Generator.Generate(asms, sb);
			var output = sb.ToString();

			// Check if the output is already exactly the same
			if (File.Exists(outputCsFileName) && File.ReadAllText(outputCsFileName) == output)
				return; // File didn't change at all, no need to write

			// Ensure target directory exists
			var dir = Path.GetDirectoryName(outputCsFileName);
			Directory.CreateDirectory(dir);

			// Write code to .cs file
			File.WriteAllText(outputCsFileName, output);

			// Tell Unity to reload the file
			AssetDatabase.ImportAsset(outputCsFileName);
		}
	}
}
