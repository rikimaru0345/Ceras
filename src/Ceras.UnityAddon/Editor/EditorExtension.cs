namespace Ceras.Editor
{
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Reflection;
	using UnityEditor;
	using CerasAotFormatterGenerator;

	public static class CerasUnityEditorTools
	{
		const string outputCsFileName = "Assets/Scripts/CerasAotFormattersGenerated.cs";

		[MenuItem("Tools/Ceras/Generate AOT Formatters")]
		public static void GenerateAotFormatters()
		{
			var executingAssembly = Assembly.GetExecutingAssembly();
			var asms = executingAssembly.GetReferencedAssemblies().Select(Assembly.Load).Append(executingAssembly);
			var sb = new StringBuilder(25 * 1000);
			Generator.Generate(asms, sb);
			File.WriteAllText(outputCsFileName, sb.ToString());
			AssetDatabase.ImportAsset(outputCsFileName);
		}
	}
}
