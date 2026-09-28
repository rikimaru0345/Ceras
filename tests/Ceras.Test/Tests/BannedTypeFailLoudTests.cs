// Banned members and banned runtime values must fail loudly instead of disappearing.
//
// Both used to be silent data loss: a member whose type is banned was dropped from the schema (the
// save succeeded and the member came back null), and a banned runtime value in a reference or Type
// slot was written as a null marker. This file pins the replacement - the value is rejected by name.
//
// The two halves of that policy are kept apart on purpose, and each has a control that must still
// pass, so the file cannot be satisfied by refusing everything:
//   * a *shape* ban (delegate, enumerator, MemberInfo, ...) is an error;
//   * a member of a type the user marked [Exclude] is a *declared* omission - still allowed, but
//     pinned below as an omission the user asked for and can inspect, never as a value that failed
//     to serialize. The companion case for the same type is a *value* in a reference slot, which is
//     not a declaration and must be rejected.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.SourceGenerator;
using Ceras.Generated;

namespace Ceras.Test.Banned
{
    public class BannedTypeFailLoudTests : TestBase
    {
        // ── member discovery: the generator (CERAS031) ─────────────────────────────────────────

        static string Source(string members, string extraTypes = "") => $@"
using System;
using System.Collections.Generic;
using Ceras;
namespace T {{
    {extraTypes}

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder
    {{
        public int Value;
        {members}
    }}
}}";

        const string ExcludedTypeDecl = @"
    [Exclude]
    public class NotSerializable { public int Secret; }";

        public static TheoryData<string, string, string> BannedMembers => new()
        {
            { "delegate", "public System.Action Callback;", "Action" },
            { "closed generic delegate", "public System.Func<int, int> Callback;", "Func" },
            { "enumerator", "public System.Collections.IEnumerator Enumerator;", "Enumerator" },
            { "generic enumerator", "public System.Collections.Generic.IEnumerator<int> Enumerator;", "Enumerator" },
            { "reflection metadata", "public System.Reflection.PropertyInfo Property;", "PropertyInfo" },
            { "auto-property", "public System.Action Callback { get; set; }", "Action" },
        };

        [Theory]
        [MemberData(nameof(BannedMembers))]
        public void Generator_BannedMember_EmitsCERAS031(string _, string member, string expectedInMessage)
        {
            var run = RunGenerator(Source(member));
            var ids = run.Diagnostics.Select(d => d.Id).Distinct().ToList();

            Assert.Contains("CERAS031", ids);
            Assert.Contains(expectedInMessage, run.Diagnostics.First(d => d.Id == "CERAS031").GetMessage());

            // One root cause, one diagnostic: the member is not emitted, so it is not also reported
            // as a member whose type has no generated formatter or as a construction problem.
            Assert.DoesNotContain("CERAS021", ids);
            Assert.DoesNotContain("CERAS003", ids);
            Assert.DoesNotContain("CERAS001", ids);

            // And the generated code is still valid C#, so the build failure a user sees is the one
            // actionable diagnostic rather than a second wave of errors from broken output.
            Assert.Empty(run.CompilationErrors);
        }

        public static TheoryData<string, string> AllowedMembers => new()
        {
            { "[Exclude]d member", "[Exclude] public System.Action Callback;" },
            { "member of an [Exclude]d type", "public NotSerializable Thing;" },
            { "ordinary members", "public object Anything; public System.Type RuntimeType; public List<int> Items; public int[] Numbers;" },
            { "generic with a banned type argument", "public List<System.Func<int>> Handlers;" },
        };

        [Theory]
        [MemberData(nameof(AllowedMembers))]
        public void Generator_AllowedMember_DoesNotEmitCERAS031(string _, string member)
        {
            var run = RunGenerator(Source(member, ExcludedTypeDecl));

            Assert.DoesNotContain("CERAS031", run.Diagnostics.Select(d => d.Id));
            Assert.Empty(run.CompilationErrors);
        }

        // ── member discovery: the runtime (reflection fallback) ─────────────────────────────────

        public class DelegateHolder { public int Value; public Action Callback; }
        public class ExcludedDelegateHolder { public int Value; [Exclude] public Action Callback; }
        public class ExcludedTypeMemberHolder { public int Value; public NotSerializableThing Thing; }
        public class StaticDelegateHolder { public static int Value; public static Action Callback; }

        [Exclude]
        public class NotSerializableThing { public int Secret; }

        public class CustomFormattedHolder { public int Value; public Action Callback; }

        class HolderFormatter : FormatterBase<CustomFormattedHolder>
        {
            public override void Serialize(IBufferWriter<byte> writer, CustomFormattedHolder value)
                => SerializerBinary.WriteInt32Fixed(writer, value?.Value ?? 0);

            public override void Deserialize(ref SequenceReader<byte> reader, ref CustomFormattedHolder value)
            {
                int v = SerializerBinary.ReadInt32Fixed(ref reader);
                if (value == null) value = new CustomFormattedHolder();
                value.Value = v;
            }
        }

        [Fact]
        public void ReflectionFallback_BannedMember_ThrowsActionableError()
        {
            var banned = AssertBanned(() => Raw().Serialize(new DelegateHolder { Value = 1, Callback = () => { } }));

            // What, where, why, and both ways out. (The declaring type is spelled with a '+' when
            // nested; matching the bare name keeps the assertion about identification, not format.)
            Assert.Contains(nameof(DelegateHolder.Callback), banned.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(DelegateHolder), banned.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Action), banned.Message, StringComparison.Ordinal);
            Assert.Contains("delegate", banned.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[Exclude]", banned.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void StaticFormatter_BannedMember_Throws()
        {
            // The static schema is a separate walk of the same members, so it gets the same
            // diagnosis rather than quietly dropping the field.
            StaticDelegateHolder.Value = 1;
            StaticDelegateHolder.Callback = () => { };

            var banned = AssertBanned(() => Raw().SerializeStatic(typeof(StaticDelegateHolder)));

            Assert.Contains(nameof(StaticDelegateHolder.Callback), banned.Message, StringComparison.Ordinal);
        }

        // The controls for the policy above: an [Exclude]d member and a member of an [Exclude]d type
        // are declared omissions, and a declared omission must look nothing like a lost value.
        [Fact]
        public void DeclaredOmission_ExcludedMember_LeavesNoTraceInThePayload()
            => AssertDeclaredOmission(
                new ExcludedDelegateHolder { Value = 7, Callback = () => { } },
                new ExcludedDelegateHolder { Value = 7 },
                nameof(ExcludedDelegateHolder.Callback), 7);

        [Fact]
        public void DeclaredOmission_MemberOfAnExcludedType_LeavesNoTraceInThePayload()
            => AssertDeclaredOmission(
                new ExcludedTypeMemberHolder { Value = 3, Thing = new NotSerializableThing { Secret = 99 } },
                new ExcludedTypeMemberHolder { Value = 3 },
                nameof(ExcludedTypeMemberHolder.Thing), 3);

        static void AssertDeclaredOmission<T>(T withValue, T withoutValue, string memberName, int expectedRemaining)
        {
            var config = new CerasRawConfig { AllowReflectionFallback = true };
            var ceras = new CerasFastSerializer(config);

            var bytes = ceras.Serialize(withValue);

            // 1. The omitted value leaves no trace: the bytes are exactly those of the same object
            //    with the member null, so nothing was written and then misread. That is the opposite
            //    of the bug, where a value was dropped from the payload and came back null.
            Assert.Equal(ceras.Serialize(withoutValue), bytes);

            // 2. The omission is attributed and inspectable, with the reason the user asked for.
            var report = config.Serializer.GenerateSerializationDebugReport(typeof(T));
            Assert.Contains(memberName, report, StringComparison.Ordinal);
            Assert.Contains("[Exclude]", report, StringComparison.Ordinal);

            // 3. Everything else survives, so the object did not quietly lose a second field too.
            Assert.Equal(expectedRemaining, typeof(T).GetField("Value").GetValue(ceras.Deserialize<T>(bytes)));
        }

        [Fact]
        public void ReflectionFallback_CustomFormatterForTheWholeType_IsASupportedReplacement()
        {
            // A formatter for the *containing* type owns every byte of that type, including the
            // member Ceras cannot write. That is a representation the user provided, so it is
            // accepted - the members are never walked at all.
            var ceras = new CerasFastSerializer(config =>
            {
                config.AllowReflectionFallback = true;
                config.ConfigType<CustomFormattedHolder>().CustomFormatter = new HolderFormatter();
            });

            var clone = ceras.Deserialize<CustomFormattedHolder>(ceras.Serialize(new CustomFormattedHolder { Value = 42, Callback = () => { } }));

            Assert.Equal(42, clone.Value);
            Assert.Null(clone.Callback);
        }

        // The shape this replaces, kept as an explicit pin: a custom formatter for the *member's*
        // type does not rescue the member, and the error must not point at one as the way out.
        class DelegateNameFormatter : FormatterBase<Action>
        {
            public override void Serialize(IBufferWriter<byte> writer, Action value) => SerializerBinary.WriteByte(writer, (byte)(value == null ? 0 : 1));
            public override void Deserialize(ref SequenceReader<byte> reader, ref Action value) => value = SerializerBinary.ReadByte(ref reader) != 0 ? (() => { }) : null;
        }

        [Fact]
        public void ReflectionFallback_CustomFormatterForTheMemberType_DoesNotRescueTheMember()
        {
            var ceras = new CerasFastSerializer(config =>
            {
                config.AllowReflectionFallback = true;
                config.ConfigType<Action>().CustomFormatter = new DelegateNameFormatter();
            });

            var banned = AssertBanned(() => ceras.Serialize(new DelegateHolder { Value = 1, Callback = () => { } }));

            Assert.Contains(nameof(DelegateHolder.Callback), banned.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("IFormatter", banned.Message, StringComparison.Ordinal);
        }

        // ── runtime values in reference slots ─────────────────────────────────────────────────

        [CerasContext("Tests")]
        [CerasSerializable]
        public class ObjectSlotHolder { public int Before; public object Value; public int After; }

        public static TheoryData<string, object, string> BannedValues => new()
        {
            { "delegate", (Func<int, int>)(x => x * 2), "Func" },
            { "LINQ enumerator", new[] { 1, 2, 3 }.AsEnumerable().Select(x => x * 2).GetEnumerator(), "Enumerator" },
            { "MemberInfo", typeof(string).GetProperty("Length"), "PropertyInfo" },
            // A type the user excluded is only excluded as a *declaration*; its value arriving in a
            // slot that never asked for that is a value Ceras cannot write, not an omission.
            { "instance of an [Exclude]d type", new NotSerializableThing { Secret = 1 }, "NotSerializable" },
        };

        [Theory]
        [MemberData(nameof(BannedValues))]
        public void Save_BannedValueInObjectSlot_Throws(string _, object value, string expectedInMessage)
        {
            var banned = AssertBannedValueWrite(() => Raw().Serialize(new ObjectSlotHolder { Before = 1, Value = value, After = 2 }));

            // The message has to name the value that was rejected, not just that "something" was,
            // and it has to say which slot it went through so the member can be found.
            Assert.Contains(expectedInMessage, banned.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Object), banned.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Save_NullAndOrdinaryValuesInObjectSlot_RoundTrip()
        {
            // A null is a value the user actually has, and stays a legal null; an ordinary value
            // still round-trips. Neither can be satisfied by refusing the slot.
            var ceras = Raw();

            foreach (var value in new object[] { null, new List<int> { 4, 5 }, "text", 42 })
            {
                var clone = ceras.Deserialize<ObjectSlotHolder>(ceras.Serialize(new ObjectSlotHolder { Before = 1, Value = value, After = 2 }));

                Assert.Equal(value, clone.Value);
                Assert.Equal(1, clone.Before);
                Assert.Equal(2, clone.After);
            }
        }

        // ── Type slots ─────────────────────────────────────────────────────────────────────────

        // Not [CerasSerializable]: these need a Type slot Ceras serves with its own TypeFormatter,
        // which resolves types by name rather than against a manifest.
        public class TypeSlotHolder { public int Before; public Type Value; public int After; }

        public static TheoryData<Type> NonBannedTypeValues => new()
        {
            { null },
            { typeof(int) },
            { typeof(string) },
            { typeof(List<int>) },
            { typeof(Dictionary<string, int>) },
            { typeof(int[]) },
        };

        [Theory]
        [MemberData(nameof(NonBannedTypeValues))]
        public void Save_NonBannedTypeValue_RoundTrips(Type value)
        {
            var clone = Raw().Deserialize<TypeSlotHolder>(Raw().Serialize(new TypeSlotHolder { Before = 1, Value = value, After = 2 }));

            Assert.Equal(value, clone.Value);
            Assert.Equal(1, clone.Before);
            Assert.Equal(2, clone.After);
        }

        public static TheoryData<string, Type, string> BannedTypeValues => new()
        {
            { "plain banned type", typeof(Action), "Action" },
            { "closed generic delegate", typeof(Func<int>), "Func" },
            { "enumerator", typeof(System.Collections.IEnumerator), "IEnumerator" },
            { "reflection metadata", typeof(PropertyInfo), "PropertyInfo" },
            // Same rule as a value in a reference slot: a reference to an [Exclude]d type is a value
            // Ceras cannot write, and used to come back as a null that named a different meaning.
            { "reference to an [Exclude]d type", typeof(NotSerializableThing), "NotSerializable" },
        };

        [Theory]
        [MemberData(nameof(BannedTypeValues))]
        public void Save_BannedTypeValue_Throws(string _, Type value, string expectedInMessage)
        {
            var banned = AssertBannedValueWrite(() => Raw().Serialize(new TypeSlotHolder { Before = 1, Value = value, After = 2 }));

            Assert.Contains(expectedInMessage, banned.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Save_BannedTypeAsGenericArgument_ThrowsBeforeRebuildingTheType()
        {
            // This used to write a null marker for the Func<> argument, and the load then handed that
            // null to MakeGenericType, which answered with a bare ArgumentNullException naming
            // neither the type nor the argument. The rejected value is named at Save instead, so
            // MakeGenericType is never reached.
            Exception thrown = Record.Exception(() => Raw().Serialize(new TypeSlotHolder { Value = typeof(List<Func<int>>) }));

            var banned = BannedInChain(thrown);

            Assert.NotNull(banned);
            Assert.IsNotType<ArgumentNullException>(banned);
            // The rejected argument, and the type that referenced it, so the user can find the member.
            Assert.Contains("Func", banned.Message, StringComparison.Ordinal);
            Assert.Contains("List", banned.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(TypeSlotHolder.Value), thrown.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Deserialize_AHandBuiltPayloadWithANullGenericArgument_IsCorruption()
        {
            // The writer can no longer produce this, but an older file (or a damaged one) can, and
            // the reader has to say what is wrong with the file rather than leak MakeGenericType's
            // own failure.
            var ceras = Raw();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, -2, 3);          // NewGeneric
            TypeFormatter(ceras).Serialize(payload, typeof(List<>));  // the base definition
            SerializerBinary.WriteByte(payload, 1);                   // one argument ...
            SerializerBinary.WriteUInt32Bias(payload, -1, 3);         // ... written as a null marker

            var ex = Assert.Throws<CerasDataCorruptionException>(() => ReadType(ceras, payload.WrittenSpan.ToArray()));

            Assert.IsNotType<ArgumentNullException>(ex);
            Assert.Contains("argument 0", ex.Message, StringComparison.Ordinal);
            Assert.Contains("List", ex.Message, StringComparison.Ordinal);
        }

        // ── robust context ─────────────────────────────────────────────────────────────────────

        [CerasContext("Tests")]
        [CerasSerializable]
        public class RobustTypeSlotHolder { public int Before; public Type Value; public int After; }

        static CerasRobustSerializer Robust() => new CerasRobustSerializer(config =>
        {
            config.UseGeneratedFormatters(TestBase.Context);
            config.AllowReflectionFallback = true;
        });

        [Fact]
        public void RobustSave_BannedValues_Throw()
        {
            var bannedInObjectSlot = AssertBannedValueWrite(() => Robust().Save(
                new ObjectSlotHolder { Before = 1, Value = (Action)(() => { }), After = 2 }, 1));
            Assert.Contains("Action", bannedInObjectSlot.Message, StringComparison.Ordinal);

            // The robust Type formatter is a separate implementation and has to reach the same
            // verdict: without a ban check the value fell through to the registry lookup and was
            // reported as an undeclared type, pointing at a fix that cannot work for a banned shape.
            var bannedInTypeSlot = AssertBannedValueWrite(() => Robust().Save(
                new RobustTypeSlotHolder { Before = 1, Value = typeof(Func<int>), After = 2 }, 1));
            Assert.Contains("Func", bannedInTypeSlot.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RobustSave_NullAndOrdinaryValues_StillRoundTrip()
        {
            var clone = Robust().Load<ObjectSlotHolder>(Robust().Save(
                new ObjectSlotHolder { Before = 1, Value = null, After = 2 }, 1), 1);

            Assert.Equal(1, clone.Before);
            Assert.Null(clone.Value);
            Assert.Equal(2, clone.After);

            var typeClone = Robust().Load<RobustTypeSlotHolder>(Robust().Save(
                new RobustTypeSlotHolder { Before = 1, Value = typeof(List<int>), After = 2 }, 1), 1);
            Assert.Equal(typeof(List<int>), typeClone.Value);
        }

        // ── callbacks are unaffected ───────────────────────────────────────────────────────────

        public class CallbackHolder
        {
            public int Value;
            [Exclude] public Action Callback;

            public static int BeforeCalls, AfterCalls;
            public void Reset() { BeforeCalls = 0; AfterCalls = 0; }

            [OnBeforeSerialize] void Before() => BeforeCalls++;
            [OnAfterSerialize] void After() => AfterCalls++;
        }

        // The same shape without the [Exclude]: the member is the user's problem again.
        public class UnresolvedCallbackHolder
        {
            public int Value;
            public Action Callback;

            [OnBeforeSerialize] void Before() { }
            [OnAfterSerialize] void After() { }
        }

        [Fact]
        public void Callbacks_AreUnaffectedByTheBanPolicy()
        {
            // The policy is about members and values, not callbacks: an [Exclude]d delegate member
            // next to real callbacks must neither stop them running nor be rescued by them. (Clearing
            // the value in [OnBeforeSerialize] is a legitimate workaround - it runs before the
            // members are read - but it is not something Ceras verifies.)
            new CallbackHolder().Reset();
            var ceras = Raw();

            var clone = ceras.Deserialize<CallbackHolder>(ceras.Serialize(new CallbackHolder { Value = 4 }));

            Assert.Equal(1, CallbackHolder.BeforeCalls);
            Assert.Equal(1, CallbackHolder.AfterCalls);
            Assert.Equal(4, clone.Value);
            Assert.Null(clone.Callback);

            var banned = AssertBanned(() => ceras.Serialize(new UnresolvedCallbackHolder { Value = 4, Callback = () => { } }));
            Assert.Contains(nameof(UnresolvedCallbackHolder.Callback), banned.Message, StringComparison.Ordinal);
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────

        static CerasFastSerializer Raw() => new CerasFastSerializer(new CerasRawConfig { AllowReflectionFallback = true });

        sealed class GeneratorRun
        {
            public List<Diagnostic> Diagnostics;
            public List<Diagnostic> CompilationErrors;
        }

        static GeneratorRun RunGenerator(string source)
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var cerasLocation = typeof(CerasSerializer).Assembly.Location;
            if (!refs.OfType<PortableExecutableReference>().Any(r => r.FilePath == cerasLocation))
                refs.Add(MetadataReference.CreateFromFile(cerasLocation));

            var compilation = CSharpCompilation.Create(
                "CerasBannedDiagTest",
                new[] { CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new CerasFormatterGenerator().AsSourceGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

            return new GeneratorRun
            {
                Diagnostics = driver.GetRunResult().Results.SelectMany(r => r.Diagnostics).ToList(),
                CompilationErrors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList(),
            };
        }

        /// <summary>
        /// Runs <paramref name="action"/> and returns the <see cref="BannedTypeException"/> it must fail
        /// with, caught by type and nothing else.
        /// <para>
        /// These are the sites where the refusal is raised while the formatter is <em>built</em> (the
        /// reflection fallback's schema walk, or the static one), and that is the case this file needs
        /// to say something about: Ceras constructs a formatter through
        /// <c>Helpers.Instantiator.Create</c>, and <see cref="System.Reflection"/> wraps whatever a
        /// constructor throws. So the refusal used to reach the caller as
        /// <c>TargetInvocationException: Exception has been thrown by the target of an invocation</c>
        /// with the whole diagnosis on one <c>---&gt;</c> line — and <c>catch (BannedTypeException)</c>,
        /// which is the entire reason the type exists, could not reach it. It is not unwrapped here to
        /// make this helper convenient: <c>Instantiator.Create</c> rethrows a wrapped Ceras exception
        /// as itself (see the note there), and a caller that catches this type gets it. There is no
        /// walk, so a regression that put the wrapper back fails these tests rather than hiding inside
        /// them.
        /// </para>
        /// </summary>
        static BannedTypeException AssertBanned(Action action)
            => Assert.Throws<BannedTypeException>(action);

        /// <summary>
        /// The same refusal raised while a <em>value</em> is written rather than while the formatter is
        /// built. Here Ceras does add context of its own, on purpose: a member failure is wrapped with
        /// the member and the type it belongs to (and an object slot with one more wrapper naming the
        /// object), which is information the diagnosis did not have. The walk is therefore over
        /// Ceras's own wrappers — and it asserts that they are the <em>only</em> wrappers there are,
        /// so a reflection wrapper in the chain is a failure here too, not something to look past.
        /// </summary>
        static BannedTypeException AssertBannedValueWrite(Action action)
            => BannedInChain(Assert.ThrowsAny<Exception>(action));

        /// <summary>
        /// The <see cref="BannedTypeException"/> inside a chain Ceras built for itself, or a failure
        /// naming what was thrown instead. Every frame has to be a <see cref="CerasException"/>: that
        /// is the invariant the walk relies on, and it is the assertion that the
        /// <see cref="System.Reflection.TargetInvocationException"/> of a constructor can no longer
        /// hide in one of them.
        /// </summary>
        static BannedTypeException BannedInChain(Exception thrown)
        {
            for (var current = thrown; current != null; current = current.InnerException)
            {
                Assert.True(current is CerasException,
                    $"Expected the refusal to be wrapped in Ceras's own context only, but the chain contains " +
                    $"{current.GetType().FullName}, which is not a CerasException. The full chain was: {thrown}");
                if (current is BannedTypeException match) return match;
            }

            Assert.Fail($"Expected a BannedTypeException, but got: {thrown}");
            return null;
        }

        static TypeFormatter TypeFormatter(CerasSerializer ceras) => (TypeFormatter)ceras.GetSpecificFormatter(typeof(Type));

        // A SequenceReader is a ref struct, so the read has to live in a method of its own.
        static Type ReadType(CerasSerializer ceras, byte[] payload)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            Type value = null;
            TypeFormatter(ceras).Deserialize(ref reader, ref value);
            return value;
        }
    }
}
