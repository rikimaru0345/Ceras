// The member order the generated writer and the reflection fallback must agree on.
//
// A payload of a declared type is its members in one fixed order, and the order is *not* the
// declaration order: it is a total order over a sort key made of the member's type identity, the
// member's own name, its declaring type and whether it is a field or a property. Two writers can
// encode the same value to different bytes and each be perfectly self-consistent, because a save
// carries no field order of its own - only a schema section, which names the fields, and the
// matching-layout reader that never looks at it. So a divergence here is invisible in a round trip
// and shows up as one member read with its neighbour's value, or as a stream that runs out of
// alignment, on a build that reaches the type through the other writer.
//
// The key used to be spelled two different ways on the two sides:
//   * generated: a Roslyn display string - `MemberExplorer` sorted by
//     `ITypeSymbol.ToDisplayString(...)` with namespaces and containing types qualified, which spells a
//     tuple `(System.Int32, System.String)`, a nullable `System.Int32?`, a generic
//     `System.Collections.Generic.List<System.Int32>` and a nested type `Outer.Inner`;
//   * reflection: `Type.FullName` - `System.ValueTuple`2[[System.Int32, ...]]`,
//     `System.Nullable`1[[System.Int32, ...]]`, `System.Collections.Generic.List`1[[System.Int32, ...]]`
//     and `Outer+Inner`.
// Two spellings of one type sort differently, so the two sides chose different orders for the same
// type. A tuple-typed member is the clearest case: `(` is below every letter, so a generated writer
// put it first while a reflective one put it after `System.Int32`.
//
// Both sides now spell the key the same way: the *canonical stable type identity*,
// `Ceras.Format.TypeIdentity.GetCanonicalName` (generated: `TypeHelpers.GetRobustName`) - the same
// string the schema section already records a type under, which is required to be assembly-version
// free and runtime independent. One spelling, one order, both writers.
//
// What is pinned here:
//   * the two writers produce the same bytes, for a type whose members cover the shapes whose
//     spellings differ (tuple, nested tuple, tuple array, a user generic closed over a tuple,
//     nullable, nested type, plain generic, scalar, string) and for one with inherited members;
//   * either writer's bytes are read back by either reader, and by the migration interpreter;
//   * the order is the ordinal order of the canonical keys, computed from the runtime's own member
//     list, and the concrete list, so a change to either is a visible diff;
//   * a member's *wire* name ([CerasStableName]) is not part of the key: the order follows the CLR
//     name, which is what a rename does not change;
//   * a generic definition's members sort by their *declared* types (`!0`), because one emitted
//     writer serves every closure - the rule, and the one limit it has, are in the type's comment.
using System;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.MemberOrder
{
    /// <summary>A nested type, for the declaring-type spelling (`Outer+Inner`, not `Outer.Inner`).</summary>
    [CerasSerializable]
    public class MoNested
    {
        [CerasSerializable]
        public class Inner
        {
            public int Q;
        }
    }

    /// <summary>
    /// A user generic, closed over a tuple. Declared rather than built in, so its canonical name is
    /// answered by this context's own manifest instead of by name resolution. A *built-in* collection
    /// closed over a tuple (<c>List&lt;(int, string)&gt;</c>) and a nested tuple member
    /// (<c>(int, (int, string))</c>) are deliberately absent from this model: neither can be baked at
    /// all today, for reasons of their own that this change does not touch - the bake hands a
    /// canonical name that is not a CLR type name to <c>Type.GetType</c>, which throws on a nested
    /// argument list instead of returning null. Their canonical spellings are pinned by
    /// <c>The_Canonical_Spellings_The_Key_Uses_Are_These</c> instead.
    /// </summary>
    [CerasSerializable]
    public class MoBox<TItem>
    {
        public TItem Content;
        public int Extra;
    }

    /// <summary>
    /// One type per spelling whose order used to differ, declared in an order that is none of the
    /// three the two writers could pick, so a change of either is visible below.
    /// </summary>
    [CerasContext("MemberOrder")]
    [CerasSerializable]
    public class MoHolder
    {
        public (int, string) Tup;                                       // System.ValueTuple`2<..>
        public MoBox<(int, string)> Boxed;                             // MoBox`1<System.ValueTuple`2<..>>
        public int? Maybe;                                             // System.Nullable`1<System.Int32>
        public MoNested.Inner Deep;                                    // MoNested+Inner
        public List<int> Ints;                                         // List`1<System.Int32>
        public Dictionary<string, int> Map;                            // Dictionary`2<..>
        public string Name;                                            // System.String
        public int After;                                              // System.Int32

        /// <summary>
        /// Renamed on the wire, and deliberately to a name that sorts nowhere near where the CLR name
        /// does: that is what makes "the key is the CLR name, not the wire name" checkable.
        /// </summary>
        [CerasStableName("ZZZ_WireName")]
        public int AaaClrName;
    }

    /// <summary>
    /// A tuple array - the remaining shape whose canonical spelling the order depends on. It lives in
    /// a type of its own and is not part of the byte-identity tests, because the two writers do not
    /// agree on which <em>formatter</em> an array member gets: the generated writer binds the array's
    /// own formatter (nothing in front of it) where the reflection fallback asks for the reference
    /// formatter (a reference marker, then the array). That is a formatter-selection difference rather
    /// than an ordering one, it is worth exactly one byte per array member, and it is not what this
    /// change is about - so it is kept out of the comparison and reported on its own.
    /// </summary>
    [CerasContext("MemberOrder")]
    [CerasSerializable]
    public class MoArrayHolder
    {
        public (int, string)[] TupArray;                               // System.ValueTuple`2<..>[]
        public int After;                                              // System.Int32
    }

    /// <summary>
    /// Carries a closure of <see cref="MoGeneric{T}"/>, so the closure is part of the manifest and its
    /// field list can be inspected. The two members are here rather than on <see cref="MoHolder"/>
    /// because a type holding one is written by the same two writers either way, while the closure
    /// itself is where the substitution limit lives (see <see cref="MoGeneric{T}"/>).
    /// </summary>
    [CerasContext("MemberOrder")]
    [CerasSerializable]
    public class MoGenericHolder
    {
        public MoGeneric<int> Typed;
        public int After;
    }

    /// <summary>Inherited members, whose keys carry the <em>base</em> type as their declaring type.</summary>
    [CerasContext("MemberOrder")]
    [CerasSerializable]
    public class MoBase
    {
        public int BaseTag;
        public (int, string) BaseTup;
    }

    [CerasContext("MemberOrder")]
    [CerasSerializable]
    public class MoDerived : MoBase
    {
        public string Own;
        public int OwnTag;
    }

    /// <summary>
    /// A generic definition: one emitted writer serves every closure, so its members sort by their
    /// <em>declared</em> types, and a declared type parameter is spelled <c>!0</c> - which sorts
    /// before every real type name, so <c>Value</c> is written before <c>Tag</c> for every argument.
    /// (The old key spelled it by the parameter's <em>name</em>, so the order depended on the
    /// argument: <c>MoGeneric&lt;string&gt;</c> wrote <c>Tag</c> first. A wire format that moves when
    /// a type parameter is renamed is a wire format that has to be regenerated for a rename.)
    /// <para>
    /// The limit of the rule, stated here because it is the one place the two sides can still
    /// disagree: the reflection fallback sees a <em>closure</em>, and reflection substitutes the
    /// argument into the member's type, so a reflective writer for a closure whose argument sorts
    /// after <c>System.Int32</c> orders these two members the other way round. That is not a sort-key
    /// defect - it is what "one shared writer per definition" means, and it is why the payload order
    /// of a generic definition is a property of the definition rather than of a closure. The
    /// byte-identity tests below use types where a closure cannot disagree with the definition.
    /// </para>
    /// </summary>
    [CerasSerializable]
    public class MoGeneric<T>
    {
        public T Value;
        public int Tag;
    }

    public class MemberOrderUnificationTests : TestBase
    {
        new const string Context = "MemberOrder";

        /// <summary>
        /// The generated writer, and a second serializer over the *same* context whose writer for
        /// <paramref name="reflectedFor"/> is the reflection fallback instead. Registering the same
        /// context keeps the manifest - type ids, schema section, fingerprint - identical, so any
        /// difference between the two byte arrays is a member-order difference and nothing else.
        /// </summary>
        static (CerasRobustSerializer Generated, CerasRobustSerializer Reflection) Writers(Type reflectedFor)
        {
            var generated = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Context);
                c.AllowReflectionFallback = true;
            });

            var reflection = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Context);
                c.AllowReflectionFallback = true;
                c.ConfigType(reflectedFor).CustomFormatter = null;
            });

            return (generated, reflection);
        }

        static MoHolder Sample() => new MoHolder
        {
            Tup = (7, "tup"),
            Boxed = new MoBox<(int, string)> { Content = (6, "boxed"), Extra = 14 },
            Maybe = 8,
            Deep = new MoNested.Inner { Q = 9 },
            Ints = new List<int> { 10, 11 },
            Map = new Dictionary<string, int> { ["k"] = 12 },
            Name = "name",
            After = 13,
            AaaClrName = 15,
        };

        static void AssertSample(MoHolder clone)
        {
            Assert.Equal((7, "tup"), clone.Tup);
            Assert.Equal((6, "boxed"), clone.Boxed.Content);
            Assert.Equal(14, clone.Boxed.Extra);
            Assert.Equal(8, clone.Maybe);
            Assert.Equal(9, clone.Deep.Q);
            Assert.Equal(new[] { 10, 11 }, clone.Ints);
            Assert.Equal(12, clone.Map["k"]);
            Assert.Equal("name", clone.Name);
            Assert.Equal(13, clone.After);
            Assert.Equal(15, clone.AaaClrName);
        }

        // ── the two writers agree ──────────────────────────────────────────────────────────────

        [Fact]
        public void The_Generated_And_The_Reflective_Writer_Produce_The_Same_Bytes()
        {
            var (generated, reflection) = Writers(typeof(MoHolder));

            Assert.Equal(Hex(generated.Save(Sample(), 1)), Hex(reflection.Save(Sample(), 1)));
        }

        [Fact]
        public void The_Two_Writers_Agree_For_A_Type_With_Inherited_Members()
        {
            var (generated, reflection) = Writers(typeof(MoDerived));

            MoDerived SampleDerived() => new MoDerived
            {
                BaseTag = 1,
                BaseTup = (2, "base"),
                Own = "own",
                OwnTag = 3,
            };

            Assert.Equal(Hex(generated.Save(SampleDerived(), 1)), Hex(reflection.Save(SampleDerived(), 1)));
        }

        // ── and either writer's bytes are read by either reader ───────────────────────────────

        [Fact]
        public void Either_Writers_Bytes_Are_Read_By_Either_Reader()
        {
            var (generated, reflection) = Writers(typeof(MoHolder));
            var sample = Sample();

            AssertSample(generated.Load<MoHolder>(reflection.Save(sample, 1), 1));
            AssertSample(reflection.Load<MoHolder>(generated.Save(sample, 1), 1));
        }

        [Fact]
        public void The_Migration_Interpreter_Reads_What_The_Reflective_Writer_Wrote()
        {
            // The interpreter walks the *file's* schema section, which is the generated manifest's
            // field order. So it is the one reader that cannot be fooled by two self-consistent
            // writers: had the reflective writer emitted a different order, these members would come
            // back with each other's values and the stream would lose alignment.
            var (_, reflection) = Writers(typeof(MoHolder));

            AssertSample(reflection.Load<MoHolder>(SlowPath(reflection.Save(Sample(), 1)), 1));
        }

        // ── the order itself ───────────────────────────────────────────────────────────────────

        [Fact]
        public void The_Order_Is_The_Ordinal_Order_Of_The_Canonical_Keys()
        {
            // The rule, computed from the runtime's own member list rather than from the manifest it
            // is compared with: canonical type identity + member name + declaring type + kind, in
            // ordinal order. This is the same claim the byte tests above make, stated about the
            // schema instead of about the bytes.
            //
            // Only types whose members' types are the same on both sides. A *closure* of a user generic
            // is not one, because reflection substitutes the argument into the member's type while the
            // generated writer sorts by the declaration (see MoGeneric<T>); the closure of a definition
            // with a type-parameter member is asserted separately below instead.
            var serializer = new CerasRobustSerializer(c => c.UseGeneratedFormatters(Context));

            foreach (var type in new[] { typeof(MoHolder), typeof(MoDerived), typeof(MoGenericHolder), typeof(MoArrayHolder) })
            {
                var expected = serializer.GetTypeMetaData(type).PrimarySchema!.Members
                    .Select(m => (Wire: m.PersistentName, Key: CanonicalKey(m)))
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => x.Wire)
                    .ToArray();

                Assert.True(serializer.Config.SchemaRegistry.TryGetInfo(type, out var info), $"no entry for {type}");
                Assert.Equal(expected, info.Fields.Select(f => f.StableName).ToArray());
            }
        }

        [Fact]
        public void The_Order_Is_This_List()
        {
            // The concrete result, so a change to either writer's key is a visible diff rather than a
            // silent one. It is the ordinal order of the canonical type identities, which is why a
            // tuple member is no longer written first and a nullable is no longer written before an
            // array.
            var registry = new CerasRobustSerializer(c => c.UseGeneratedFormatters(Context))
                .Config.SchemaRegistry;
            registry.Seal();
            Assert.True(registry.TryGetInfo(typeof(MoHolder), out var info));

            Assert.Equal(
                new[]
                {
                    "Boxed",      // Ceras.Test.MemberOrder.MoBox`1<System.ValueTuple`2<..>>
                    "Deep",       // Ceras.Test.MemberOrder.MoNested+Inner
                    "Map",        // System.Collections.Generic.Dictionary`2<..>
                    "Ints",       // System.Collections.Generic.List`1<System.Int32>
                    "ZZZ_WireName",// System.Int32 - the wire name, in the CLR name's slot
                    "After",      // System.Int32
                    "Maybe",      // System.Nullable`1<System.Int32>
                    "Name",       // System.String
                    "Tup",        // System.ValueTuple`2<System.Int32,System.String>
                },
                info.Fields.Select(f => f.StableName).ToArray());
        }

        [Fact]
        public void The_Canonical_Spellings_The_Key_Uses_Are_These()
        {
            // The one spelling both writers now use, stated for every shape whose *old* spelling
            // sorted differently. It is also why a tuple member is no longer written first: `(` has
            // become `System.ValueTuple`.
            Assert.Equal("System.ValueTuple`2<System.Int32,System.String>",
                TypeIdentity.GetCanonicalName(typeof((int, string))));

            // A nested tuple and a tuple array, which the old display string spelled with
            // parentheses and the old reflection key with assembly-qualified arguments.
            Assert.Equal("System.ValueTuple`2<System.Int32,System.ValueTuple`2<System.Int32,System.String>>",
                TypeIdentity.GetCanonicalName(typeof((int, (int, string)))));
            Assert.Equal("System.ValueTuple`2<System.Int32,System.String>[]",
                TypeIdentity.GetCanonicalName(typeof((int, string)[])));

            // A nullable, a nested type and a closed generic - the three other shapes whose two
            // spellings (`A?` vs `Nullable`1[[A]]`, `Outer.Inner` vs `Outer+Inner`,
            // `List<A>` vs `List`1[[A]]`) sorted differently.
            Assert.Equal("System.Nullable`1<System.Int32>", TypeIdentity.GetCanonicalName(typeof(int?)));
            Assert.Equal("Ceras.Test.MemberOrder.MoNested+Inner", TypeIdentity.GetCanonicalName(typeof(MoNested.Inner)));
            Assert.Equal("System.Collections.Generic.List`1<System.Int32>",
                TypeIdentity.GetCanonicalName(typeof(List<int>)));

            // A declared type parameter is spelled by its position, not by its name: the order of a
            // generic definition's members must not move when the parameter is renamed.
            Assert.Equal("!0", TypeIdentity.GetCanonicalName(typeof(MoGeneric<>).GetGenericArguments()[0]));
        }

        [Fact]
        public void A_Generic_Definitions_Fields_Are_Written_In_Declared_Type_Order()
        {
            // `!0` sorts before `System.Int32`, so the shared writer emits the type-parameter member
            // first, for every argument. The manifest records that same order, because the payload is
            // written that way (see GenericMemberOrderTests for why the two used to disagree).
            var registry = new CerasRobustSerializer(c => c.UseGeneratedFormatters(Context))
                .Config.SchemaRegistry;
            registry.Seal();

            Assert.True(registry.TryGetInfo(typeof(MoGeneric<int>), out var info));
            Assert.Equal(new[] { "Value", "Tag" }, info.Fields.Select(f => f.StableName).ToArray());
        }

        /// <summary>
        /// The one sort key, spelled the one way: canonical type identity + member name + declaring
        /// type + kind. <c>SchemaMemberComparer.GetSortKey</c> is its runtime half and the emitted
        /// member order is its generator half, so this expression is the rule in full.
        /// </summary>
        static string CanonicalKey(Ceras.Helpers.SchemaMember m)
        {
            var declaring = m.Member.DeclaringType;
            return TypeIdentity.GetCanonicalName(m.MemberType) + m.Member.Name +
                   TypeIdentity.GetCanonicalName(declaring) + (m.Member is System.Reflection.FieldInfo ? "f" : "p");
        }

        static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    }
}
