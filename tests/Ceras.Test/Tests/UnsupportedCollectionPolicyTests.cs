// The owner's temporary collection policy (Ceras/Format/UnsupportedCollectionPolicy), end to end.
//
// The rule: ReadOnlyCollection<T> and ReadOnlyDictionary<TKey,TValue> are refused wherever a value
// would have to be produced — the built-in types and every subclass — and so is every subclass of
// List<T> and Dictionary<TKey,TValue>. The exact List<T> and Dictionary<TKey,TValue> stay supported, and
// so does everything else the built-in collection formatter serves. It applies to the robust serializer
// *and* to the raw/transient one. (The one documented exemption is the migration *skip* path: a value in
// a member the user has since removed is skipped rather than refused. There is no test for a refusal
// there, because there is no refusal.)
//
// Why the file is shaped the way it is: a refusal is only worth something if the supported shapes are still
// reachable, so almost every refusal test is paired with a control through the *same* position, and the
// supported controls are the shapes the policy is most likely to break by accident — the exact built-in
// List/Dictionary, an IReadOnly*/ICollection<> signature, a HashSet<> subclass, and a save made entirely of
// kept shapes.
//
// The positions, and why each needs its own site:
//   * generator — a compile error at the member (CERAS033) and at a declaration/context root (CERAS034);
//   * robust bake — the structural schema, the declared root, and the root of a Save;
//   * raw — a member, a root value, a value in an object slot, a System.Type slot, and a *read*;
//   * migration — a legacy file that names a refused type (pinned in CollectionShapeRejectionTests).
//
// The read positions are listed separately on purpose. A refusal on the way *in* proves nothing about a
// payload that already exists, and every one of these used to be able to produce a plausible empty result
// instead of an error.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.Format;
using Ceras.Generated;
using Ceras.SourceGenerator;

namespace Ceras.Test.CollectionPolicy
{
    // ── Refused shapes. None of them may appear as a *declared* member of a [CerasContext] type in this
    //    compilation: that is CERAS033, a build error by design. They reach the serializer through an
    //    `object` slot, a `System.Type` slot, a root, or the generator harness below.
    public class UcpListSubclass : List<int> { }
    public class UcpListSubclassOfSubclass : UcpListSubclass { }
    public class UcpDictSubclass : Dictionary<string, int> { }
    public class UcpReadOnlyListSubclass : ReadOnlyCollection<int>
    {
        public UcpReadOnlyListSubclass(IList<int> inner) : base(inner) { }
    }
    public class UcpReadOnlyDictSubclass : ReadOnlyDictionary<string, int>
    {
        public UcpReadOnlyDictSubclass(IDictionary<string, int> inner) : base(inner) { }
    }
    public class UcpReadOnlyListSubclassOfSubclass : UcpReadOnlyListSubclass
    {
        public UcpReadOnlyListSubclassOfSubclass(IList<int> inner) : base(inner) { }
    }

    /// <summary>Kept, and the control for every refusal below: a subclass of a collection the policy does not name.</summary>
    public class UcpSetSubclass : HashSet<int> { }

    /// <summary>
    /// A <c>ReadOnlyCollection&lt;T&gt;</c> subclass that can hold a reference to itself, so a cycle is
    /// possible through the wrapper.
    /// <para>
    /// Before the policy, the base <c>ReadOnlyCollection&lt;T&gt;</c> round-tripped through the raw
    /// serializer including this self-referencing case — so the *base* was never the broken part. What
    /// could not be saved was any <em>subclass</em>: the dedicated formatter was an
    /// <c>IFormatter&lt;ReadOnlyCollection&lt;T&gt;&gt;</c>, so the reference wrapper could not bind a
    /// serializer for the derived type and answered <c>Save</c> with a bare
    /// <see cref="ArgumentNullException"/> naming a "method" and neither the type nor the member.
    /// </para>
    /// <para>
    /// This test therefore must not claim the shape works in either direction: the policy refuses it, by
    /// name, like any other member of the family.
    /// </para>
    /// </summary>
    public class UcpSelfCyclingReadOnlyList : ReadOnlyCollection<int>
    {
        public UcpSelfCyclingReadOnlyList(IList<int> inner) : base(inner) { }

        public UcpSelfCyclingReadOnlyList Self;
    }

    // ── One context whose every member is a slot, so a single serializer exercises the whole policy ──

    [CerasContext("UcpCtx")]
    [CerasSerializable]
    public class UcpHolder
    {
        public int Before;
        public object Value;
        public Type RuntimeType;
        public int After;
    }

    [CerasContext("UcpCtx")]
    [CerasSerializable]
    public class UcpNestedHolder
    {
        public List<HashSet<int>> Kept;   // supported, and the control for the nested-argument walk
        public int After;
    }

    public class UnsupportedCollectionPolicyTests : TestBase
    {
        const string Ctx = "UcpCtx";

        static CerasFastSerializer Raw() => new CerasFastSerializer(new CerasRawConfig { AllowReflectionFallback = true });
        static CerasRobustSerializer Robust() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));
        static string Name(Type type) => TypeIdentity.GetCanonicalName(type);

        /// <summary>
        /// The namespace-qualified name without the metadata arity back-tick, which is how every
        /// user-facing message spells a type (ReflectionHelper.FriendlyName). Asserting against that rather
        /// than against the canonical form keeps the test about "the name the user sees", not about which
        /// of the two spellings one particular site happens to use.
        /// </summary>
        static string DisplayName(Type type) => Name(type).Split('`')[0];

        /// <summary>
        /// Every refused type, with the phrase its diagnosis has to contain. The phrase is asserted
        /// separately from the value, so a row cannot pass by being refused for the wrong reason.
        /// </summary>
        public static TheoryData<Type, string> Refused => new()
        {
            { typeof(ReadOnlyCollection<int>), "ReadOnlyCollection<T>" },
            { typeof(ReadOnlyDictionary<string, int>), "ReadOnlyDictionary<TKey,TValue>" },
            { typeof(UcpReadOnlyListSubclass), "ReadOnlyCollection<T>" },
            { typeof(UcpReadOnlyListSubclassOfSubclass), "ReadOnlyCollection<T>" },
            { typeof(UcpReadOnlyDictSubclass), "ReadOnlyDictionary<TKey,TValue>" },
            { typeof(UcpListSubclass), "subclass of List<T>" },
            { typeof(UcpListSubclassOfSubclass), "subclass of List<T>" },
            { typeof(UcpDictSubclass), "subclass of Dictionary<TKey,TValue>" },
        };

        /// <summary>The same families, as values, for the positions that take one.</summary>
        public static TheoryData<object, string> RefusedValues => new()
        {
            { new ReadOnlyCollection<int>(new[] { 1, 2 }), "ReadOnlyCollection<T>" },
            { new ReadOnlyDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1 }), "ReadOnlyDictionary<TKey,TValue>" },
            { new UcpReadOnlyListSubclass(new[] { 1, 2 }), "ReadOnlyCollection<T>" },
            { new UcpReadOnlyDictSubclass(new Dictionary<string, int> { ["a"] = 1 }), "ReadOnlyDictionary<TKey,TValue>" },
            { new UcpListSubclass { 1, 2 }, "subclass of List<T>" },
            { new UcpDictSubclass { ["a"] = 1 }, "subclass of Dictionary<TKey,TValue>" },
        };

        /// <summary>What the policy must never refuse, whatever position it is asked in.</summary>
        public static TheoryData<object> KeptValues => new()
        {
            { new List<int> { 1, 2 } },
            { new Dictionary<string, int> { ["a"] = 1 } },
            { new HashSet<int> { 3 } },
            { new UcpSetSubclass { 4 } },
            { new Queue<int>(new[] { 5 }) },
            { new Stack<int>(new[] { 6 }) },
            { new LinkedList<int>(new[] { 7 }) },
            { new SortedSet<int> { 8 } },
            { new SortedList<string, int> { ["k"] = 9 } },
            { new int[] { 10, 11 } },
        };

        // ── The predicate ────────────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Refused))]
        public void The_Predicate_Refuses_Every_Decided_Shape(Type type, string expectedInReason)
        {
            Assert.True(UnsupportedCollectionPolicy.IsUnsupported(type), $"{Name(type)} should be refused");
            Assert.Contains(expectedInReason, UnsupportedCollectionPolicy.BanReason, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Predicate_Answers_For_Everything_And_Never_Raises()
        {
            // A predicate a caller can ask about any type without guarding the call.
            foreach (var type in new[]
            {
                null, typeof(int), typeof(void), typeof(int[,]), typeof(int[]), typeof(string),
                typeof(List<>), typeof(Dictionary<,>), typeof(Delegate), typeof(Action),
                typeof(IReadOnlyList<int>), typeof(IReadOnlyDictionary<string, int>),
                typeof(ICollection<int>), typeof(IDictionary<string, int>), typeof(IEnumerable<int>),
                typeof(UcpSetSubclass), typeof(Queue<int>), typeof(Stack<int>),
            })
            {
                Assert.Null(Record.Exception(() => UnsupportedCollectionPolicy.IsUnsupported(type)));
            }

            // And it distinguishes the kept built-in from its own subclass, which is the whole rule.
            Assert.False(UnsupportedCollectionPolicy.IsUnsupported(typeof(List<int>)));
            Assert.False(UnsupportedCollectionPolicy.IsUnsupported(typeof(Dictionary<string, int>)));
            Assert.True(UnsupportedCollectionPolicy.IsUnsupported(typeof(UcpListSubclass)));
        }

        // ── The raw / transient serializer ────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(RefusedValues))]
        public void Raw_ObjectSlot_Refuses_The_Value_By_Name(object value, string expectedInReason)
        {
            var ceras = Raw();
            var banned = AssertBanned(() => ceras.Serialize(new UcpHolder { Before = 1, Value = value, After = 2 }));

            // The value that was rejected, the slot it went through (so the member can be found), and the
            // reason that names the rule. The slot is named as a *declared type*, which is what the
            // reference wrapper has and what a reader of the message can match against their model.
            Assert.Contains(expectedInReason, banned.Message, StringComparison.Ordinal);
            Assert.Contains(DisplayName(value.GetType()), banned.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(Object), banned.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(banned);
        }

        [Theory]
        [MemberData(nameof(RefusedValues))]
        public void Raw_Root_Refuses_The_Value_By_Name(object value, string expectedInReason)
        {
            var ceras = Raw();
            var banned = Assert.ThrowsAny<Exception>(() => ceras.Serialize(value));

            // Either site may fire — the reference wrapper names the runtime value, the resolver refuses to
            // build a formatter for the type at all — and both carry the same sentence.
            Assert.Contains(expectedInReason, UnwrapChain(banned), StringComparison.Ordinal);
            AssertNoRawReflectionFailure(banned);
        }

        [Theory]
        [MemberData(nameof(Refused))]
        public void Raw_TypeSlot_Refuses_The_Type_By_Name(Type value, string expectedInReason)
        {
            // A Type value is a name, so the diagnosis names the type and not the member — the same
            // contract the existing banned-type-in-a-Type-slot test pins.
            var ceras = Raw();
            var banned = AssertBanned(() => ceras.Serialize(new UcpHolder { Before = 1, RuntimeType = value, After = 2 }));

            Assert.Contains(expectedInReason, banned.Message, StringComparison.Ordinal);
            Assert.Contains(DisplayName(value), banned.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(banned);
        }

        [Fact]
        public void Raw_TypeSlot_Refuses_One_Reached_As_A_Generic_Argument()
        {
            // The nested route: the *slot's* type (List<ReadOnlyCollection<int>>) is a supported shape, and
            // only the argument is refused. The writer would otherwise record the argument and hand
            // MakeGenericType a type Ceras cannot serialize — which is how a bare ArgumentNullException
            // used to escape when the argument was written as a null instead.
            var ceras = Raw();
            var banned = AssertBanned(() => ceras.Serialize(new UcpHolder
            {
                Before = 1,
                RuntimeType = typeof(List<>).MakeGenericType(typeof(ReadOnlyCollection<int>)),
                After = 2,
            }));

            Assert.Contains("ReadOnlyCollection<T>", banned.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(banned);
        }

        [Theory]
        [MemberData(nameof(Refused))]
        public void Robust_TypeSlot_Refuses_The_Type_By_Name(Type value, string expectedInReason)
        {
            // The robust Type formatter is a separate implementation from the transient one, and without the
            // check a refused type would fall through to the registry lookup and be reported as an
            // *undeclared* type — advice that cannot work for a shape Ceras will never describe.
            var banned = AssertBanned(() => Robust().Save(new UcpHolder { Before = 1, RuntimeType = value, After = 2 }, 1));

            Assert.Contains(expectedInReason, banned.Message, StringComparison.Ordinal);
            Assert.Contains(DisplayName(value), banned.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(banned);
        }

        [Fact]
        public void Robust_TypeSlot_Refuses_One_Reached_As_A_Generic_Argument()
        {
            var banned = AssertBanned(() => Robust().Save(new UcpHolder
            {
                Before = 1,
                RuntimeType = typeof(List<>).MakeGenericType(typeof(ReadOnlyCollection<int>)),
                After = 2,
            }, 1));

            Assert.Contains("ReadOnlyCollection<T>", banned.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(banned);
        }

        [Fact]
        public void Raw_Read_Refuses_A_Payload_That_Names_A_Refused_Type()
        {
            // A payload can outlive the policy: the raw format names types by string, so a buffer written
            // before it can still name one. Reading it must refuse too, or a serializer that will not write
            // a value would still hand it back on the way in. The bytes are written by hand, because the
            // writer refuses to produce them — which is the point.
            var ceras = Raw();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, -3, 3);   // NewSingle, bias 3
            SerializerBinary.WriteString(payload, "System.Collections.ObjectModel.ReadOnlyCollection`1[[System.Int32, System.Private.CoreLib, Version=9.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], System.Private.CoreLib");

            var ex = Record.Exception(() => ReadType((TypeFormatter)ceras.GetSpecificFormatter(typeof(Type)), payload.WrittenSpan.ToArray()));

            var banned = Assert.IsType<BannedTypeException>(ex);
            Assert.Contains("ReadOnlyCollection<T>", banned.Message, StringComparison.Ordinal);
        }

        /// <summary>A SequenceReader is a ref struct, so the read has to live in a method of its own.</summary>
        static Type ReadType(TypeFormatter formatter, byte[] payload)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            Type value = null;
            formatter.Deserialize(ref reader, ref value);
            return value;
        }

        [Theory]
        [MemberData(nameof(KeptValues))]
        public void Raw_Kept_Shapes_Still_Round_Trip(object value)
        {
            var ceras = Raw();
            var clone = ceras.Deserialize<UcpHolder>(ceras.Serialize(new UcpHolder { Before = 1, Value = value, After = 2 }));

            Assert.Equal(value.GetType(), clone.Value.GetType());
            Assert.Equal(1, clone.Before);
            Assert.Equal(2, clone.After);
        }

        /// <summary>
        /// The self-cycle variant, on both serializers. The assertion is on the diagnosis, not on a round
        /// trip — the point of the policy is that this shape is refused rather than claimed — and the whole
        /// chain must be free of the bare reflection failure it used to surface.
        /// </summary>
        [Fact]
        public void A_ReadOnlyCollection_Self_Cycle_Is_Refused_Rather_Than_Claimed()
        {
            var self = new UcpSelfCyclingReadOnlyList(new[] { 1, 2 });
            self.Self = self;

            var raw = AssertBanned(() => Raw().Serialize(new UcpHolder { Before = 1, Value = self, After = 2 }));
            Assert.Contains("ReadOnlyCollection<T>", raw.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(raw);

            var robust = AssertBanned(() => Robust().Save(new UcpHolder { Before = 1, Value = self, After = 2 }, 1));
            Assert.Contains("ReadOnlyCollection<T>", robust.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(robust);
        }

        // ── The robust serializer ────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(RefusedValues))]
        public void Robust_ObjectSlot_Refuses_The_Value_By_Name(object value, string expectedInReason)
        {
            var banned = AssertBanned(() => Robust().Save(new UcpHolder { Before = 1, Value = value, After = 2 }, 1));

            Assert.Contains(expectedInReason, banned.Message, StringComparison.Ordinal);
            AssertNoRawReflectionFailure(banned);
        }

        [Theory]
        [MemberData(nameof(Refused))]
        public void Robust_Structural_Schema_Refuses_The_Shape(Type type, string expectedInReason)
        {
            // The bake's own entry point, and the one path DeclareRoot, a member the manifest does not
            // contain, and a Save root all go through.
            var ex = Assert.Throws<CerasSchemaException>(() => SchemaRegistry.TryBuildStructuralSchema(type, out _));

            Assert.Contains(expectedInReason, ex.Message, StringComparison.Ordinal);
            Assert.Contains(Name(type), ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(Refused))]
        public void Robust_DeclareRoot_Refuses_The_Shape(Type type, string expectedInReason)
        {
            var ex = Assert.Throws<CerasSchemaException>(() => new CerasRobustConfig().SchemaRegistry.DeclareRoot(type));

            Assert.Contains(expectedInReason, ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(Refused))]
        public void Robust_Root_Refuses_The_Shape_Before_Any_Byte_Is_Written(Type type, string expectedInReason)
        {
            // Declaring the root is the first thing Save does, so a refused root fails before the header
            // exists — and the message has to be the policy's own, not the generic "not part of the
            // manifest", which would send the user looking for a declaration problem that cannot help.
            Exception ex = Record.Exception(() => SaveRoot(type));
            string message = UnwrapChain(ex);

            Assert.Contains(expectedInReason, message, StringComparison.Ordinal);
            Assert.Contains(Name(type), message, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>Save&lt;T&gt;</c> is generic, so the refused type is closed into it by reflection and the
        /// <c>TargetInvocationException</c> unwrapped — the value is irrelevant, because the refusal is
        /// about the root <em>type</em> a context would have to describe.
        /// </summary>
        static void SaveRoot(Type rootType)
        {
            var save = typeof(UnsupportedCollectionPolicyTests)
                .GetMethod(nameof(SaveRootOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(rootType);

            try
            {
                save.Invoke(null, null);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        static void SaveRootOf<T>()
        {
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));
            serializer.Save(default(T), 1);
        }

        [Fact]
        public void Robust_Kept_Shapes_Still_Round_Trip_On_Both_Readers()
        {
            var s = Robust();
            var holder = new UcpNestedHolder { Kept = new List<HashSet<int>> { new HashSet<int> { 1, 2 } }, After = 77 };
            var bytes = s.Save(holder, 1);

            foreach (var clone in new[] { s.Load<UcpNestedHolder>(bytes, 1), s.Load<UcpNestedHolder>(SlowPath(bytes), 1) })
            {
                Assert.Single(clone.Kept);
                Assert.Equal(new[] { 1, 2 }, clone.Kept[0].OrderBy(x => x));
                Assert.Equal(77, clone.After);
            }
        }

        [Fact]
        public void Robust_The_Exact_BuiltIn_Collections_Still_Bake()
        {
            // The control for every refusal in this file: the exact List<T>/Dictionary<TKey,TValue> are the
            // shapes most at risk from a rule that says "derives from List<T>", so they must keep exactly
            // the structural descriptor they had — a name change here would be a format change.
            Assert.True(SchemaRegistry.TryBuildStructuralSchema(typeof(List<int>), out var list));
            Assert.Equal("System.Collections.Generic.List`1", list.UnderlyingTypeName);
            Assert.Equal(new[] { "System.Int32" }, list.ArgTypeNames);

            Assert.True(SchemaRegistry.TryBuildStructuralSchema(typeof(Dictionary<string, int>), out var dict));
            Assert.Equal("System.Collections.Generic.Dictionary`2", dict.UnderlyingTypeName);
            Assert.Equal(new[] { "System.String", "System.Int32" }, dict.ArgTypeNames);

            Assert.True(SchemaRegistry.TryBuildStructuralSchema(typeof(UcpSetSubclass), out var set));
            Assert.Equal("System.Collections.Generic.HashSet`1", set.UnderlyingTypeName);
        }

        // ── The source generator ──────────────────────────────────────────────────────────

        static string Source(string members, string extraTypes = "") => $@"
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        const string SubclassDeclarations = @"
    public class MyList<T> : List<T> { }
    public class MyDict : Dictionary<string, int> { }
    public class MyReadOnlyList : ReadOnlyCollection<int> { public MyReadOnlyList(IList<int> inner) : base(inner) { } }
    public class MyReadOnlyDict : ReadOnlyDictionary<string, int> { public MyReadOnlyDict(IDictionary<string, int> inner) : base(inner) { } }
    public class MySet : HashSet<int> { }
    public class MyQueue : Queue<int> { }";

        public static TheoryData<string, string> RefusedMembers => new()
        {
            { "public System.Collections.ObjectModel.ReadOnlyCollection<int> Items;", "ReadOnlyCollection<int>" },
            { "public System.Collections.ObjectModel.ReadOnlyDictionary<string, int> Items;", "ReadOnlyDictionary<string, int>" },
            { "public MyReadOnlyList Items;", "MyReadOnlyList" },
            { "public MyReadOnlyDict Items;", "MyReadOnlyDict" },
            { "public MyList<int> Items;", "MyList<int>" },
            { "public MyDict Items;", "MyDict" },
            // Nested reachability: the member's own type is supported and the argument is not, so the
            // offending type is what has to be named or the user fixes the wrong end of it.
            { "public List<System.Collections.ObjectModel.ReadOnlyCollection<int>> Items;", "ReadOnlyCollection<int>" },
            { "public Dictionary<string, System.Collections.ObjectModel.ReadOnlyDictionary<string, int>> Items;", "ReadOnlyDictionary<string, int>" },
            { "public System.Collections.ObjectModel.ReadOnlyCollection<int>[] Items;", "ReadOnlyCollection<int>" },
            { "public List<MyList<int>> Items;", "MyList<int>" },
            { "public HashSet<MyReadOnlyDict> Items;", "MyReadOnlyDict" },
            { "public KeyValuePair<int, MyReadOnlyDict> Items;", "MyReadOnlyDict" },
            // An auto-property, so the rule is not quietly limited to fields.
            { "public System.Collections.ObjectModel.ReadOnlyCollection<int> Items { get; set; }", "ReadOnlyCollection<int>" },
        };

        [Theory]
        [MemberData(nameof(RefusedMembers))]
        public void Generator_RefusedMember_EmitsCERAS033(string member, string expectedInMessage)
        {
            var run = RunGenerator(Source(member, SubclassDeclarations));

            Assert.Contains("CERAS033", run.Diagnostics.Select(d => d.Id));
            Assert.Contains(expectedInMessage, run.Text("CERAS033"));

            // One root cause, one diagnostic: the member is not emitted, so it is not also reported as a
            // member without a formatter, as a construction problem, or as a System-namespace shape.
            Assert.DoesNotContain("CERAS021", run.Diagnostics.Select(d => d.Id));
            Assert.DoesNotContain("CERAS003", run.Diagnostics.Select(d => d.Id));
            Assert.DoesNotContain("CERAS001", run.Diagnostics.Select(d => d.Id));
            Assert.DoesNotContain("CERAS029", run.Diagnostics.Select(d => d.Id));

            // And the generated code is still valid C#, so the build failure a user sees is the one
            // actionable diagnostic rather than a second wave of errors from broken output.
            Assert.Empty(run.CompilationErrors);
        }

        public static TheoryData<string> KeptMembers => new()
        {
            { "public List<int> Items;" },
            { "public Dictionary<string, int> Items;" },
            { "public HashSet<int> Items;" },
            { "public MySet Items;" },
            { "public Queue<int> Items;" },
            { "public MyQueue Items;" },
            { "public Stack<int> Items;" },
            { "public LinkedList<int> Items;" },
            { "public int[] Numbers; public int[][] Jagged;" },
            { "public List<HashSet<int>> Nested;" },
            { "public HashSet<MySet> Nested;" },
            // [Exclude] is the documented way to keep a refused member out of the payload, so it has to
            // stay a way out.
            { "[Exclude] public System.Collections.ObjectModel.ReadOnlyCollection<int> Items;" },
            { "[Exclude] public MyList<int> Items;" },
        };

        [Theory]
        [MemberData(nameof(KeptMembers))]
        public void Generator_KeptMember_DoesNotEmitCERAS033(string member)
        {
            var run = RunGenerator(Source(member, SubclassDeclarations));

            Assert.DoesNotContain("CERAS033", run.Diagnostics.Select(d => d.Id));
            Assert.DoesNotContain("CERAS034", run.Diagnostics.Select(d => d.Id));
            Assert.Empty(run.CompilationErrors);
        }

        /// <summary>
        /// The game-facing half of the rule: an <c>IReadOnly*</c> / <c>ICollection&lt;T&gt;</c> / <c>IList&lt;T&gt;</c>
        /// signature is an ordinary API declaration, not a collection shape, and nothing here serializes one.
        /// A name-shaped check would reject code that has nothing to do with serialization, so the test
        /// names the real pattern (a method parameter, a getter, a builder argument) rather than a member.
        /// </summary>
        [Fact]
        public void Generator_Ordinary_ReadOnly_Signatures_Are_Not_Rejected()
        {
            var run = RunGenerator(@"
using System;
using System.Collections.Generic;
using Ceras;
namespace T {
    public class Game
    {
        public IReadOnlyList<IReadOnlyDictionary<string, int>> Snapshot()
            => new List<IReadOnlyDictionary<string, int>>();

        public IReadOnlyCollection<int> Instances => new List<int>();
        public int Count(IReadOnlyList<int> items) => items.Count;
        public ICollection<int> Take(ICollection<int> source) => source;
        public IList<int> AsList(IEnumerable<int> source) => new List<int>(source);
        public IReadOnlyDictionary<string, int> Lookup() => null;
    }

    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder { public int Value; }
}");

            Assert.DoesNotContain("CERAS033", run.Diagnostics.Select(d => d.Id));
            Assert.DoesNotContain("CERAS034", run.Diagnostics.Select(d => d.Id));
            Assert.Empty(run.CompilationErrors);
        }

        [Fact]
        public void Generator_RefusedContextRoot_EmitsCERAS034()
        {
            // A context root that is itself a refused shape. It gets no generated formatter and no schema
            // the bake accepts, so without this the context would only fail on its first Save.
            var run = RunGenerator(@"
using System;
using System.Collections.Generic;
using Ceras;
namespace T {
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class MyListRoot : List<int> { }
}");

            Assert.Contains("CERAS034", run.Diagnostics.Select(d => d.Id));
            Assert.Contains("MyListRoot", run.Text("CERAS034"));
            Assert.DoesNotContain("CERAS033", run.Diagnostics.Select(d => d.Id));
            Assert.Empty(run.CompilationErrors);
        }

        [Fact]
        public void Generator_RefusedDeclaration_EmitsCERAS034()
        {
            // An assembly-level declaration naming one, reported where the declaration is written.
            var run = RunGenerator(@"
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Ceras;
[assembly: CerasKnownType(typeof(System.Collections.ObjectModel.ReadOnlyCollection<int>), ""Tests"")]
namespace T {
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder { public int Value; }
}");

            Assert.Contains("CERAS034", run.Diagnostics.Select(d => d.Id));
            Assert.Contains("ReadOnlyCollection", run.Text("CERAS034"));
            Assert.DoesNotContain("CERAS030", run.Diagnostics.Select(d => d.Id));
            Assert.Empty(run.CompilationErrors);
        }

        [Fact]
        public void Generator_KeptDeclaration_DoesNotEmitCERAS034()
        {
            var run = RunGenerator(@"
using System;
using System.Collections.Generic;
using Ceras;
[assembly: CerasKnownType(typeof(List<int>), ""Tests"")]
[assembly: CerasKnownType(typeof(Dictionary<string, int>), ""Tests"")]
namespace T {
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public class Holder { public int Value; }
}");

            Assert.DoesNotContain("CERAS034", run.Diagnostics.Select(d => d.Id));
            Assert.DoesNotContain("CERAS030", run.Diagnostics.Select(d => d.Id));
            Assert.Empty(run.CompilationErrors);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────

        sealed class GeneratorRun
        {
            public List<Diagnostic> Diagnostics = new();
            public List<string> CompilationErrors = new();

            public string Text(string id)
            {
                var messages = Diagnostics.Where(d => d.Id == id).Select(d => d.GetMessage()).ToList();
                Assert.NotEmpty(messages);
                return string.Join(" | ", messages);
            }
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
                "CerasCollectionPolicyTest",
                new[] { CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new CerasFormatterGenerator().AsSourceGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

            return new GeneratorRun
            {
                Diagnostics = driver.GetRunResult().Results.SelectMany(r => r.Diagnostics).ToList(),
                CompilationErrors = output.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString())
                    .ToList(),
            };
        }

        static BannedTypeException AssertBanned(Action action)
        {
            Exception ex = Assert.ThrowsAny<Exception>(action);
            for (var current = ex; current != null; current = current.InnerException)
                if (current is BannedTypeException match) return match;

            Assert.Fail($"Expected a BannedTypeException, but got: {ex}");
            return null!;
        }

        /// <summary>Every message in the chain, joined, so a site that wraps the diagnosis still matches.</summary>
        static string UnwrapChain(Exception ex)
        {
            var messages = new List<string>();
            for (var current = ex; current != null; current = current.InnerException) messages.Add(current.Message);
            return string.Join(" | ", messages);
        }

        /// <summary>
        /// The point of refusing these shapes rather than admitting them: never a bare reflection failure
        /// (MissingMethodException, ArgumentNullException("method"), a cast, an unwrapped
        /// TargetInvocationException) — which is exactly what the dedicated ReadOnlyCollection formatter and
        /// the generic collection formatter used to surface.
        /// </summary>
        static void AssertNoRawReflectionFailure(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                Assert.IsNotType<MissingMethodException>(current);
                Assert.IsNotType<ArgumentNullException>(current);
                Assert.IsNotType<InvalidCastException>(current);
                Assert.IsNotType<System.Reflection.TargetInvocationException>(current);
            }
        }
    }
}
