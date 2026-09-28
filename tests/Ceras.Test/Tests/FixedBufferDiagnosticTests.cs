// The compile-time half of the fixed-buffer refusal, and the agreement between the two halves.
//
// A C# `fixed` buffer is the one member shape Ceras cannot describe, and it is refused on both paths:
// the reflection fallback at runtime (Formatters/FixedBufferGuard) and the source generator at compile
// time. This file is about the *second* one, and about the two saying the same thing.
//
// Why that is not automatic. The generator is a separate compilation: it references Microsoft.CodeAnalysis
// and nothing else, so it cannot read `Ceras.Formatters.FixedBufferGuard.Advice` (an `internal const` in
// the library) and the library cannot be changed to hand it over without dragging the analyzer's
// dependency graph along with it. The repository already has this exact situation and a stated rule for
// it — see `Ceras.SourceGenerator/Diagnostics.cs`, the comment above CERAS033/CERAS034: "the generator
// cannot reference a runtime const and a rule that reads differently in a compile error than in a bake
// error is two rules". So the sentence is written once per compilation and this file pins the two
// together from the one place that can see both. Without the pin, "in the same words" is a claim in a
// comment; with it, it is a failing test.
//
// The other half of the file is the two positions where a fixed buffer must *not* be refused, because
// the runtime permits them:
//
//   * `[Exclude]` — the member is not in the payload, so there is nothing to describe. The runtime asks
//     its guard of the members Ceras resolved for the type, not of every field the CLR has
//     (ReflectionFallbackFormatter, and the test in FixedBufferStructTests that pins it). A compiler that
//     refused what the library permits is exactly the "two rules" failure, one layer up.
//   * a type nothing serializes — the generator only walks the members of a type a
//     [CerasContext]/[CerasSerializable] root reaches, and the runtime only builds a formatter for a type
//     something asks it for. Neither ever arrives at a buffer that cannot matter.
using System;
using System.Collections.Generic;
using System.Linq;
using Ceras.Format;
using Ceras.Formatters;
using Ceras.Test.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Ceras.Test.Tests.FixedBufferDiagnostics
{
    public class FixedBufferDiagnosticTests
    {
        // ── The refusal, and what it is allowed to say ─────────────────────────────────────────────

        /// <summary>
        /// A fixed buffer gets its own diagnostic, and is not also reported as a type with no built-in
        /// representation. The old behaviour (CERAS029) refused correctly and explained itself wrongly: it
        /// said <c>byte*</c> was "not declared in this compilation and carries no [CerasSerializable]
        /// attribute, so the generator treats it as a framework type from a System.* namespace", which is
        /// false — <c>byte*</c> is the surface C# gives a buffer, and
        /// <c>TypeHelpers.GetCoreNamedType</c> peeling it to <c>System.Byte</c> is the whole accident.
        /// The second of its three suggestions was no better: "if it is your own type from another
        /// assembly, mark it [CerasSerializable]", offered to somebody already inside a
        /// [CerasSerializable] member.
        /// </summary>
        [Fact]
        public void A_Fixed_Buffer_Member_Is_Reported_By_CERAS035_And_Not_As_A_Framework_Type()
        {
            var run = RunGenerator(Source("public fixed byte Buffer[25];"));

            var d = Assert.Single(run.Diagnostics.Where(x => x.Id == "CERAS035"));
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.DoesNotContain("CERAS029", run.Diagnostics.Select(x => x.Id));
            Assert.DoesNotContain("CERAS030", run.Diagnostics.Select(x => x.Id));

            // One rule, one diagnostic: the member is dropped, so it is not also reported as a member
            // without a formatter, and the generated code is still valid C# — the build failure a user
            // sees is the one actionable diagnostic, not a second wave from broken output.
            Assert.DoesNotContain("CERAS021", run.Diagnostics.Select(x => x.Id));
            Assert.Empty(run.CompilationErrors);

            // Reported on the member, at the declaration the user wrote.
            var line = Source("public fixed byte Buffer[25];").Split('\n')
                [d.Location.GetLineSpan().StartLinePosition.Line];
            Assert.Contains("Buffer", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// The message states the real cause: the declared length is in an attribute on the field, so it is
        /// nowhere in the member's type and a member-by-member encoder cannot see it. Each of the three
        /// remedies it offers is a door that is actually open — a <c>byte[]</c> member, <c>[Exclude]</c>,
        /// and a custom <c>IFormatter&lt;T&gt;</c> for the declaring type — and nothing else is offered.
        /// </summary>
        [Fact]
        public void The_Compile_Time_Message_Names_The_Cause_And_Only_True_Remedies()
        {
            var message = Assert.Single(RunGenerator(Source("public fixed byte Buffer[25];"))
                .Diagnostics.Where(x => x.Id == "CERAS035")).GetMessage();

            Assert.Contains("is a C# fixed buffer", message, StringComparison.Ordinal);
            Assert.Contains("declared length in an attribute on the field", message, StringComparison.Ordinal);
            Assert.Contains("nowhere in the member's type", message, StringComparison.Ordinal);
            Assert.Contains(FixedBufferGuard.Advice.Replace("{0}", "Root"), message, StringComparison.Ordinal);

            // The three doors out, and only those.
            Assert.Contains("byte[]", message, StringComparison.Ordinal);
            Assert.Contains("[Exclude]", message, StringComparison.Ordinal);
            Assert.Contains("IFormatter<Root>", message, StringComparison.Ordinal);

            // Not offered, because it is not a way out of this shape:
            //   * [CerasSerializable] — the diagnostic fires inside a [CerasSerializable] type already;
            //   * the reflection-based CerasSerializer — the advice that walked a user into the 5-of-32
            //     byte truncation. That path now refuses with a CerasSchemaException (f0f5594), so the
            //     advice is no longer dangerous, but no path can encode the shape, so it is still wrong to
            //     volunteer it and it is gone;
            //   * the framework-type story, which is a different shape's story.
            Assert.DoesNotContain("[CerasSerializable]", message, StringComparison.Ordinal);
            Assert.DoesNotContain("CerasSerializer", message, StringComparison.Ordinal);
            Assert.DoesNotContain("System.*", message, StringComparison.Ordinal);
            Assert.DoesNotContain("not declared in this compilation", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The declaration is named the way the runtime names it — element type, element count, byte
        /// count — for a buffer that is not a byte buffer too. The byte count is what makes the loss
        /// concrete: it is why <c>fixed byte[25]</c> is 25 bytes against an encoding that writes 1.
        /// </summary>
        [Theory]
        [InlineData("public fixed byte Buffer[25];", "fixed byte[25], 25 bytes")]
        [InlineData("public fixed int Counts[3];", "fixed int[3], 12 bytes")]
        [InlineData("public fixed double Values[2];", "fixed double[2], 16 bytes")]
        [InlineData("public fixed bool Flags[4];", "fixed bool[4], 4 bytes")]
        public void The_Compile_Time_Message_Names_The_Declaration(string member, string expected)
        {
            var message = Assert.Single(RunGenerator(Source(member))
                .Diagnostics.Where(x => x.Id == "CERAS035")).GetMessage();
            Assert.Contains(expected, message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every buffer a serialized type declares is reported, not just the first one found. The fixes are
        /// per member, so naming one would only move the failure to the next save — the same reason the
        /// runtime lists them all (FixedBufferGuard.Reject).
        /// </summary>
        [Fact]
        public void Every_Buffer_On_The_Type_Is_Named()
        {
            var run = RunGenerator(Source("public fixed byte First[4]; public fixed byte Second[8];"));

            Assert.Equal(2, run.Diagnostics.Count(x => x.Id == "CERAS035"));
            Assert.Contains("First", run.Text("CERAS035"), StringComparison.Ordinal);
            Assert.Contains("Second", run.Text("CERAS035"), StringComparison.Ordinal);
        }

        // ── The two messages must be one message ─────────────────────────────────────────────────

        /// <summary>
        /// The rule the repository states for the CERAS033/CERAS034 pair, applied here: the compile-time
        /// refusal and the runtime refusal say the same thing in the same words for the same reason,
        /// because a rule that reads differently in a compile error than in a bake error is two rules.
        /// The two cannot share a constant (the generator references no Ceras assembly), so the sentence
        /// is written twice and pinned here — once from the runtime side, which is the only side whose
        /// text a test can read as a constant.
        /// </summary>
        [Fact]
        public void The_Two_Refusals_Say_The_Same_Thing()
        {
            var runtime = RuntimeRefusal();
            var compileTime = Assert.Single(RunGenerator(Source("public fixed byte Buffer[25];"))
                .Diagnostics.Where(x => x.Id == "CERAS035")).GetMessage();

            // The reason, verbatim, in both.
            Assert.Contains(FixedBufferGuard.Cause, runtime, StringComparison.Ordinal);
            Assert.Contains(FixedBufferGuard.Cause, compileTime, StringComparison.Ordinal);

            // The remedies, verbatim, in both. The generator names the type as it is written in source
            // ('Root') and the runtime as its canonical CLR identity, so the template is filled in with
            // the name that site uses and the sentence around it must be byte-identical.
            Assert.Contains(string.Format(FixedBufferGuard.Advice, TypeIdentity.GetCanonicalName(typeof(Probe))),
                runtime, StringComparison.Ordinal);
            Assert.Contains(string.Format(FixedBufferGuard.Advice, "Root"),
                compileTime, StringComparison.Ordinal);

            // The verdict is phrased the same way too, and it is the one thing both messages must agree
            // on before any of their details: Ceras cannot write this member. (The byte counts differ on
            // purpose — the compile-time one is what a save would have to write, the runtime one is what
            // the reflection fallback did write, 1 of the 25, and that difference is the reason the
            // shape is refused rather than encoded. Each side's own text is pinned where it belongs.)
            Assert.Contains("Ceras cannot write", runtime, StringComparison.Ordinal);
            Assert.Contains("Ceras cannot write", compileTime, StringComparison.Ordinal);
        }

        // ── The positions where the compiler must agree with the runtime and stay quiet ───────────

        /// <summary>
        /// <c>[Exclude]</c> is the documented way to keep a member out of a payload, and the runtime guard
        /// honours it because it is asked of the members Ceras resolved for the type rather than of every
        /// field the CLR has (see FixedBufferStructTests.A_Fixed_Buffer_Excluded_From_The_Payload_Is_Not_Refused).
        /// A compiler that refused an excluded buffer would be inventing a rule the library does not have.
        /// </summary>
        [Fact]
        public void An_Excluded_Fixed_Buffer_Is_Not_Reported()
        {
            var run = RunGenerator(Source("[Exclude] public fixed byte Data[16]; public int Value;"));

            Assert.DoesNotContain("CERAS035", run.Diagnostics.Select(x => x.Id));
            Assert.DoesNotContain("CERAS029", run.Diagnostics.Select(x => x.Id));
            Assert.Empty(run.CompilationErrors);

            // The rest of the type is untouched, so it is still generated and still serialized.
            var formatter = run.SourceContaining("Root");
            Assert.NotNull(formatter);
            Assert.Contains("\"Value\"", formatter, StringComparison.Ordinal);
        }

        /// <summary>
        /// A fixed buffer in a type nothing serializes is not a problem for either path, and neither
        /// reaches it: the generator only walks the members of a type a context root reaches, and the
        /// runtime only builds a formatter for a type something asks it for. The type below is declared,
        /// carries a buffer, and is reachable only through an excluded member — so it is never walked.
        /// </summary>
        [Fact]
        public void A_Fixed_Buffer_In_A_Type_Nothing_Serializes_Is_Not_Reported()
        {
            var run = RunGenerator(@"
using Ceras;
namespace T
{
    public unsafe struct NeverReached { public fixed byte Blob[8]; public int Value; }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public unsafe struct Root
    {
        [Exclude] public NeverReached Skipped;
        public int Value;
    }
}");

            Assert.DoesNotContain("CERAS035", run.Diagnostics.Select(x => x.Id));
            Assert.DoesNotContain("CERAS029", run.Diagnostics.Select(x => x.Id));
            Assert.Empty(run.CompilationErrors);
        }

        /// <summary>
        /// The same refusal for a buffer this compilation does not declare but reaches through a reference
        /// — the shape a user's own type from another assembly has, and the one case where the compiler's
        /// <c>FixedBufferAttribute</c> really is sitting on the field symbol. It has to be refused, and
        /// named identically: the element type and the count are read from the attribute here and from the
        /// symbol's own <c>FixedSize</c> in the source-declared case, and a user must not be able to tell
        /// which of the two produced the message.
        /// </summary>
        [Fact]
        public void A_Fixed_Buffer_Reached_Through_A_Referenced_Assembly_Is_Refused_The_Same_Way()
        {
            var library = EmitLibrary(@"
namespace Lib
{
    public unsafe struct HasBuffer { public fixed byte Buffer[25]; public int Value; }
}");

            var run = RunGenerator(@"
using Ceras;
[assembly: CerasKnownType(typeof(Lib.HasBuffer), ""Tests"")]
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public unsafe class Root { public int Value; }
}", library);

            string message = Assert.Single(run.Diagnostics.Where(x => x.Id == "CERAS035")).GetMessage();
            Assert.Contains("Member 'Buffer' in 'HasBuffer'", message, StringComparison.Ordinal);
            Assert.Contains("fixed byte[25], 25 bytes", message, StringComparison.Ordinal);
            Assert.Contains(FixedBufferGuard.Advice.Replace("{0}", "HasBuffer"), message, StringComparison.Ordinal);
            Assert.DoesNotContain("CERAS029", run.Diagnostics.Select(x => x.Id));
        }

        /// <summary>
        /// A hand-written pointer is not a fixed buffer. C# gives the two the same type (<c>byte*</c>), and
        /// the generator peels pointers, so this is the confusion waiting to happen — and widening the
        /// check to pointers would quietly change what the framework-type rule does with them.
        /// </summary>
        [Fact]
        public void A_Hand_Written_Pointer_Is_Not_A_Fixed_Buffer()
        {
            var run = RunGenerator(Source("public unsafe byte* NotABuffer; public int Value;"));

            Assert.DoesNotContain("CERAS035", run.Diagnostics.Select(x => x.Id));
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────────────

        /// <summary>One serialized root with one member, which is the position every shape is asked in.</summary>
        static string Source(string member) => @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public unsafe struct Root
    {
        " + member + @"
    }
}";

        /// <summary>
        /// The runtime refusal, from a real type carrying a real buffer, so this is the message a user
        /// meets rather than a string built for the test. <see cref="Probe"/> is a real
        /// <c>fixed byte[25]</c> and nothing in this compilation serializes it, so the reflection fallback
        /// is the only path that can be asked about it.
        /// </summary>
        static string RuntimeRefusal()
        {
            var raw = new CerasFastSerializer(new CerasRawConfig { AllowReflectionFallback = true });
            try
            {
                raw.GetSpecificFormatter(typeof(Probe));
            }
            catch (Exception e)
            {
                for (var current = e; current != null; current = current.InnerException)
                    if (current is Ceras.Exceptions.CerasSchemaException refusal)
                        return refusal.Message;

                throw;
            }

            throw new InvalidOperationException("the fixed-buffer struct was not refused at runtime");
        }

        sealed class Run
        {
            public List<Diagnostic> Diagnostics = new();
            public List<string> CompilationErrors = new();
            public Dictionary<string, string> Sources = new();

            public string Text(string id)
                => string.Join(" | ", Diagnostics.Where(d => d.Id == id).Select(d => d.GetMessage()));

            public string SourceContaining(string fragment)
            {
                foreach (var s in Sources)
                    if (s.Key.Contains(fragment, StringComparison.Ordinal))
                        return s.Value;
                return null;
            }
        }

        /// <summary>
        /// Runs the real generator over a one-file compilation, and then over the compilation the
        /// generated sources were added to. The second pass is the point: a diagnostic that drops a member
        /// has to leave output that still compiles, or the user reads a wave of CS errors instead of the
        /// one message that can be acted on.
        /// </summary>
        static Run RunGenerator(string source, MetadataReference extraReference = null)
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var cerasLocation = typeof(Ceras.CerasSerializer).Assembly.Location;
            if (!refs.OfType<PortableExecutableReference>().Any(r => r.FilePath == cerasLocation))
                refs.Add(MetadataReference.CreateFromFile(cerasLocation));

            if (extraReference != null)
                refs.Add(extraReference);

            var compilation = CSharpCompilation.Create(
                "CerasFixedBufferDiagnosticTest",
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14)) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var driver = CSharpGeneratorDriver.Create(new Ceras.SourceGenerator.CerasFormatterGenerator().AsSourceGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

            var result = driver.GetRunResult().Results.First();
            return new Run
            {
                Diagnostics = result.Diagnostics.ToList(),
                CompilationErrors = output.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString())
                    .ToList(),
                Sources = result.GeneratedSources.ToDictionary(s => s.HintName, s => s.SourceText.ToString()),
            };
        }

        /// <summary>
        /// Emits a one-file library to an in-memory assembly and hands back a reference to it, so a case
        /// the test has to reach through <em>metadata</em> can be built here instead of being faked with a
        /// source declaration. This is the only way to reach the position below, and the position matters:
        /// a fixed buffer declared in the sources being compiled and one read back from a referenced
        /// assembly are two different symbol shapes, and only the second one carries the compiler's own
        /// <c>FixedBufferAttribute</c> on the field (see MemberExplorer.IsFixedBufferField).
        /// </summary>
        static MetadataReference EmitLibrary(string source, string assemblyName = "CerasFixedBufferTestLibrary")
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var library = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14)) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            Assert.Empty(library.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

            var image = new System.IO.MemoryStream();
            Assert.True(library.Emit(image).Success);
            return MetadataReference.CreateFromImage(image.ToArray());
        }
    }

    /// <summary>
    /// A real fixed-buffer type with a real declared length, so the runtime half of the agreement test
    /// runs the library's own guard on a real declaration instead of a message assembled for the test.
    /// Deliberately not marked <c>[CerasSerializable]</c>: nothing in this compilation serializes it (the
    /// generator never sees it, which is the point of the not-serialized test above), so the only path
    /// that can be asked about it is the runtime one.
    /// </summary>
    public unsafe struct Probe
    {
        public fixed byte Buffer[25];
        public int Value;
    }
}
