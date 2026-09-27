using System;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Format;
using Ceras.Generated;
using Ceras.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

// Priority 0 — serialize callbacks ([OnBeforeSerialize] / [OnAfterSerialize]) must behave the way the
// public attribute promises and the way the reflection fallback already did: once per written instance,
// bracketing that instance's member writes, with the supported signatures, and never silently dropped.
// The fixtures share one serialization context, except the nested-operation pair (a *different* manifest
// inside the callback) and the byte-parity type (registered twice, changing only the writer).
namespace Ceras.Test.SerializationCallbacks
{
    static class Sc
    {
        public static readonly List<string> Log = new List<string>();
        public static int Calls, Base, Derived, PrivateBase;
        public static CerasSerializer Serializer;
        public static object UserContext;
        public static void Reset() { Log.Clear(); Calls = Base = Derived = PrivateBase = 0; Serializer = null; UserContext = null; }

        public static CerasRobustSerializer Inner;   // for the nested-operation test
        public static byte[] InnerBytes;
    }

    // ── Order: before < member writes < after, nested objects inside their parent ───────────────────
    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScLeaf
    {
        public int Value;
        [OnBeforeSerialize] private void Before() => Sc.Log.Add("leaf-before:" + Value);
        [OnAfterSerialize] private void After() => Sc.Log.Add("leaf-after:" + Value);
    }

    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScRoot
    {
        public ScLeaf Leaf;
        public ScLeaf Second;   // may alias Leaf; reference tracking must write it only once
        public string Name;
        [OnBeforeSerialize] private void Before() => Sc.Log.Add("root-before");
        [OnAfterSerialize] private void After() => Sc.Log.Add("root-after");
    }

    // ── Mutation visibility: only a [OnBeforeSerialize] mutation can reach the stream ───────────────
    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScMutate
    {
        public int Value;
        public string Label;

        [OnBeforeSerialize]
        private void Before()
        {
            Sc.Log.Add("before:" + Value + ":" + Label);
            Value += 1000;
            Label += "-mutated";
        }

        [OnAfterSerialize]
        private void After()
        {
            Sc.Log.Add("after:" + Value + ":" + Label);
            Value = -1;          // assigned after the members were written, so it must not be saved
            Label = "discarded";
        }
    }

    // ── CerasSerializer argument + private callbacks + all four directions on one type ──────────────
    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScArg
    {
        public int Value;

        [OnBeforeSerialize] private void Before(CerasSerializer c) { Sc.Calls++; Sc.Serializer = c; Sc.UserContext = c.UserContext; }
        [OnAfterSerialize] private void After(CerasSerializer c) { Sc.Calls++; Sc.Serializer = c; }
        [OnBeforeDeserialize] private void Bd() => Sc.Log.Add("bd");
        [OnAfterDeserialize] private void Ad() => Sc.Log.Add("ad");
    }

    // ── Generic declaring type: the shared open formatter + the generic accessor helper ──────────────
    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScGeneric<T>
    {
        public T Value;
        [OnBeforeSerialize] private void Before() => Sc.Log.Add("generic-before");
        [OnAfterSerialize] private void After(CerasSerializer c) => Sc.Log.Add("generic-after:" + (c != null));
    }

    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScGenericBox
    {
        public ScGeneric<int> Box;
    }

    // ── Value type: no _ceras field of its own, so a serializer-argument callback forces one ─────────
    [CerasContext("SerCb")]
    [CerasSerializable]
    public struct ScStruct
    {
        public int Value;
        [OnBeforeSerialize] private void Before(CerasSerializer c) { Sc.Calls++; Sc.Serializer = c; }
        [OnAfterSerialize] private void After() => Sc.Log.Add("struct-after");
    }

    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScStructBox
    {
        public ScStruct Item;
    }

    // ── Inheritance: a private base callback is found, an override shadows it ───────────────────────
    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScBase
    {
        public int Id;
        [OnAfterSerialize] protected virtual void After() => Sc.Base++;
    }

    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScDerived : ScBase
    {
    }

    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScOverride : ScBase
    {
        [OnAfterSerialize] protected override void After() => Sc.Derived++;
    }

    /// <summary>A private base callback: reflection's <c>GetMethods</c> on the derived type would never see
    /// it, so it is only reachable by walking the hierarchy to an accessor on the base.</summary>
    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScPrivBase
    {
        public int Id;
        [OnAfterSerialize] private void After() => Sc.PrivateBase++;
    }

    [CerasContext("SerCb")]
    [CerasSerializable]
    public class ScPrivDerived : ScPrivBase
    {
    }

    // ── Nested operations started from a serialize callback ─────────────────────────────────────────
    [CerasContext("SerCbNestIn")]
    [CerasSerializable]
    public class ScNestIn
    {
        public string Name;
        public int Number;
    }

    [CerasContext("SerCbNestOut")]
    [CerasSerializable]
    public class ScNestOut
    {
        // Both members are written *after* the callback, so a leaked inner string table would surface as
        // the wrong (or missing) string in the outer payload.
        public string Name;
        public int Number;

        [OnBeforeSerialize]
        private void Before()
        {
            if (Sc.Inner == null) return;

            var saved = Sc.Inner.Save(new ScNestIn { Name = "inner-string-that-should-never-leak", Number = 7 }, 1);
            var loaded = Sc.Inner.Load<ScNestIn>(Sc.InnerBytes, 1);
            Sc.Log.Add("outer-before:" + Name);
            Sc.Log.Add("nested-save:" + saved.Length);
            Sc.Log.Add("nested-load:" + loaded.Name);
        }
    }

    // ── Generated vs reflection fallback (own context, see the byte-parity test) ─────────────────────
    [CerasContext("SerCbBytes")]
    [CerasSerializable]
    public class ScBytes
    {
        public int Number;
        public string Text;
        public long Big;
        public bool Flag;
        [OnBeforeSerialize] private void Before() => Sc.Log.Add("bytes-before:" + Number);
        [OnAfterSerialize] private void After() => Sc.Log.Add("bytes-after:" + Number);
    }

    // Deliberately *not* [CerasSerializable]: the generator must not see this type, so what the raw path
    // does with an unsupported serialize-callback signature is observable on its own.
    public class RawBadCallback
    {
        public int Value;
        [OnBeforeSerialize] private void Before(MigrationContext context) { }
    }

    public class SerializationCallbackTests : TestBase
    {
        static CerasRobustSerializer S(string context) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));
        static CerasRobustSerializer Ser() => S("SerCb");
        static ScBytes Sample() => new ScBytes { Number = 1, Text = "text", Big = 1234567890123, Flag = true };

        [Fact]
        public void Generated_CallbacksBracketTheMemberWrites_InOrder_AndOnlyOncePerWrittenInstance()
        {
            var shared = new ScLeaf { Value = 5 };
            Sc.Reset();
            var bytes = Ser().Save(new ScRoot { Leaf = shared, Second = shared, Name = "root-string" }, 1);

            // The child's whole Serialize — including its own after-callback — happens while the parent is
            // writing its members, so it nests inside the parent's two callbacks. It appears once even
            // though it is referenced twice: a callback runs per *written* instance, and reference tracking
            // writes a shared object only once. Callbacks otherwise have no wire effect.
            Assert.Equal(new[] { "root-before", "leaf-before:5", "leaf-after:5", "root-after" }, Sc.Log);

            var loaded = Ser().Load<ScRoot>(bytes, 1);
            Assert.Equal("root-string", loaded.Name);
            Assert.Same(loaded.Leaf, loaded.Second);
        }

        [Fact]
        public void Generated_BeforeMutationReachesThePayload_AfterMutationDoesNot()
        {
            Sc.Reset();
            var bytes = Ser().Save(new ScMutate { Value = 1, Label = "a" }, 1);

            // Before ran with the pre-save values and After saw what Before wrote, which is only possible
            // if Before runs before the members are read and After after they are written.
            Assert.Equal(new[] { "before:1:a", "after:1001:a-mutated" }, Sc.Log);

            var loaded = Ser().Load<ScMutate>(bytes, 1);
            Assert.Equal(1001, loaded.Value);
            Assert.Equal("a-mutated", loaded.Label);
        }

        [Fact]
        public void Generated_PrivateGenericStructAndSerializerCallbacks_Run_AndDoNotCrossDirections()
        {
            var serializer = Ser();
            serializer.UserContext = new object();
            var userContext = serializer.UserContext;

            // Private callbacks taking the serializer: both directions fire, the other two do not.
            Sc.Reset();
            var argBytes = serializer.Save(new ScArg { Value = 3 }, 1);
            Assert.Equal(2, Sc.Calls);
            Assert.Same(serializer, Sc.Serializer);
            Assert.Same(userContext, Sc.UserContext);
            Assert.Empty(Sc.Log);

            // No reset here: the serialize counters must be *unchanged* by the load.
            Assert.Equal(3, Ser().Load<ScArg>(argBytes, 1).Value);
            Assert.Equal(2, Sc.Calls);
            Assert.Equal(new[] { "bd", "ad" }, Sc.Log);

            // Generic declaring type: reached through the shared open formatter + generic accessor helper.
            Sc.Reset();
            var genericBytes = serializer.Save(new ScGenericBox { Box = new ScGeneric<int> { Value = 11 } }, 1);
            Assert.Equal(new[] { "generic-before", "generic-after:True" }, Sc.Log);
            Assert.Equal(11, Ser().Load<ScGenericBox>(genericBytes, 1).Box.Value);

            // Value type: runs inline on the parameter copy, and a serializer argument forces the _ceras
            // field into a formatter that would otherwise not have one.
            Sc.Reset();
            var structBytes = serializer.Save(new ScStructBox { Item = new ScStruct { Value = 4 } }, 1);
            Assert.Equal(1, Sc.Calls);
            Assert.Same(serializer, Sc.Serializer);
            Assert.Equal(new[] { "struct-after" }, Sc.Log);
            Assert.Equal(4, Ser().Load<ScStructBox>(structBytes, 1).Item.Value);
        }

        [Fact]
        public void Generated_InheritedCallbackIsFound_AnOverrideShadowsIt_AndAPrivateBaseOneStillWorks()
        {
            Sc.Reset();
            Ser().Save(new ScDerived { Id = 1 }, 1);
            Assert.Equal(1, Sc.Base);
            Assert.Equal(0, Sc.Derived);

            Sc.Reset();
            Ser().Save(new ScOverride { Id = 2 }, 1);
            Assert.Equal(0, Sc.Base);
            Assert.Equal(1, Sc.Derived);

            Sc.Reset();
            var bytes = Ser().Save(new ScPrivDerived { Id = 3 }, 1);
            Assert.Equal(1, Sc.PrivateBase);
            Assert.Equal(3, Ser().Load<ScPrivDerived>(bytes, 1).Id);
        }

        [Fact]
        public void Generated_ASaveAndALoadStartedInsideASerializeCallback_AreScopedToTheInnerContext()
        {
            Sc.Reset();
            Sc.Inner = S("SerCbNestIn");
            Sc.InnerBytes = Sc.Inner.Save(new ScNestIn { Name = "inner", Number = 3 }, 1);

            var serializer = S("SerCbNestOut");
            for (int i = 0; i < 3; i++)
            {
                Sc.Reset();
                var bytes = serializer.Save(new ScNestOut { Name = "outer-string", Number = 42 }, 1);

                // Both nested operations ran, through a different manifest, and the scope restores itself
                // on every save rather than only the first.
                Assert.Equal("outer-before:outer-string", Sc.Log[0]);
                Assert.StartsWith("nested-save:", Sc.Log[1], StringComparison.Ordinal);
                Assert.Equal("nested-load:inner", Sc.Log[2]);

                // The outer payload was written against the outer write context throughout: its own string
                // is intact and its own value did not come from the inner tables.
                var loaded = S("SerCbNestOut").Load<ScNestOut>(bytes, 1);
                Assert.Equal("outer-string", loaded.Name);
                Assert.Equal(42, loaded.Number);
            }
        }

        [Fact]
        public void Generated_And_ReflectionFallback_ProduceIdenticalBytes_AndTheSameCallbackOrder()
        {
            Sc.Reset();
            var generated = S("SerCbBytes");
            var generatedBytes = generated.Save(Sample(), 1);
            var generatedLog = new List<string>(Sc.Log);

            // The reflection fallback is the ground truth for byte identity. Registering the *same* context
            // keeps the manifest (type ids, schema section, fingerprint) identical and swaps only the writer
            // for ScBytes, so any difference below is a writer difference, not a manifest one.
            Sc.Reset();
            var reflection = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters("SerCbBytes");
                c.AllowReflectionFallback = true;
                c.ConfigType<ScBytes>().CustomFormatter = null;
            });
            var reflectionBytes = reflection.Save(Sample(), 1);

            Assert.Equal(new[] { "bytes-before:1", "bytes-after:1" }, generatedLog);
            Assert.Equal(generatedBytes, reflectionBytes);
            Assert.Equal(generatedLog, Sc.Log);
            Assert.Equal(1, generated.Load<ScBytes>(generatedBytes, 1).Number);
            Assert.Equal(1, reflection.Load<ScBytes>(reflectionBytes, 1).Number);
        }

        [Fact]
        public void Raw_HonorsTheSupportedSignatures_AndFailsLoudlyOnAnUnsupportedOne()
        {
            // The raw format has no generated path, so this is the reference behaviour to match.
            var raw = new CerasFastSerializer(c => c.AllowReflectionFallback = true);
            raw.UserContext = new object();

            Sc.Reset();
            var clone = raw.Deserialize<ScArg>(raw.Serialize(new ScArg { Value = 9 }));
            Assert.Equal(9, clone.Value);
            Assert.Equal(2, Sc.Calls);
            Assert.Same(raw, Sc.Serializer);
            Assert.Same(raw.UserContext, Sc.UserContext);

            // A MigrationContext parameter cannot be satisfied while saving. The generated path rejects that
            // at compile time (CERAS028, below); the raw path has no compile-time check, so it must at
            // least not silently run without the callback.
            var error = Assert.ThrowsAny<Exception>(() => raw.Serialize(new RawBadCallback { Value = 1 }));
            Assert.Contains("MigrationContext", error.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// CERAS028 — a callback whose signature Ceras cannot invoke must not be dropped in silence: the
    /// generated writer/reader would emit no call for it, so the author's code would never run while the
    /// attribute still claims it will. This lives in the generator-driver harness (like the other
    /// diagnostic tests) because a real CERAS028 would fail the test assembly's own build.
    /// </summary>
    public class CallbackSignatureDiagnostics
    {
        // A single-type compilation, so an index comparison inside the one formatter cannot straddle two files.
        const string Preamble = """
            using Ceras;
            using Ceras.Format;
            namespace T
            {
                [CerasContext("Tests")]
                [CerasSerializable]
                public class Holder
                {
                    public int Value;
            """;

        static string Source(string members) => Preamble + members + "\n    }\n}";

        [Theory]
        // member declaration, fragment the reason must contain
        [InlineData("void Before(MigrationContext c) { }", "no migration while saving")]
        [InlineData("int After() => 1;", "instead of void")]
        [InlineData("void Before(int a, int b) { }", "2 parameters")]
        [InlineData("void After(int notASerializer) { }", "has type 'int'")]
        [InlineData("static void Before() { }", "it is static")]
        [InlineData("void After<TItem>() { }", "it is a generic method")]
        [InlineData("async void Before() { await System.Threading.Tasks.Task.Yield(); }", "it is async")]
        [InlineData("void Before(ref CerasSerializer c) { }", "passed by 'ref'")]
        [InlineData("void Before(out CerasSerializer c) { c = null!; }", "passed by 'out'")]
        [InlineData("void Before(in CerasSerializer c) { }", "passed by 'in'")]
        public void UnsupportedSignature_EmitsExactlyOneCERAS028(string member, string reasonFragment)
        {
            // The declaration's own name picks the attribute, so every case really does exercise the
            // direction it claims to.
            bool isAfter = member.Contains("After");
            var attribute = isAfter ? "OnAfterSerialize" : "OnBeforeSerialize";
            var source = Source($"""
                [{attribute}]
                private {member}
                """);

            var d = Assert.Single(DiagnosticsFor(source).Where(x => x.Id == "CERAS028"));
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);

            // The message has to be actionable: the direction, the reason, and the shape that would work.
            Assert.Contains(attribute, d.GetMessage(), StringComparison.Ordinal);
            Assert.Contains(reasonFragment, d.GetMessage(), StringComparison.Ordinal);
            Assert.Contains("non-async instance method that returns void", d.GetMessage(), StringComparison.Ordinal);

            // Reported on the method itself: the declaration's own line, pointing at its identifier.
            var span = d.Location.GetLineSpan();
            var lines = source.Split('\n');
            int declLine = Array.FindIndex(lines, l => l.Contains(member, StringComparison.Ordinal));
            Assert.Equal(declLine, span.StartLinePosition.Line);
            Assert.Equal(isAfter ? "After" : "Before", lines[declLine].Substring(
                span.StartLinePosition.Character,
                span.EndLinePosition.Character - span.StartLinePosition.Character));
        }

        [Fact]
        public void SupportedSignatures_AreEmittedAroundTheMemberWrites_WithOneAccessorPerDirection()
        {
            // All four directions on one type: the "before" accessor of one direction must not collide with
            // the other's, so every direction needs its own name.
            var source = Source("""
                [OnBeforeSerialize] private void BS() { }
                [OnAfterSerialize] private void AS(CerasSerializer c) { }
                [OnBeforeDeserialize] private void BD(MigrationContext c) { }
                [OnAfterDeserialize] private void AD() { }
                """);

            Assert.DoesNotContain("CERAS028", DiagnosticsFor(source).Select(x => x.Id));

            var text = Assert.Single(FormatterTexts(source));
            Assert.All(new[] { "__ceras_cb_serialize_before", "__ceras_cb_serialize_after",
                               "__ceras_cb_deserialize_before", "__ceras_cb_deserialize_after" },
                n => Assert.Contains(n, text, StringComparison.Ordinal));

            // The positive half of the contract, read out of the emitted writer: [OnBeforeSerialize]
            // precedes the first member write and [OnAfterSerialize] follows the last one.
            int firstWrite = text.IndexOf("SerializerBinary.WriteInt32Fixed", StringComparison.Ordinal);
            int lastWrite = text.LastIndexOf("SerializerBinary.WriteInt32Fixed", StringComparison.Ordinal);
            int beforeCall = text.IndexOf("__ceras_cb_serialize_before", StringComparison.Ordinal);
            int afterCall = text.IndexOf("__ceras_cb_serialize_after", StringComparison.Ordinal);
            Assert.True(beforeCall > 0 && firstWrite > 0 && lastWrite > 0 && afterCall > 0,
                "expected both callback calls and member writes");
            Assert.True(beforeCall < firstWrite, "the before-callback must precede the first member write");
            Assert.True(lastWrite < afterCall, "the after-callback must follow the last member write");
        }

        [Fact]
        public void MigrationContextStaysSupportedOnDeserialize_TheSerializeRuleIsDirectionSpecific()
        {
            // The MigrationContext case above is direction-specific: a MigrationContext parameter is
            // exactly what a migration callback is *for*, so the deserialization side must keep it.
            var source = Source("""
                [OnBeforeDeserialize] private void Before(MigrationContext context) { }
                """);

            Assert.DoesNotContain("CERAS028", DiagnosticsFor(source).Select(x => x.Id));
            Assert.Contains("__ceras_cb_deserialize_before", Assert.Single(FormatterTexts(source)), StringComparison.Ordinal);
        }

        [Fact]
        public void GenericType_EmitsTheCallbackOnce_AndReportsABadSignatureOncePerDefinition()
        {
            // One open formatter serves every closure, so the call is emitted once and a bad signature is
            // diagnosed once even though two closures exist (the way member diagnostics already are).
            static string Generic(string member) => $$"""
                using Ceras;
                namespace T
                {
                    [CerasContext("Tests")]
                    [CerasSerializable]
                    public class Box<TItem> { public TItem Value; [OnBeforeSerialize] private {{member}} }

                    [CerasContext("Tests")]
                    [CerasSerializable]
                    public class Root { public Box<int> A; public Box<string> B; }
                }
                """;

            var good = Generic("void Before() { }");
            Assert.DoesNotContain("CERAS028", DiagnosticsFor(good).Select(x => x.Id));
            Assert.Equal(1, FormatterTexts(good).Count(t => t.Contains("__ceras_cb_serialize_before", StringComparison.Ordinal)));

            Assert.Single(DiagnosticsFor(Generic("void Before(int notASerializer) { }")).Where(x => x.Id == "CERAS028"));
        }

        static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<(string Hint, string Text)> Sources)
            Run(string source)
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .Append(MetadataReference.CreateFromFile(typeof(CerasSerializer).Assembly.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "CerasCallbackSignatureDiagTest",
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14)) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var ran = CSharpGeneratorDriver.Create(new CerasFormatterGenerator().AsSourceGenerator())
                .RunGenerators(compilation)
                .GetRunResult();

            return (ran.Diagnostics, ran.Results
                .SelectMany(r => r.GeneratedSources.Select(s => (s.HintName, s.SourceText.ToString())))
                .ToList());
        }

        static IReadOnlyList<Diagnostic> DiagnosticsFor(string source) => Run(source).Diagnostics;

        /// <summary>Every generated formatter source that declares a Serialize override.</summary>
        static IReadOnlyList<string> FormatterTexts(string source) => Run(source).Sources
            .Where(s => s.Hint.EndsWith(".Ceras.g.cs", StringComparison.Ordinal))
            .Select(s => s.Text)
            .Where(t => t.Contains("public override void Serialize", StringComparison.Ordinal))
            .ToList();    }
}
