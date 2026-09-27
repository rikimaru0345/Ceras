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

        // ----------------------------------------------------------------------------------------
        // CASE A — generated formatter field-name collisions.
        //
        // MakeFormatterFieldName sanitizes the fully-qualified display: 'A.B_C' and the nested
        // 'A.B.C' both become '_A_B_CFormatter'. Here the second type is a reference-type collection,
        // so it is skipped from formatter generation (no CERAS022) and no member references it, which
        // isolates the field-name collision to exactly one declaring type.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void FormatterFieldNameCollision_EmitsCERAS020()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable]
    public class Holder
    {
        public A.B_C First;
        public A.B.C Second;
    }
}

namespace A
{
    [CerasSerializable]
    public class B_C { public int X; }

    [CerasSerializable]
    public class B
    {
        [CerasSerializable]
        public class C : System.Collections.Generic.List<int> { }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS020", ids);
            // The collection type is skipped before it can claim a generated name, and a reference-type
            // collection is deliberately excluded from the CASE B check, so this isolates CERAS020.
            Assert.DoesNotContain("CERAS021", ids);
            Assert.DoesNotContain("CERAS022", ids);
        }

        [Fact]
        public void DistinctMemberTypes_DoNotEmitCERAS020()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable]
    public class First { public int X; }

    [CerasSerializable]
    public class Second { public int Y; }

    [CerasSerializable]
    public class Holder
    {
        public First A;
        public Second B;
        public int Number;
        public System.Collections.Generic.List<int> Items;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS020", ids);
        }

        // ----------------------------------------------------------------------------------------
        // CASE A — generated class / source-hint name collisions (two distinct types sanitize to the
        // same identifier even though no single formatter declares both as members).
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void GeneratedNameCollision_EmitsCERAS022()
        {
            const string source = @"
using Ceras;
namespace A
{
    [CerasSerializable]
    public class B_C { public int X; }

    [CerasSerializable]
    public class B
    {
        [CerasSerializable]
        public class C { public int Y; }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS022", ids);
            // No single formatter declares both colliding types as members, so this is purely the
            // global generated-name collision (class name / source hint).
            Assert.DoesNotContain("CERAS020", ids);
        }

        [Fact]
        public void DistinctGeneratedNames_DoNotEmitCERAS022()
        {
            const string source = @"
using Ceras;
namespace A
{
    [CerasSerializable]
    public class B_C { public int X; }

    [CerasSerializable]
    public class B
    {
        [CerasSerializable]
        public class D { public int Y; }
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS022", ids);
        }

        // ----------------------------------------------------------------------------------------
        // CASE B — a member references a [CerasSerializable] type for which no formatter will be
        // generated because Ceras cannot construct it.
        // ----------------------------------------------------------------------------------------
        [Fact]
        public void Member_ReferencingUnconstructableSerializableType_EmitsCERAS021()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable]
    public class NoCtor
    {
        public int Value;
        public NoCtor(int somethingElse) { }
    }

    [CerasSerializable]
    public class Holder
    {
        public NoCtor Member;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.Contains("CERAS021", ids);
        }

        [Fact]
        public void Member_ReferencingConstructableSerializableType_DoesNotEmitCERAS021()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasSerializable]
    public class Nested { public int X; }

    [CerasSerializable]
    public class Holder
    {
        public Nested Member;
        public int Number;
        public System.Collections.Generic.List<int> Items;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS021", ids);
        }

        // A member whose type has no [CerasSerializable] attribute is treated as user-provided (a custom
        // formatter supplies it), so no generated formatter is required and CASE B must not fire.
        [Fact]
        public void Member_ReferencingCustomFormatterType_DoesNotEmitCERAS021()
        {
            const string source = @"
using Ceras;
namespace T
{
    public class External { public int X; }

    [CerasSerializable]
    public class Holder
    {
        public External Member;
    }
}";

            var ids = RunGeneratorDiagnosticIds(source);
            Assert.DoesNotContain("CERAS021", ids);
        }
    }
}
