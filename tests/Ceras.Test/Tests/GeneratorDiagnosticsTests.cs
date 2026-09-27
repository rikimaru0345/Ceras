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
    /// Pins the CERAS018 boundary: a private member with a nameable type must be accessed through a
    /// plain <c>[UnsafeAccessor]</c> and must NOT trip CERAS018; only a member whose *type* generated
    /// code cannot name should. (No reflection fallback is added for these shapes.)
    /// </summary>
    public class GeneratorDiagnosticsTests
    {
        static IReadOnlyList<string> RunGeneratorDiagnosticIds(string source)
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var cerasLocation = typeof(Ceras.CerasSerializer).Assembly.Location;
            if (!refs.OfType<PortableExecutableReference>().Any(r => r.FilePath == cerasLocation))
                refs.Add(MetadataReference.CreateFromFile(cerasLocation));

            var compilation = CSharpCompilation.Create(
                "CerasGeneratorDiagTest",
                new[] { CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var generator = new CerasFormatterGenerator();
            var driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
            var ran = driver.RunGenerators(compilation);

            return ran.GetRunResult().Results
                .SelectMany(r => r.Diagnostics)
                .Select(d => d.Id)
                .Distinct()
                .ToList();
        }

        [Fact]
        public void PrivateMember_WithNameableType_DoesNotEmitCERAS018()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable]
    public class Holder
    {
        private int _value;
        public Holder(int value) { _value = value; }
        public int Value => _value;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS018", ids);
        }

        [Fact]
        public void Member_WithInaccessibleType_EmitsCERAS018()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable]
    public class Holder
    {
        private Secret _secret;
        private class Secret { public int X; }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS018", ids);
        }

        [Fact]
        public void TwoTypes_WithSameExplicitStableName_EmitCERAS019()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable(""Dup"")]
    public class A { public int X; }

    [CerasSerializable(""Dup"")]
    public class B { public int Y; }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS019", ids);
        }
    }
}
