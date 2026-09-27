using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ceras.SourceGenerator;
using Xunit;

namespace Ceras.Test.Generator
{
    /// <summary>
    /// The platform is pinned to C# 14 (global.json SDK 10.0.111, the root Directory.Build.props and
    /// src/Ceras.SourceGenerator/Ceras.SourceGenerator.csproj), and the C# this generator emits is
    /// consumed by ordinary projects at that same version. Every build of this test project already
    /// compiles the generated code, but only at whatever LangVersion the project happens to resolve;
    /// here the parse options state C# 14 explicitly, so emitted code that needs a newer or older
    /// language version is a test failure instead of a surprise in some future build. Emitted code
    /// currently needs nothing above C# 8.0 (the <c>#nullable</c> directives), so this is headroom,
    /// not a squeeze.
    /// </summary>
    public class GeneratedCodeLanguageVersionTests
    {
        /// <summary>
        /// A model that reaches the emitters the platform cares about: fixed-width primitives, the
        /// BCL shapes with hand-written formatters, jagged and nested generics, user generics,
        /// polymorphism, a struct, an enum and auto-properties. Deliberately free of unusual names;
        /// hostile names are a separate concern from the language version.
        /// </summary>
        const string Model = @"
using System;
using System.Collections.Generic;
using Ceras;

namespace LangProbe
{
    public enum Kind : byte { A = 0, B = 1 }

    [CerasSerializable]
    public class Base { public int Id; }

    [CerasSerializable]
    public class Derived : Base { public string Name; }

    [CerasSerializable]
    public struct Point { public int X; public int Y; }

    [CerasSerializable]
    public class Box<T> { public T Value; public List<T> Items; }

    [CerasSerializable]
    [CerasContext(""LanguageProbe"")]
    public class Root
    {
        public bool Flag;
        public int Number;
        public long Big;
        public double Real;
        public decimal Money;
        public Guid Id;
        public DateTime Moment;
        public string Text;
        public int? Maybe;
        public Kind Kind;
        public Point Point;
        public int[] Numbers;
        public int[][] Jagged;
        public List<string> Names;
        public Dictionary<string, int> Lookup;
        public HashSet<int> Unique;
        public Queue<Point> Points;
        public Base Polymorphic;
        public List<Base> PolymorphicList;
        public Box<Point> Boxed;
        public int Computed { get; set; }
    }
}";

        [Fact]
        public void GeneratedCode_IsValidAtThePinnedCSharp14()
        {
            // The pinned consumer language version, stated rather than inherited.
            var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp14);

            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var cerasLocation = typeof(Ceras.CerasSerializer).Assembly.Location;
            if (!refs.OfType<PortableExecutableReference>().Any(r => r.FilePath == cerasLocation))
                refs.Add(MetadataReference.CreateFromFile(cerasLocation));

            var compilation = CSharpCompilation.Create(
                "CerasLanguageVersionTest",
                new[] { CSharpSyntaxTree.ParseText(Model, parseOptions) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(
                new[] { new CerasFormatterGenerator().AsSourceGenerator() },
                parseOptions: parseOptions);

            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
                compilation, out var outputCompilation, out var generatorDiagnostics);

            Assert.Empty(Where(generatorDiagnostics));

            var generated = driver.GetRunResult().Results
                .SelectMany(r => r.GeneratedSources)
                .ToList();

            // A generator that stops emitting would otherwise pass the check below vacuously.
            Assert.Contains(generated, g => g.HintName.Contains("LangProbe_Root"));
            Assert.True(generated.Count >= 3, "expected the root formatter, the robust formatters and the generic helper");

            // Every emitted file has to be valid C# 14 on its own, and has to bind: a formatter that
            // names something the compiler cannot resolve is as broken as one that does not parse.
            var generatedTrees = new HashSet<SyntaxTree>(generated.Select(g => g.SyntaxTree));
            Assert.Empty(generated.SelectMany(g => Where(g.SyntaxTree.GetDiagnostics())));

            var errorsInGeneratedCode = outputCompilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error
                            && d.Location != null
                            && d.Location.SourceTree != null
                            && generatedTrees.Contains(d.Location.SourceTree));
            Assert.Empty(errorsInGeneratedCode);
        }

        static IEnumerable<Diagnostic> Where(IEnumerable<Diagnostic> diagnostics)
        {
            return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
        }
    }
}
