using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ceras.SourceGenerator;

namespace Ceras.Test.Generator
{
    /// <summary>
    /// Runs the real source generator over a one-file compilation, so a test can assert on the
    /// diagnostics and on the sources it emits. Shared by the diagnostic test classes: the harness has to
    /// reproduce what the compiler does (the compilation's own references, the C# language version), so a
    /// second copy of it would be a second thing that can drift from the real build.
    /// </summary>
    internal static class GeneratorTestHarness
    {
        /// <summary>
        /// The diagnostics the generator reported, and the sources it generated. The generated output is
        /// NOT fed back into the compilation: a test that needs to know whether the emitted code compiles
        /// asserts on its text (or builds a real project), because a generator cannot observe that.
        /// </summary>
        public static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<(string Hint, string Text)> Sources)
            Run(string source, string assemblyName = "CerasGeneratorHarnessTest", bool allowUnsafe = false)
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            // The Ceras assembly is the one the generator reads attributes from; it is referenced
            // explicitly in case the test host has not loaded it yet, and only once either way.
            var cerasLocation = typeof(Ceras.CerasSerializer).Assembly.Location;
            if (!refs.OfType<PortableExecutableReference>().Any(r => r.FilePath == cerasLocation))
                refs.Add(MetadataReference.CreateFromFile(cerasLocation));

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14)) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe));

            var ran = CSharpGeneratorDriver.Create(new CerasFormatterGenerator().AsSourceGenerator())
                .RunGenerators(compilation)
                .GetRunResult();

            return (ran.Diagnostics, ran.Results
                .SelectMany(r => r.GeneratedSources.Select(s => (s.HintName, s.SourceText.ToString())))
                .ToList());
        }

        public static IReadOnlyList<Diagnostic> Diagnostics(string source, bool allowUnsafe = false)
            => Run(source, allowUnsafe: allowUnsafe).Diagnostics;

        /// <summary>The distinct diagnostic ids, in report order.</summary>
        public static IReadOnlyList<string> DiagnosticIds(string source, bool allowUnsafe = false)
            => Diagnostics(source, allowUnsafe).Select(d => d.Id).Distinct().ToList();

        /// <summary>The hint names of the generated files, in emission order.</summary>
        public static IReadOnlyList<string> GeneratedHintNames(string source)
            => Run(source).Sources.Select(s => s.Hint).ToList();

        /// <summary>
        /// The generated source whose hint name contains <paramref name="hintFragment"/>, or null when the
        /// generator emitted no such file.
        /// </summary>
        public static string GeneratedSourceContaining(string source, string hintFragment)
        {
            foreach (var generated in Run(source).Sources)
                if (generated.Hint.Contains(hintFragment, StringComparison.Ordinal))
                    return generated.Text;
            return null;
        }
    }
}
