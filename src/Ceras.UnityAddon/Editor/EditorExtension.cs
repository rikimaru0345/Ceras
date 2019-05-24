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
			var executingAssembly = Assembly.GetExecutingAssembly();
			var asms = executingAssembly.GetReferencedAssemblies().Select(Assembly.Load).Append(executingAssembly);
			var sb = new StringBuilder(25 * 1000);
			Generator.Generate(asms, sb);
			var output = sb.ToString();
			if (File.Exists(outputCsFileName) && File.ReadAllText(outputCsFileName) == output)
				return;

			File.WriteAllText(outputCsFileName, output);
			AssetDatabase.ImportAsset(outputCsFileName);
		}
	}
}
