using System;

namespace CerasAotFormatterGenerator
{
	using System.IO;
	using System.Linq;
	using System.Reflection;
	using System.Text;
	using System.Threading;

	class Program
	{
		static void Main(string[] args)
		{
			if (args.Length < 2)
			{
				var error = "Not enough arguments. The last argument is always the .cs file output path, all arguments before that are the input assemblies (.dll files) of your unity project. Example: \"C:\\MyUnityProject\\Temp\\bin\\Debug\\Assembly-CSharp.dll C:\\MyUnityProject\\Assets\\Scripts\\GeneratedFormatters.cs\"";

				Console.WriteLine(error);
				throw new ArgumentException(error);
			}

			var inputAssemblies = args.Reverse().Skip(1).Reverse().ToArray();
			var outputCsFileName = args.Reverse().First();

			Generator.RegisterAssemblyResolver();

			var asms = inputAssemblies.Select(Assembly.LoadFrom);
			StringBuilder fullCode = new StringBuilder(25 * 1000);
			Generator.Generate("AotSerialization", asms, fullCode);

			Console.WriteLine($"Saving...");

			using (var fs = File.OpenWrite(outputCsFileName))
			using (var w = new StreamWriter(fs))
			{
				fs.SetLength(0);
				w.Write(fullCode.ToString());
			}



			// todo: maybe we'll generate an assembly instead of source code at some pointlater...
			//GenerateFormattersAssembly(targets);

			Thread.Sleep(300);
			Console.WriteLine($"> Done!");
		}
	}
}

