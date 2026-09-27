using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Ceras.Test.Generator
{
    /// <summary>
    /// TODO P0 0.8.10: every string literal the generator writes must go through
    /// <c>TypeHelpers.QuoteLiteral</c>, because almost all of them are user-written attribute text.
    /// <para>
    /// These tests compile a real generated assembly and inspect what it actually carries, never the
    /// generated text, so an escaping scheme that is valid C# but decodes to a different string still
    /// fails here.
    /// </para>
    /// </summary>
    public class GeneratedStringLiteralTests
    {
        // The escaped source text that goes into the compilation (a raw string literal, so its
        // backslashes are literal), and the value the inner compiler decodes it to. Together they attack
        // quote, backslash, CR, LF, TAB, NUL and an unpaired surrogate.
        //
        // The lone surrogate cannot be a constant in the expected value: a compile-time constant holding
        // an unpaired surrogate has to be encoded into this assembly's metadata, so it is concatenated at
        // run time instead.
        const char LoneSurrogate = (char)0xD800;

        const string StableNameText = """Stable\"Type\\Name\n\r\t\0Z\ud800Q""";
        static readonly string StableName = "Stable\"Type\\Name\n\r\t\0Z" + LoneSurrogate + "Q";

        const string PreviousTypeNameText = """Prev\"Type\\Alias\n\r\tQ""";
        const string PreviousTypeName = "Prev\"Type\\Alias\n\r\tQ";

        const string MemberStableNameText = """F\"ield\\Name\n\t\0Q""";
        const string MemberStableName = "F\"ield\\Name\n\t\0Q";

        const string MemberPreviousNameText = """Old\"Field\\Name\n\tQ""";
        const string MemberPreviousName = "Old\"Field\\Name\n\tQ";

        // The stable name without the lone surrogate, for the one test that goes through a real file: a
        // robust file's schema and payload string tables are UTF-8, which cannot represent an unpaired
        // surrogate (it would be written as U+FFFD and never match again). That is a limit of the file
        // format, not of the generator's quoting, which is why only the file-writing test needs this.
        const string PortableStableNameText = """Stable\"Type\\Name\n\r\t\0ZQ""";
        const string PortableStableName = "Stable\"Type\\Name\n\r\t\0ZQ";

        // The same shape before and after a rename: the hostile names move from "current" to "previous",
        // which is the only configuration in which they are used rather than merely stored. OrdinaryType
        // is the control - a name that needs no escaping must not be escaped into something that merely
        // decodes back to the same value.
        const string OriginalTemplate = """
            using Ceras;

            namespace Hostile
            {
                [CerasContext("Hostile")]
                [CerasSerializable("__STABLE__")]
                [PreviousTypeName("__PREVTYPE__")]
                public class HostileType
                {
                    [CerasStableName("__MEMBER__")]
                    public int Number;

                    [PreviousName("__MEMBERPREV__", "Another\"Previous")]
                    public string Text;
                }

                [CerasContext("Ordinary")]
                [CerasSerializable("Plain.Control.Type")]
                public class OrdinaryType
                {
                    [CerasStableName("PlainMember")]
                    public int Number;
                }
            }
            """;

        const string RenamedTemplate = """
            using Ceras;

            namespace Hostile
            {
                [CerasContext("Hostile")]
                [CerasSerializable("New.Stable.Type")]
                [PreviousTypeName("__STABLE__")]
                public class RenamedType
                {
                    [CerasStableName("Amount")]
                    [PreviousName("__MEMBER__")]
                    public int Number;

                    public string Text;
                }
            }
            """;

        // A placeholder that a template does not contain is never substituted, so one builder serves both.
        static readonly string HostileSource = Expand(OriginalTemplate, StableNameText);
        static readonly string PortableHostileSource = Expand(OriginalTemplate, PortableStableNameText);
        static readonly string PortableRenamedSource = Expand(RenamedTemplate, PortableStableNameText);

        static string Expand(string template, string stableName) => template
            .Replace("__STABLE__", stableName)
            .Replace("__PREVTYPE__", PreviousTypeNameText)
            .Replace("__MEMBER__", MemberStableNameText)
            .Replace("__MEMBERPREV__", MemberPreviousNameText);

        [Fact]
        public void HostileAliases_CompileAndCarryTheExactSchemaIdentities()
        {
            var generated = _hostile.Value;

            AssertNoErrors(generated, "the generator or the generated code reported errors");

            var hostile = GetSchema(generated.Assembly, "Hostile.HostileType");
            Assert.Equal(StableName, hostile.StableName);
            Assert.Equal(new[] { PreviousTypeName }, hostile.PreviousTypeNames);

            var number = Assert.Single(hostile.Fields, f => f.TypeName == "System.Int32");
            Assert.Equal(MemberStableName, number.StableName);
            Assert.Empty(number.PreviousNames);

            var text = Assert.Single(hostile.Fields, f => f.TypeName == "System.String");
            Assert.Equal("Text", text.StableName);
            Assert.Equal(new[] { MemberPreviousName, "Another\"Previous" }, text.PreviousNames);

            // The control pins the other half of the contract: a name that needs no escaping must not be
            // escaped into something that merely decodes back to the same value.
            var ordinary = GetSchema(generated.Assembly, "Hostile.OrdinaryType");
            Assert.Equal("Plain.Control.Type", ordinary.StableName);
            Assert.Equal(new[] { "PlainMember" }, ordinary.Fields.Select(f => f.StableName));
        }

        [Fact]
        public void HostileAliases_RoundTripThroughTheGeneratedFormatters()
        {
            var generated = _hostile.Value;
            var hostType = generated.Assembly.GetType("Hostile.HostileType")!;

            var serializer = CreateSerializer(generated.Assembly);
            // No lone surrogate in the payload: payload strings travel through the same UTF-8 table.
            string payload = "quotes \" backslash \\ newline \n tab \t nul \0 done";
            object original = Activator.CreateInstance(hostType);
            hostType.GetField("Number")!.SetValue(original, 4242);
            hostType.GetField("Text")!.SetValue(original, payload);

            object clone = Load(serializer, hostType, Save(serializer, original));

            Assert.Equal(4242, hostType.GetField("Number")!.GetValue(clone));
            Assert.Equal(payload, hostType.GetField("Text")!.GetValue(clone));
        }

        [Fact]
        public void HostileAliases_ResolveAnOldSaveThroughTheRenamedSchema()
        {
            var oldRun = _oldFile.Value;
            var newRun = _renamedFile.Value;
            var oldType = oldRun.Assembly.GetType("Hostile.HostileType")!;
            var newType = newRun.Assembly.GetType("Hostile.RenamedType")!;

            var writer = CreateSerializer(oldRun.Assembly);
            object original = Activator.CreateInstance(oldType);
            oldType.GetField("Number")!.SetValue(original, 7);
            oldType.GetField("Text")!.SetValue(original, "old payload");
            byte[] bytes = Save(writer, original);

            // The aliases have to resolve, not merely be spelled: the file's type and field identities are
            // matched against this manifest's [PreviousTypeName]/[PreviousName] by the migration engine.
            var reader = CreateSerializer(newRun.Assembly);
            object migrated = Load(reader, newType, bytes);
            Assert.Equal(7, newType.GetField("Number")!.GetValue(migrated));
            Assert.Equal("old payload", newType.GetField("Text")!.GetValue(migrated));

            var renamed = GetSchema(newRun.Assembly, "Hostile.RenamedType");
            Assert.Equal(new[] { PortableStableName }, renamed.PreviousTypeNames);
            var amount = Assert.Single(renamed.Fields, f => f.TypeName == "System.Int32");
            Assert.Equal("Amount", amount.StableName);
            Assert.Equal(new[] { MemberStableName }, amount.PreviousNames);
        }

        // ------------------------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------------------------

        sealed class Generated
        {
            public Compilation Compilation;
            public Assembly Assembly;
            public IReadOnlyList<Diagnostic> GeneratorDiagnostics;
        }

        // Cached: the two tests that only read the hostile compilation share it, so the generator runs
        // three times in total rather than once per test.
        static readonly Lazy<Generated> _hostile = new(() => Generate(HostileSource, "HostileLiterals"));
        static readonly Lazy<Generated> _oldFile = new(() => Generate(PortableHostileSource, "HostileLiteralsOld"));
        static readonly Lazy<Generated> _renamedFile = new(() => Generate(PortableRenamedSource, "HostileLiteralsRenamed"));

        static Generated Generate(string source, string assemblyName)
        {
            var ownLocation = typeof(GeneratedStringLiteralTests).Assembly.Location;
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                // The test assembly is deliberately excluded: it already declares a Ceras.Generated
                // namespace, and two of them in scope is the ambiguity the generated code avoids.
                .Where(a => a.Location != ownLocation && a.GetName().Name != "Ceras.SourceGenerator")
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14)) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(true)
                    .WithOptimizationLevel(OptimizationLevel.Debug));

            var driver = CSharpGeneratorDriver.Create(new CerasFormatterGenerator().AsSourceGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

            var generated = new Generated
            {
                Compilation = output,
                GeneratorDiagnostics = generatorDiagnostics.ToList(),
            };

            using var peStream = new MemoryStream();
            var emit = output.Emit(peStream);
            if (!emit.Success) AssertNoErrors(emit.Diagnostics, "the generated code did not compile");
            generated.Assembly = Assembly.Load(peStream.ToArray());
            return generated;
        }

        static void AssertNoErrors(Generated generated, string because)
            => AssertNoErrors(generated.Compilation.GetDiagnostics().Concat(generated.GeneratorDiagnostics), because);

        static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics, string because)
        {
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, because + ": " + string.Join("; ", errors.Select(d => d.ToString())));
        }

        /// <summary>
        /// The <c>Schema</c> the generated formatter for a type carries, read out of the loaded assembly's
        /// static field. The formatter is located through its <c>FormatterBase&lt;T&gt;</c> base, so the
        /// lookup cannot agree with the generator's own name scheme.
        /// </summary>
        static SchemaTypeInfo GetSchema(Assembly assembly, string typeFullName)
        {
            var wanted = assembly.GetType(typeFullName);
            Assert.NotNull(wanted);

            var formatter = assembly.GetTypes()
                .Where(t => t.Namespace == "Ceras.Generated")
                .FirstOrDefault(t => FormattedTypeOf(t) == wanted);
            Assert.True(formatter != null, "no generated formatter found for " + typeFullName);

            var field = formatter.GetField("Schema", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(field != null, formatter.FullName + " has no Schema field");
            return Assert.IsType<SchemaTypeInfo>(field.GetValue(null));
        }

        static Type FormattedTypeOf(Type formatterType)
        {
            for (var t = formatterType; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(FormatterBase<>))
                    return t.GetGenericArguments()[0];
            return null;
        }

        /// <summary>A robust serializer whose manifest is the given assembly's generated one.</summary>
        static CerasRobustSerializer CreateSerializer(Assembly generatedAssembly)
        {
            var use = generatedAssembly.GetType("Ceras.Generated.RobustFormatters")
                ?.GetMethod("UseRobustFormatters", BindingFlags.Static | BindingFlags.Public);
            Assert.True(use != null, "the emitted assembly has no RobustFormatters.UseRobustFormatters");

            return new CerasRobustSerializer(config =>
            {
                try
                {
                    use.Invoke(null, new object[] { config, "Hostile" });
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    throw e.InnerException;
                }
            });
        }

        static readonly MethodInfo SaveMethod =
            typeof(CerasRobustSerializer).GetMethods().Single(m => m.Name == nameof(CerasRobustSerializer.Save));

        static readonly MethodInfo LoadMethod =
            typeof(CerasRobustSerializer).GetMethods().Single(m => m.Name == nameof(CerasRobustSerializer.Load)
                                                                  && m.GetParameters().Length == 2
                                                                  && m.GetParameters()[0].ParameterType == typeof(byte[]));

        static byte[] Save(CerasRobustSerializer serializer, object root)
            => (byte[])SaveMethod.MakeGenericMethod(root.GetType()).Invoke(serializer, new object[] { root, 1u });

        static object Load(CerasRobustSerializer serializer, Type type, byte[] bytes)
            => LoadMethod.MakeGenericMethod(type).Invoke(serializer, new object[] { bytes, 1u });
    }
}
