// The reflection fallback in a robust context: the last door that writes a member list no schema section
// describes.
//
// A robust save describes every value it writes, and it describes a value one of two ways. A value written
// member by member is described by the member list its manifest entry carries — the names, the types and
// the order — and that list is hashed into the layout fingerprint, which is what decides whether a reader
// may take the matching-layout fast path or must run the migration interpreter. A value written by a user
// custom formatter is described as an opaque block instead, with the formatter's identity recorded
// (RobustCustomFormatterPolicy, "Format/LAYOUT.md", "A user custom formatter's payload is an opaque
// block").
//
// The built-in reflection fallback is on the *first* road and does not know it. `ReflectionFallbackResolver`
// is the last resolver in `CerasSerializer.GetSpecificFormatter`, it answers for every type nothing else
// claims, and it derives its member list from the CLR type at run time with `SchemaMemberComparer`. The
// manifest's list for the same type was derived at compile time by the source generator. Nothing compares
// the two, and nothing records the runtime one, so:
//
//   * a type the generator registers a *member-less placeholder* for (any type it emits no formatter for —
//     an engine object, or one excluded with [CerasGeneratorBlacklist]) is described as "a class with no
//     members" while the payload holds every reflected member;
//   * a type whose members the two sides *order* differently is described in one order and written in
//     another;
//   * a type whose members the two sides *count* differently (a runtime [Exclude] the generator cannot see)
//     is described with members the payload does not contain.
//
// The order case has since been closed at its source, by the one-key change master carries
// (`TypeHelpers.MemberSortKey` / `SchemaMemberComparer`, one canonical spelling both sides): a tuple-typed,
// nullable-typed or nested-typed member no longer sorts differently on the two, so the two member lists of
// a *generated* type are now equal and its bytes are too. This file therefore pins the order half of the
// guard the only way it is still reachable — a manifest entry that states an order the writer does not
// produce, which a hand-written `SchemaRegistry.Register` descriptor can do and the generator cannot — and
// pins the tuple case as the *parity* it is now, from the fallback's side.
// `MemberOrderUnificationTests` pins the same parity from the generated side, over a wider set of
// spellings; the tuple here is the one this file's measurement was taken on.
//
// All three carry the same layout fingerprint as a save written by the generated formatter, so a later
// build that disagrees takes the fast path, reads the payload member by member in *its* order, and returns
// a wrong value with nothing reported. Measured on the unguarded build, with both contexts sealed first (an
// unsealed registry's fingerprint is smaller than a baked one's, which is a measurement artefact and not a
// fact about the format — every comparison below is between two sealed registries):
//
//   blacklisted class, two builds that drop different same-width members
//     writer {A,B,C}   reader {B,D,C}   both fingerprint 827E843A71E6D170
//     the load SUCCEEDED, every byte of the data section was consumed, and the result was
//     A=00000000 (never written), B=0A0A0A0A (A's value), D=0B0B0B0B (B's value) and
//     C=0C0C0C0C0C0C0C0C (right, by the accident that a 4-byte member sat in front of it),
//     for a model saved A=0A0A0A0A B=0B0B0B0B C=0C0C0C0C0C0C0C0C D=0D0D0D0D
//
//   tuple-typed member — measured on the unguarded build, *before* the two keys were unified, so it is
//   the record of what the order divergence was worth and not a description of today's build
//     described order [Tup, A, B]   runtime order [A, B, Tup]   one fingerprint (8A5D65EABEA175AD)
//     data written by the generated writer 22222222 33333333 11111111 44444444
//     data written by the reflected writer 11111111 44444444 22222222 33333333
//     the reflected reader of the generated file returned A=22222222 Tup=(11111111,44444444) B=33333333,
//     for a model saved as A=11111111 Tup=(22222222,33333333) B=44444444
//     …and after the unification both writers write 11111111 22222222 33333333 44444444, which is
//     `ATupleTypedMemberNowSortsTheSameOnBothSidesAndWritesTheSameBytes`
//
//   one member dropped from a *generated* type (a count divergence rather than a swap)
//     the same fingerprint, and the load failed on the trailing-byte check with "4 byte(s) of the data
//     section were never read" — loud, but only because of where the dropped member happened to sit
//
// The two fingerprints above are from the throwaway context the measurement ran in, so they are recorded
// rather than reproducible: what this file asserts is the *equality* of two fingerprints, which is the
// whole point, and the payload bytes, which do not depend on a context's id assignment.
//
// The guard in `RobustReflectionFallbackPolicy` is asked at the resolver line, before the formatter is
// built and before it is cached. It asks two separate things, and neither substitutes for the other:
//
//   1. the member list — count, ordered stable names, each member's type as an identity. That is all a
//      manifest entry records, so it is all a member-by-member payload can be checked against.
//   2. each member's own reference marker, which is in NO member list, so (1) cannot see it and does not
//      claim to. A member declared as an array type is written through `ArrayFormatter<T>` by the
//      generated writer and through `ReferenceFormatter_KnownSealedType<T[]>` by the fallback — one extra
//      marker byte in front of the same payload, same fingerprint, equal member names and types. Measured
//      byte for byte and refused outright; `TheTwoWritersPutDifferentBytesInFrontOfAnArrayMemberWithEqualMemberLists`
//      pins the measurement and `AnArrayTypedMemberIsRefusedOnSaveOnLoadAndAgainAfterwards` pins the guard.
//
// What it deliberately does NOT do is compare the `IsMonomorphic` flag: the generator and the runtime
// compute it differently for a collection member, and `TheMonomorphicFlagDiffersForCollectionsButTheBytesDoNot`
// measures that the bytes are identical anyway, so a flag comparison would refuse a payload that is
// correct.
//
// The tests pin: the shape that makes the door reachable (a blacklisted class *and* a blacklisted struct),
// the refusal on the Save and the Load path, the same-width-count case, the order case as a refusal reached
// through a hand-written descriptor and as byte parity for a generated type, the array-marker case with its
// measurement and its Save/Load/repeat refusals, that a refusal leaves nothing cached, that a described
// selection (the one case that was working and produced correct bytes) still saves with byte-identical
// output, and that a raw/transient context is untouched.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

// The generator emits no formatter for these, and registers a member-less placeholder for them instead —
// the shape an engine object has, and the only shape a formatter can be selected for without a generated
// formatter having been installed first. Asserted in `TheBlacklistedTypesGetAMemberlessPlaceholder` so a
// generator that changed its mind fails here rather than quietly making this file a test of something
// else.
[assembly: Ceras.CerasGeneratorBlacklist("Ceras.Test.ReflectionLayout.Engine*")]

namespace Ceras.Test.ReflectionLayout
{
    /// <summary>The root of the class half. It holds the payload, so a save walks into it.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class Root
    {
        public int Sentinel;
        public EngineObject Payload;
    }

    /// <summary>The root of the struct half: same shape, but the payload is a value type.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class StructRoot
    {
        public int Sentinel;
        public EnginePart Payload;
    }

    /// <summary>
    /// Blacklisted, so the manifest holds a member-less <c>Class</c> entry for it and nothing installs a
    /// formatter: the reflection fallback is the only formatter that can ever be selected for it.
    /// </summary>
    public class EngineObject
    {
        public int Id;
        public string Label;
    }

    /// <summary>The same, for a value type: a member-less <c>Struct</c> entry, written inline.</summary>
    public struct EnginePart
    {
        public int A;
        public long B;
    }

    /// <summary>
    /// Three members of the same total width, so two builds that drop a <em>different</em> one write the
    /// same number of bytes and a reader cannot notice the swap by the length. This is the shape the audit
    /// reproduced with two real builds, and it produced the worst outcome measured for this defect: a load
    /// that consumed every byte and returned a value nobody saved.
    /// </summary>
    public class EngineTriple
    {
        public int A;      // 4 bytes - written by the old build, dropped by the new one
        public int B;      // 4 bytes - in both
        public long C;     // 8 bytes - in both
        public int D;      // 4 bytes - added by the new one
    }

    /// <summary>The root of the same-width half.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class TripleRoot
    {
        public int Sentinel;
        public EngineTriple Triple;
    }

    /// <summary>
    /// A generated type with a tuple-typed member, which is <b>where the two sides' order used to
    /// diverge</b>: the generator sorted a member by the display string of its type
    /// (<c>"(int, int)Tup"</c>, which starts with a bracket) and the runtime by
    /// <see cref="Type.FullName"/> (<c>"System.ValueTuple`2[...]Tup"</c>, which starts with an S), so the
    /// generated writer put it first and the fallback put it after <c>System.Int32</c>.
    /// <para>
    /// Both sides now spell the sort key the same way — the canonical type identity
    /// (<c>System.ValueTuple`2&lt;System.Int32,System.Int32&gt;</c>, which starts with an S) — so this
    /// type is the parity case instead: same three members, same order on both sides, same bytes.
    /// <c>MemberOrderUnificationTests</c> pins that over a wider set of spellings; it is pinned here from
    /// the fallback's side, because this is the type the measurement below was taken on.
    /// </para>
    /// </summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class TupleRoot
    {
        public int A;
        public (int, int) Tup;
        public int B;
    }

    /// <summary>Two same-width members, so a build that drops one and a build that drops the other write
    /// the same number of bytes and a reader cannot notice the swap by the length.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class PairRoot
    {
        public int First;
        public int Second;
    }

    /// <summary>
    /// The order case, the only way it is still reachable: a type the manifest describes by hand, in an
    /// order the writer does not produce. <c>SchemaRegistry.Register</c> with an explicit
    /// <c>SchemaTypeInfo</c> is the documented forge for a historical file's shape, and it takes a field
    /// list in whatever order its author wrote — while the fallback's order is the one key's. A descriptor
    /// that lists the same members in the other order is refused here by name, which is what a generator
    /// cannot produce and therefore what the two writers no longer diverge over on their own.
    /// <para>
    /// Deliberately <em>not</em> in the <c>ReflectionLayout</c> context: a hand-written descriptor is a
    /// whole manifest entry, so it has to be the <em>only</em> one — a context's generated registration
    /// for the type is idempotent and would win.
    /// </para>
    /// </summary>
    public class HandDescribedOrder
    {
        public int First;
        public int Second;
    }

    /// <summary>
    /// The control. Every member is a shape the generated writer and the reflection fallback route
    /// through the *same* formatter, so a selection here produces exactly the bytes the generated writer
    /// would have produced — which is what makes "the described selection still saves" measurable as
    /// byte-identity rather than as a successful round trip.
    /// <para>
    /// Deliberately free of array members: an array member is the one shape whose reference marker the two
    /// writers disagree about, which is measured separately and is <em>not</em> what this control is for.
    /// </para>
    /// </summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class ControlRoot
    {
        public int Sentinel;
        public string Text;
        public Nested Nested;
        public List<int> Numbers;
    }

    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public struct Nested
    {
        public int A;
        public long B;
    }

    /// <summary>One array member: the marker measurement.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class ArrayRoot
    {
        public int Sentinel;
        public int[] Values;
    }

    /// <summary>An array of a nullable element type — still an array-typed member.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class NullableArrayRoot
    {
        public int Sentinel;
        public int?[] Values;
    }

    /// <summary>A jagged array: an array whose elements are arrays.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class JaggedArrayRoot
    {
        public int Sentinel;
        public int[][] Values;
    }

    /// <summary>An array of a closed generic — an array-typed member of a generic element type.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class GenericArrayRoot
    {
        public int Sentinel;
        public List<int>[] Values;
    }

    /// <summary>
    /// An array that is an <em>element</em> of a collection rather than a member. Not refused, and not by
    /// luck: both writers reach the array through the same runtime collection formatter, so there is no
    /// second route to disagree about.
    /// </summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class ListOfArrayRoot
    {
        public int Sentinel;
        public List<int[]> Values;
    }

    /// <summary>The same for a dictionary value.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class DictOfArrayRoot
    {
        public int Sentinel;
        public Dictionary<string, int[]> Map;
    }

    /// <summary>One collection member: the <c>IsMonomorphic</c> measurement.</summary>
    [Ceras.CerasContext("ReflectionLayout")]
    [Ceras.CerasSerializable]
    public class ListRoot
    {
        public int Sentinel;
        public List<int> Values;
    }
}

namespace Ceras.Test.Tests
{
    using Ceras.Test.ReflectionLayout;

    /// <summary>
    /// The reflection fallback's member list against the manifest's, in a robust context.
    /// <para>
    /// Every test that needs a member list to disagree forces the disagreement with
    /// <c>ConfigType&lt;T&gt;().CustomFormatter = null</c> (a generated type whose own formatter is taken
    /// away, so the fallback is the only thing left) or with a member the generator cannot see
    /// (<c>[CerasGeneratorBlacklist]</c>, <c>ConfigField(...).Exclude()</c>). Those are the two ways it
    /// happens in a real build, and neither is a corrupted manifest: both produce a file that is
    /// self-consistent, carries the fingerprint of the described layout, and decodes to a wrong value.
    /// </para>
    /// </summary>
    public class ReflectionFallbackLayoutTests : TestBase
    {
        const string ContextName = "ReflectionLayout";
        const int PayloadId = 0x2BADF00D;

        static CerasRobustSerializer NewRobust(Action<CerasRobustConfig> configure = null)
            => new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(ContextName);
                configure?.Invoke(c);
            });

        /// <summary>A robust serializer for <paramref name="T"/> written by the reflection fallback.</summary>
        static CerasRobustSerializer NewReflected<T>(Action<CerasRobustConfig> configure = null)
            => NewRobust(c =>
            {
                c.ConfigType<T>().CustomFormatter = null;
                configure?.Invoke(c);
            });

        /// <summary>
        /// A robust serializer whose only manifest entry is a hand-written one for
        /// <see cref="HandDescribedOrder"/>, with the reflection fallback as the writer for it. No generated
        /// context is registered: the entry is whole, and the generated registration for the type would be
        /// idempotent over it.
        /// </summary>
        static CerasRobustSerializer NewHandDescribed(SchemaFieldInfo[] fields)
            => new CerasRobustSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                c.SchemaRegistry.Register(typeof(HandDescribedOrder), new SchemaTypeInfo(
                    stableName: nameof(HandDescribedOrder),
                    previousTypeNames: Array.Empty<string>(),
                    kind: SchemaTypeKind.Class,
                    fields: fields,
                    fixedByteSize: 0));
            });

        static Root ClassModel(int sentinel = 7) => new Root { Sentinel = sentinel, Payload = new EngineObject { Id = PayloadId, Label = "tag" } };
        static StructRoot StructModel() => new StructRoot { Sentinel = 11, Payload = new EnginePart { A = 0x1111_1111, B = 0x2222_2222_3333_3333L } };
        static TripleRoot TripleModel() => new TripleRoot
        {
            Sentinel = 5,
            Triple = new EngineTriple { A = 0x0A0A_0A0A, B = 0x0B0B_0B0B, C = 0x0C0C_0C0C_0C0C_0C0CL, D = 0x0D0D_0D0D }
        };
        static ControlRoot ControlModel() => new ControlRoot { Sentinel = 5, Text = "still fine", Nested = new Nested { A = 6, B = 7 }, Numbers = new List<int> { 8, 9 } };
        static TupleRoot TupleModel() => new TupleRoot { A = 111, Tup = (222, 333), B = 444 };
        static ArrayRoot ArrayModel() => new ArrayRoot { Sentinel = 7, Values = new[] { 9, 10 } };
        static NullableArrayRoot NullableArrayModel() => new NullableArrayRoot { Sentinel = 7, Values = new int?[] { 9, null } };
        static JaggedArrayRoot JaggedArrayModel() => new JaggedArrayRoot { Sentinel = 7, Values = new[] { new[] { 9 }, new[] { 10 } } };
        static GenericArrayRoot GenericArrayModel() => new GenericArrayRoot { Sentinel = 7, Values = new[] { new List<int> { 9 } } };
        static ListOfArrayRoot ListOfArrayModel() => new ListOfArrayRoot { Sentinel = 7, Values = new List<int[]> { new[] { 9 } } };
        static DictOfArrayRoot DictOfArrayModel() => new DictOfArrayRoot { Sentinel = 7, Map = new Dictionary<string, int[]> { ["k"] = new[] { 9 } } };

        static string[] DescribedMemberNames(Type type)
        {
            Assert.True(NewRobust().Config.SchemaRegistry.TryGetInfo(type, out var info),
                $"the context's manifest holds no entry for '{type.Name}'");
            return info.Fields.Select(f => f.StableName).ToArray();
        }

        static string[] RuntimeMemberNames(CerasSerializer serializer, Type type)
            => serializer.GetTypeMetaData(type).PrimarySchema!.Members.Select(m => m.PersistentName).ToArray();

        /// <summary>The data section of a save, as hex: the layout the two writers disagree about.</summary>
        static string DataSection(byte[] save)
        {
            RobustHeader.Read(save, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            var bytes = new byte[stringOffset - dataOffset];
            Array.Copy(save, dataOffset, bytes, 0, bytes.Length);
            return Convert.ToHexString(bytes);
        }

        // ── the shape that makes the door reachable ─────────────────────────────────────────────

        /// <summary>
        /// Precondition for the blacklisted half: the generator emits no formatter for these two types
        /// and registers a member-less entry for each, so the manifest describes "no members" while the
        /// reflection fallback writes every one of them. Without this the tests below would be passing
        /// for the wrong reason — a generated type's formatter is installed as <c>CustomFormatter</c> and
        /// consulted first, so the fallback is never reached for it.
        /// </summary>
        [Fact]
        public void TheBlacklistedTypesGetAMemberlessPlaceholder()
        {
            var serializer = NewRobust();

            Assert.True(serializer.Config.SchemaRegistry.TryGetInfo(typeof(EngineObject), out var forClass));
            Assert.Equal(SchemaTypeKind.Class, forClass.Kind);
            Assert.Empty(forClass.Fields);

            // The struct half is a separate branch in the emitted registration (`isStruct` decides the
            // kind), and it is the value-type case that matters twice over: the payload is written
            // inline, with no reference marker to notice.
            Assert.True(serializer.Config.SchemaRegistry.TryGetInfo(typeof(EnginePart), out var forStruct));
            Assert.Equal(SchemaTypeKind.Struct, forStruct.Kind);
            Assert.Empty(forStruct.Fields);

            // …and the runtime sees members where the manifest sees none.
            Assert.Equal(new[] { "Id", "Label" }, RuntimeMemberNames(serializer, typeof(EngineObject)));
            Assert.Equal(new[] { "A", "B" }, RuntimeMemberNames(serializer, typeof(EnginePart)));

            // No formatter of the library's own was installed for either, which is what leaves the
            // resolver door open.
            Assert.Null(serializer.Config.GetExistingTypeConfig(typeof(EngineObject), false)?.CustomFormatter);
            Assert.Null(serializer.Config.GetExistingTypeConfig(typeof(EnginePart), false)?.CustomFormatter);
        }

        // ── refusals: the blacklisted placeholder ───────────────────────────────────────────────

        /// <summary>
        /// The class case. The generator registers a member-less <c>Class</c> entry for it, so the entry —
        /// and with it the layout fingerprint — says the same thing in every build that keeps the type
        /// out of generation, while the payload holds whatever members reflection finds. Measured on the
        /// unguarded build, in the same-width form pinned by
        /// <see cref="ABlacklistedTypeWhoseTwoBuildsDropDifferentSameWidthMembersIsRefusedOnBothSides"/>:
        /// a load that consumed every byte and returned a value nobody saved.
        /// <para>
        /// Now the selection is refused before the value has a byte, so there is no fingerprint left to
        /// compare: the refusal <em>is</em> the assertion. The control save in the same test shows the
        /// refusal is about the selection and nothing else.
        /// </para>
        /// </summary>
        [Fact]
        public void AReflectionFallbackOverABlacklistedClassPlaceholderIsRefusedOnSave()
        {
            var error = Assert.Throws<CerasSchemaException>(() => NewRobust().Save(ClassModel(), 1));

            // The type, the door, and the first member the two lists disagree about.
            Assert.Contains(nameof(EngineObject), error.Message, StringComparison.Ordinal);
            Assert.Contains("reflection fallback", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("describes 0 members", error.Message, StringComparison.Ordinal);
            Assert.Contains("member #0", error.Message, StringComparison.Ordinal);
            Assert.Contains("'Id'", error.Message, StringComparison.Ordinal);

            // The ways out, and the one that is not a way out.
            Assert.Contains("CustomFormatter", error.Message, StringComparison.Ordinal);
            Assert.Contains("CerasSerializable", error.Message, StringComparison.Ordinal);
            Assert.Contains("CerasFastSerializer", error.Message, StringComparison.Ordinal);

            // The rest of the context is untouched: a root whose types are all the context's own saves
            // and reads back on the very serializer that just refused.
            var control = new ControlRoot { Sentinel = 3, Text = "control", Nested = new Nested { A = 1, B = 2 }, Numbers = new List<int>() };
            var bytes = NewRobust().Save(control, 1);
            Assert.Equal(3, NewRobust().Load<ControlRoot>(bytes, 1).Sentinel);
        }

        /// <summary>
        /// The struct half of the same thing, which is a separate emitted branch and a separate payload
        /// shape: a value type is written inline, so the divergence is the *whole* payload rather than
        /// what follows a reference marker.
        /// </summary>
        [Fact]
        public void AReflectionFallbackOverABlacklistedStructPlaceholderIsRefusedOnSave()
        {
            var error = Assert.Throws<CerasSchemaException>(() => NewRobust().Save(StructModel(), 1));

            Assert.Contains(nameof(EnginePart), error.Message, StringComparison.Ordinal);
            Assert.Contains("reflection fallback", error.Message, StringComparison.OrdinalIgnoreCase);
            // A struct entry is a `Struct`, not a `Class`; the message has to say which kind it refused.
            Assert.Contains("Struct", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The same guard on the read side, and it is the sharper half: a load resolves formatters
        /// through the same line as a save, so a file whose payload is a member list is refused while it
        /// is being read — before the bytes are interpreted.
        /// <para>
        /// The file is a legitimate one: the writer excluded both members of the blacklisted type
        /// (<c>ConfigField(...).Exclude()</c>), so its reflected member list is empty and agrees with the
        /// member-less entry the manifest holds. The reader has no such exclusion, so its own reflected
        /// list has two members the manifest does not describe. The two contexts' fingerprints are
        /// asserted equal first, which is the whole reason a reader would not have noticed on its own.
        /// </para>
        /// </summary>
        [Fact]
        public void TheLoadPathRefusesTheSameSelectionAndTheTwoFingerprintsAreEqual()
        {
            byte[] written = NewRobust(c =>
            {
                c.ConfigType<EngineObject>().ConfigField(nameof(EngineObject.Id)).Exclude();
                c.ConfigType<EngineObject>().ConfigField(nameof(EngineObject.Label)).Exclude();
            }).Save(ClassModel(), 1);

            var reader = NewRobust();

            // A config that changes the *runtime* member list cannot change the manifest, so it cannot
            // change the fingerprint: this is the same file's layout claim on both sides.
            Assert.Equal(
                reader.Config.SchemaRegistry.ComputeFingerprint(),
                NewRobust(c =>
                {
                    c.ConfigType<EngineObject>().ConfigField(nameof(EngineObject.Id)).Exclude();
                    c.ConfigType<EngineObject>().ConfigField(nameof(EngineObject.Label)).Exclude();
                }).Config.SchemaRegistry.ComputeFingerprint());

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(written, 1));
            Assert.Contains(nameof(EngineObject), error.Message, StringComparison.Ordinal);
            Assert.Contains("reflection fallback", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── refusals: the same-width reorder across two builds ─────────────────────────────────

        /// <summary>
        /// The shape the audit reproduced with two real builds, and the worst outcome measured for this
        /// defect. A blacklisted type is described as "a class with no members" in both builds, so its
        /// manifest entry — and therefore the layout fingerprint — is identical; the only thing that differs
        /// is which member each build's reflection happens to find, and both write 16 bytes.
        /// <para>
        /// Measured on the unguarded build, with both contexts sealed: fingerprints equal
        /// (<c>827E843A71E6D170</c> on both sides), the old build wrote <c>{A,B,C}</c> and the new one read
        /// <c>{B,D,C}</c> out of it, the load <b>succeeded</b>, every byte of the data section was consumed,
        /// and the result was <c>A=00000000</c> (never written), <c>B=0A0A0A0A</c> (A's value),
        /// <c>D=0B0B0B0B</c> (B's value) and <c>C=0C0C0C0C0C0C0C0C</c> (right, by the accident that a 4-byte
        /// member sits in front of it). The model saved A=0A0A0A0A, B=0B0B0B0B, C=0C0C…, D=0D0D0D0D.
        /// </para>
        /// <para>
        /// Both selections are refused now, each naming the same first divergence, and there is no file for
        /// a reader to be fooled by.
        /// </para>
        /// </summary>
        [Fact]
        public void ABlacklistedTypeWhoseTwoBuildsDropDifferentSameWidthMembersIsRefusedOnBothSides()
        {
            var oldBuild = NewRobust(c => c.ConfigType<EngineTriple>().ConfigField(nameof(EngineTriple.D)).Exclude());
            var newBuild = NewRobust(c => c.ConfigType<EngineTriple>().ConfigField(nameof(EngineTriple.A)).Exclude());

            // The two member lists, as each build's reflection finds them: same count, same total width
            // (4+4+8 and 4+4+8), different members — which is the whole reason nothing noticed.
            Assert.Equal(new[] { "A", "B", "C" }, RuntimeMemberNames(oldBuild, typeof(EngineTriple)));
            Assert.Equal(new[] { "B", "D", "C" }, RuntimeMemberNames(newBuild, typeof(EngineTriple)));

            // …and the member-less placeholder the manifest holds for it in both.
            Assert.Empty(DescribedMemberNames(typeof(EngineTriple)));

            // Each refusal names the first member that build would write and the schema section has no
            // entry for — the divergence itself, not the member that was dropped.
            foreach (var (build, first) in new[] { (oldBuild, "A"), (newBuild, "B") })
            {
                var error = Assert.Throws<CerasSchemaException>(() => build.Save(TripleModel(), 1));
                Assert.Contains(nameof(EngineTriple), error.Message, StringComparison.Ordinal);
                Assert.Contains("describes 0 members", error.Message, StringComparison.Ordinal);
                Assert.Contains($"'{first}'", error.Message, StringComparison.Ordinal);
            }
        }

        // ── the order: parity for a generated type, a refusal for a hand-described one ───────────

        /// <summary>
        /// The tuple case, as it stands: the two member lists are <b>equal</b>, so the payload is, and the
        /// two writers write the same bytes.
        /// <para>
        /// <b>Reversed since it was written.</b> It used to assert the opposite and is the one test in this
        /// file whose expectation the member-order unification flipped: the generator sorted a member on the
        /// type's Roslyn display string (<c>"(int, int)Tup"</c>, below every letter) and the runtime on
        /// <see cref="Type.FullName"/> (<c>"System.ValueTuple`2[...]Tup"</c>, an S), so the two lists were
        /// <c>[Tup, A, B]</c> and <c>[A, B, Tup]</c>. Measured then, on the unguarded build: one
        /// fingerprint (8A5D65EABEA175AD), the generated data section 22222222 33333333 11111111 44444444
        /// and the reflected one 11111111 44444444 22222222 33333333, and the reflected reader of the
        /// generated file returned A=22222222 Tup=(11111111,44444444) B=33333333 for a model saved as
        /// A=11111111 Tup=(22222222,33333333) B=44444444 — a complete, aligned, unremarked misread.
        /// </para>
        /// <para>
        /// Both sides now spell the key the one way (canonical type identity,
        /// <c>System.ValueTuple`2&lt;System.Int32,System.Int32&gt;</c>), so <c>Tup</c> sorts after both
        /// ints on both sides and the divergence is gone at its source. The guard below no longer refuses
        /// anything here — which is the point: it refuses only where a described list and the written list
        /// really are two different payloads, and for a generated type they no longer can be.
        /// </para>
        /// </summary>
        [Fact]
        public void ATupleTypedMemberNowSortsTheSameOnBothSidesAndWritesTheSameBytes()
        {
            var described = DescribedMemberNames(typeof(TupleRoot));
            var runtime = RuntimeMemberNames(NewRobust(), typeof(TupleRoot));

            // 'A' and 'B' are both `System.Int32` and 'Tup' is `System.ValueTuple`2<…>`, an S, so the
            // tuple member is written last on both sides now — the concrete result, so a change to either
            // writer's key is a visible diff here too.
            Assert.Equal(new[] { "A", "B", "Tup" }, described);
            Assert.Equal(described, runtime);

            // …and the payload follows the list: byte for byte, not merely a successful round trip,
            // because "still round-trips" would also be true of a guard that quietly changed them.
            byte[] byGenerated = NewRobust().Save(TupleModel(), 1);
            byte[] byReflection = NewReflected<TupleRoot>().Save(TupleModel(), 1);
            Assert.Equal(DataSection(byGenerated), DataSection(byReflection));
            Assert.Equal(
                NewRobust().Config.SchemaRegistry.ComputeFingerprint(),
                NewReflected<TupleRoot>().Config.SchemaRegistry.ComputeFingerprint());

            var clone = NewReflected<TupleRoot>().Load<TupleRoot>(byGenerated, 1);
            Assert.Equal(111, clone.A);
            Assert.Equal((222, 333), clone.Tup);
            Assert.Equal(444, clone.B);
        }

        /// <summary>
        /// The order half of the guard, in the one form that is still reachable. The two writers now agree
        /// on the order by construction, so the generator cannot produce a described list in an order the
        /// writer will not use — but a hand-written <c>SchemaRegistry.Register</c> descriptor can, and that
        /// is the documented forge for a historical file's shape. Same members, same types, same total
        /// width, one fingerprint, and a payload in the wrong order: the exact shape the tuple case used to
        /// be, reached the only way it still can be.
        /// </summary>
        [Fact]
        public void AHandWrittenManifestEntryInAnotherOrderIsRefused()
        {
            var serializer = NewHandDescribed(
                fields: new[]
                {
                    new SchemaFieldInfo(nameof(HandDescribedOrder.Second), Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo(nameof(HandDescribedOrder.First), Array.Empty<string>(), "System.Int32", true),
                });

            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(new HandDescribedOrder { First = 1, Second = 2 }, 1));

            // The first position the two lists disagree about, and both members named on it.
            Assert.Contains(nameof(HandDescribedOrder), error.Message, StringComparison.Ordinal);
            Assert.Contains("'Second'", error.Message, StringComparison.Ordinal);
            Assert.Contains("'First'", error.Message, StringComparison.Ordinal);
            Assert.Contains("member #0", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The same hand-written descriptor with the order the writer actually uses, which is what makes
        /// the refusal above about the order and not about a hand-written manifest being unwelcome. The
        /// selection is accepted, the two files carry the same fingerprint, and either writer's bytes are
        /// read by either reader.
        /// </summary>
        [Fact]
        public void AHandWrittenManifestEntryInTheWritersOrderIsAccepted()
        {
            var serializer = NewHandDescribed(
                fields: new[]
                {
                    new SchemaFieldInfo(nameof(HandDescribedOrder.First), Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo(nameof(HandDescribedOrder.Second), Array.Empty<string>(), "System.Int32", true),
                });

            var model = new HandDescribedOrder { First = 111, Second = 222 };
            byte[] written = serializer.Save(model, 1);

            Assert.Equal(111, serializer.Load<HandDescribedOrder>(written, 1).First);
            Assert.Equal(222, serializer.Load<HandDescribedOrder>(written, 1).Second);
        }

        // ── refusals: a described list of a different length ───────────────────────────────────

        /// <summary>
        /// The count case on a generated type: a runtime exclusion is invisible to the generator, so the
        /// manifest keeps a member the payload does not contain. Two builds that exclude <em>different</em>
        /// same-width members write the same number of bytes, and a reader cannot notice the swap by the
        /// length — measured on the unguarded build, where the fingerprint was equal and the load failed
        /// only on the trailing-byte check ("4 byte(s) of the data section were never read"), which is
        /// loud by the accident of where the dropped member sat rather than by anything that noticed the
        /// layout.
        /// </summary>
        [Fact]
        public void ARuntimeExclusionTheGeneratorCannotSeeIsRefusedAndNamesTheFirstDivergentMember()
        {
            Assert.Equal(new[] { "First", "Second" }, DescribedMemberNames(typeof(PairRoot)));

            var serializer = NewReflected<PairRoot>(c => c.ConfigType<PairRoot>().ConfigField(nameof(PairRoot.Second)).Exclude());

            var error = Assert.Throws<CerasSchemaException>(() => serializer.Save(new PairRoot { First = 0x1111_1111, Second = 0x2222_2222 }, 1));

            // 'First' agrees, so the divergence is reported at the first position that does not: index 1.
            Assert.Contains(nameof(PairRoot), error.Message, StringComparison.Ordinal);
            Assert.Contains("member #1", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PairRoot.Second), error.Message, StringComparison.Ordinal);

            // The swap, had it been written: same width either way, and the same fingerprint.
            Assert.Equal(
                NewRobust().Config.SchemaRegistry.ComputeFingerprint(),
                NewReflected<PairRoot>(c => c.ConfigType<PairRoot>().ConfigField(nameof(PairRoot.First)).Exclude())
                    .Config.SchemaRegistry.ComputeFingerprint());
        }

        /// <summary>
        /// The same exclusion seen from the other end: a file written by a build whose runtime list
        /// <em>does</em> agree with the manifest (so it is a legitimate file, accepted by the guard) is
        /// refused by a reader whose own runtime list is one member shorter. This is the load-path twin of
        /// the count case, and it is what makes the refusal observable on a read at all: a file the guard
        /// would have refused to write is a file this build cannot produce.
        /// </summary>
        [Fact]
        public void AFileWrittenByAnAgreeingBuildIsRefusedByAReaderWithOneMemberFewer()
        {
            byte[] written = NewReflected<PairRoot>().Save(new PairRoot { First = 0x1111_1111, Second = 0x2222_2222 }, 1);

            var reader = NewReflected<PairRoot>(c => c.ConfigType<PairRoot>().ConfigField(nameof(PairRoot.Second)).Exclude());

            var error = Assert.Throws<CerasSchemaException>(() => reader.Load<PairRoot>(written, 1));
            Assert.Contains(nameof(PairRoot), error.Message, StringComparison.Ordinal);
            Assert.Contains("member #1", error.Message, StringComparison.Ordinal);
        }

        // ── what must keep working ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The case that was working, and the reason the guard is worth having: a described selection —
        /// the reflection fallback writing exactly the member list the manifest describes — is accepted,
        /// and it writes <b>byte-identical</b> output to the generated writer it replaced. The bytes are
        /// pinned, not just the round trip, because "still round-trips" would also be true of a guard
        /// that quietly changed them.
        /// </summary>
        [Fact]
        public void ADescribedReflectionSelectionStillSavesAndWritesTheGeneratedWritersBytes()
        {
            var generated = NewRobust();
            var reflected = NewReflected<ControlRoot>();

            // The precondition, stated rather than assumed: the two member lists are identical, which is
            // why this selection is described and the ones above are not.
            Assert.Equal(DescribedMemberNames(typeof(ControlRoot)), RuntimeMemberNames(generated, typeof(ControlRoot)));

            byte[] byGenerated = generated.Save(ControlModel(), 1);
            byte[] byReflection = reflected.Save(ControlModel(), 1);

            Assert.Equal(DataSection(byGenerated), DataSection(byReflection));
            Assert.Equal(
                generated.RobustCompatibility.LayoutFingerprint,
                reflected.RobustCompatibility.LayoutFingerprint);

            var clone = NewReflected<ControlRoot>().Load<ControlRoot>(byReflection, 1);
            Assert.Equal(5, clone.Sentinel);
            Assert.Equal("still fine", clone.Text);
            Assert.Equal(6, clone.Nested.A);
            Assert.Equal(7L, clone.Nested.B);
            Assert.Equal(new[] { 8, 9 }, clone.Numbers);
        }

        /// <summary>
        /// The zero-change guarantee. A raw/transient context describes nothing, so the fallback's bytes
        /// there are exactly what they always have been; the check is behind <c>Config is
        /// CerasRobustConfig</c>, so a raw serializer reaches the identical line with the identical
        /// formatter. Pinned as exact bytes (measured on this build before the guard existed, and
        /// unchanged by it) rather than as a round trip.
        /// </summary>
        [Fact]
        public void ARawContextIsUntouchedAndItsBytesAreUnchanged()
        {
            var raw = new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                c.UseGeneratedFormatters(ContextName);
                c.ConfigType<ControlRoot>().CustomFormatter = null;
            });
            var generated = new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                c.UseGeneratedFormatters(ContextName);
            });

            byte[] bytes = raw.Serialize(ControlModel());

            // The generated raw writer and the reflected one agree byte for byte, for the same reason the
            // robust control does: same member list, and every member is a shape the two route through the
            // same formatter. Pinned as exact bytes (measured before the guard existed) so a change in
            // either writer is visible, not just a successful round trip.
            Assert.Equal(Convert.ToHexString(generated.Serialize(ControlModel())), Convert.ToHexString(bytes));
            // 02 | Nested.A=6 | Nested.B=7 | 02 (list marker) | 03 (count) | 8 | 9 | Sentinel=5 | 0B "still fine"
            Assert.Equal("0206000000070000000000000002030800000009000000050000000B7374696C6C2066696E65",
                Convert.ToHexString(bytes));

            var clone = raw.Deserialize<ControlRoot>(bytes);
            Assert.Equal(5, clone.Sentinel);
            Assert.Equal("still fine", clone.Text);
            Assert.Equal(new[] { 8, 9 }, clone.Numbers);
        }

        // ── a refused selection must leave nothing cached ──────────────────────────────────────

        /// <summary>
        /// The refusal has to leave the serializer exactly as it found it. A resolved formatter is cached
        /// on the type's metadata and every later lookup returns that cache without consulting anything,
        /// so a refusal raised after the cache was written would leave the refused formatter sitting in
        /// it: the second save would take the cache, skip the guard, and write the payload the first one
        /// refused. The guard is asked before the fallback is even constructed, so there is nothing to
        /// cache.
        /// </summary>
        [Fact]
        public void ARefusedSaveLeavesNothingCachedSoASecondSaveIsRefusedToo()
        {
            var serializer = NewRobust();

            var first = Assert.Throws<CerasSchemaException>(() => serializer.Save(ClassModel(), 1));
            var second = Assert.Throws<CerasSchemaException>(() => serializer.Save(ClassModel(), 1));

            Assert.Equal(first.Message, second.Message);
        }

        [Fact]
        public void ARefusedLoadLeavesNothingCachedSoASecondLoadIsRefusedToo()
        {
            byte[] written = NewRobust(c =>
            {
                c.ConfigType<EngineObject>().ConfigField(nameof(EngineObject.Id)).Exclude();
                c.ConfigType<EngineObject>().ConfigField(nameof(EngineObject.Label)).Exclude();
            }).Save(ClassModel(), 1);

            var reader = NewRobust();

            var first = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(written, 1));
            var second = Assert.Throws<CerasSchemaException>(() => reader.Load<Root>(written, 1));

            Assert.Equal(first.Message, second.Message);
        }

        // ── what the guard does NOT cover, measured ────────────────────────────────────────────

        /// <summary>
        /// The <c>IsMonomorphic</c> flag: the two sides compute it differently, and the difference is
        /// measured to be invisible in the bytes.
        /// <para>
        /// The generator (<c>TypeHelpers.IsMonomorphicByDefault</c>) recurses into an array's element type
        /// and into a generic's element/value argument, so <c>List&lt;int&gt;</c> is monomorphic to it. The
        /// runtime (<c>SchemaMember.IsMonomorphic</c>) is <c>IsValueType || IsSealed</c>, so
        /// <c>List&lt;int&gt;</c> is not. The flag is hashed into the layout fingerprint and written into
        /// the schema section, so this is a real disagreement between what the file claims and what the
        /// build computes — and yet the payload is byte-identical, because a collection member is routed
        /// through a reference formatter on both paths and the per-element type id is decided inside the
        /// shared collection formatter.
        /// </para>
        /// <para>
        /// Pinned so the two facts cannot drift apart: if a future change makes the flag reachable in the
        /// bytes, the byte assertion here is what fails.
        /// </para>
        /// </summary>
        [Fact]
        public void TheMonomorphicFlagDiffersForCollectionsButTheBytesDoNot()
        {
            var generated = NewRobust();
            Assert.True(generated.Config.SchemaRegistry.TryGetInfo(typeof(ListRoot), out var info));

            var described = info.Fields.Single(f => f.StableName == nameof(ListRoot.Values));
            var runtime = generated.GetTypeMetaData(typeof(ListRoot)).PrimarySchema!.Members.Single(m => m.PersistentName == nameof(ListRoot.Values));

            // The disagreement, on both sides' own terms.
            Assert.True(described.IsMonomorphic);
            Assert.False(runtime.IsMonomorphic);

            // …and no byte of the payload moves, because of it.
            byte[] byGenerated = generated.Save(new ListRoot { Sentinel = 7, Values = new List<int> { 9, 10 } }, 1);
            byte[] byReflection = NewReflected<ListRoot>().Save(new ListRoot { Sentinel = 7, Values = new List<int> { 9, 10 } }, 1);

            Assert.Equal(DataSection(byGenerated), DataSection(byReflection));
        }
        /// <summary>
        /// <b>The defect, measured, and the part of it a member list cannot see.</b> Every member of
        /// <see cref="ArrayRoot"/> is described and written correctly — same count, same names, same types,
        /// same order, and one layout fingerprint — and the payload is still not the same, because a
        /// member's own reference marker is in no member list.
        /// <para>
        /// The generated writer binds <c>ArrayFormatter&lt;int&gt;</c> for that member, which writes the
        /// length and the elements; the reflection fallback binds
        /// <c>ReferenceFormatter_KnownSealedType&lt;int[]&gt;</c> (its rule is "a specific formatter only for
        /// a <em>value</em> type", and an array is a sealed reference type), which writes one reference
        /// marker in front of exactly those bytes.
        /// </para>
        /// <para>
        /// Measured on a <b>raw</b> context, which is the only place the two writers can be run side by side
        /// over one model at all (a robust context refuses the selection, which is what the next two tests
        /// pin) and the right place to measure it, because the divergence belongs to the two formatter
        /// classes rather than to the envelope: raw bytes carry no type ids, so the two strings below are
        /// stable. It is also the control that the raw path is unchanged by any of this — the same
        /// <c>SerializeRaw</c> helper with the fallback selected writes the marker-bearing bytes it always
        /// did.
        /// </para>
        /// </summary>
        [Fact]
        public void TheTwoWritersPutDifferentBytesInFrontOfAnArrayMemberWithEqualMemberLists()
        {
            // Equal on all three counts, and one fingerprint: that is the whole claim the file makes about
            // its layout, and it is not enough to describe the payload.
            Assert.Equal(DescribedMemberNames(typeof(ArrayRoot)), RuntimeMemberNames(NewRobust(), typeof(ArrayRoot)));
            Assert.Equal(
                NewRobust().Config.SchemaRegistry.ComputeFingerprint(),
                NewReflected<ArrayRoot>().Config.SchemaRegistry.ComputeFingerprint());

            byte[] byGenerated = SerializeRaw(ArrayModel());
            byte[] byReflection = SerializeRaw(ArrayModel(), useReflectionFallback: true);

            // 02 (root) | Sentinel=7 | length 3 | 9 | 10 — no marker in front of the array
            Assert.Equal("020700000003090000000A000000", Convert.ToHexString(byGenerated));
            //                                           — one extra 02, the reference marker
            Assert.Equal("02070000000203090000000A000000", Convert.ToHexString(byReflection));
            Assert.Equal(byGenerated.Length + 1, byReflection.Length);
        }

        /// <summary>
        /// The guard for the above: refused on the Save path, on the Load path, and again on both — a
        /// refusal has to leave nothing behind. A resolved formatter is cached on the type's metadata and
        /// every later lookup returns that cache without consulting anything, so a guard asked after the
        /// cache was written would let the second save through and write the payload the first one refused.
        /// This one is asked before the fallback is even constructed, so there is nothing to cache.
        /// </summary>
        [Fact]
        public void AnArrayTypedMemberIsRefusedOnSaveOnLoadAndAgainAfterwards()
        {
            var writer = NewReflected<ArrayRoot>();

            var first = Assert.Throws<CerasSchemaException>(() => writer.Save(ArrayModel(), 1));
            var second = Assert.Throws<CerasSchemaException>(() => writer.Save(ArrayModel(), 1));
            Assert.Equal(first.Message, second.Message);

            // The member, the two formatter classes the two writers bind for it, the measured bytes, and
            // the way out — including the member named in a form a caller can paste.
            Assert.Contains(nameof(ArrayRoot), first.Message, StringComparison.Ordinal);
            Assert.Contains("'" + nameof(ArrayRoot.Values) + "'", first.Message, StringComparison.Ordinal);
            Assert.Contains("ArrayFormatter", first.Message, StringComparison.Ordinal);
            Assert.Contains("ReferenceFormatter_KnownSealedType", first.Message, StringComparison.Ordinal);
            Assert.Contains("02070000000203090000000A000000", first.Message, StringComparison.Ordinal);
            Assert.Contains("ConfigField(\"" + nameof(ArrayRoot.Values) + "\")", first.Message, StringComparison.Ordinal);

            // The load path is the same line, and the file it reads is a legitimate one: written by the
            // generated writer, whose payload is what the manifest describes.
            byte[] written = NewRobust().Save(ArrayModel(), 1);
            var reader = NewReflected<ArrayRoot>();

            var read = Assert.Throws<CerasSchemaException>(() => reader.Load<ArrayRoot>(written, 1));
            Assert.Equal(first.Message, read.Message);
            Assert.Throws<CerasSchemaException>(() => reader.Load<ArrayRoot>(written, 1));
        }

        /// <summary>
        /// The rule is the shape, not the spelling: whatever an array is made of, a member declared as an
        /// array type is refused. <c>int[]</c>, <c>int?[]</c> (a nullable element), <c>int[][]</c> (jagged)
        /// and <c>List&lt;int&gt;[]</c> (a closed generic element) are four different CLR types and one rule,
        /// and all four reach the same two formatter classes.
        /// <para>
        /// The refusal message is asserted for all four, so that "refused" means "refused for the marker"
        /// rather than "refused for something". <c>int?[]</c> used to be left out of that assertion,
        /// because it was also one of the shapes whose two sides <em>ordered</em> the members differently
        /// (the generator sorted on the display string <c>"int?[]Values"</c>, the runtime on
        /// <c>System.Nullable`1[…][]Values</c>), so the member-list check answered it first. The two keys
        /// are now one key, so this shape's member lists agree like the rest and the marker check is what
        /// answers — which is the point of the rule, since it is a rule about <em>array</em> members and
        /// not about the ones whose ordering happened to differ.
        /// </para>
        /// </summary>
        [Fact]
        public void AnyArrayTypedMemberIsRefusedHoweverItIsSpelled()
        {
            // Refused, whatever the element type is.
            Assert.Throws<CerasSchemaException>(() => NewReflected<ArrayRoot>().Save(ArrayModel(), 1));
            Assert.Throws<CerasSchemaException>(() => NewReflected<NullableArrayRoot>().Save(NullableArrayModel(), 1));
            Assert.Throws<CerasSchemaException>(() => NewReflected<JaggedArrayRoot>().Save(JaggedArrayModel(), 1));
            Assert.Throws<CerasSchemaException>(() => NewReflected<GenericArrayRoot>().Save(GenericArrayModel(), 1));

            // …and refused for the marker in all four cases: the named member, both formatter classes, and
            // the marker named as the difference.
            AssertMarkerRefusal(() => NewReflected<ArrayRoot>().Save(ArrayModel(), 1), nameof(ArrayRoot.Values));
            AssertMarkerRefusal(() => NewReflected<NullableArrayRoot>().Save(NullableArrayModel(), 1), nameof(NullableArrayRoot.Values));
            AssertMarkerRefusal(() => NewReflected<JaggedArrayRoot>().Save(JaggedArrayModel(), 1), nameof(JaggedArrayRoot.Values));
            AssertMarkerRefusal(() => NewReflected<GenericArrayRoot>().Save(GenericArrayModel(), 1), nameof(GenericArrayRoot.Values));
        }

        /// <summary>
        /// The other side of the boundary, so the refusal cannot creep: an array that is an <em>element</em>
        /// of a collection is not a marker problem and is not refused. Both writers reach it through the
        /// same runtime collection formatter, which asks for the element's reference formatter on both
        /// sides, so there is no second route to disagree about. Measured on the raw path, where both
        /// writers can run; asserted on the robust path, where the selection is accepted, round-trips and
        /// writes the generated writer's bytes.
        /// </summary>
        [Fact]
        public void AnArrayNestedInsideACollectionIsNotRefusedAndWritesTheSameBytes()
        {
            Assert.Equal(Convert.ToHexString(SerializeRaw(ListOfArrayModel())),
                         Convert.ToHexString(SerializeRaw(ListOfArrayModel(), useReflectionFallback: true)));
            Assert.Equal(Convert.ToHexString(SerializeRaw(DictOfArrayModel())),
                         Convert.ToHexString(SerializeRaw(DictOfArrayModel(), useReflectionFallback: true)));

            // The precondition, stated: no member of an array type, which is why nothing is refused.
            Assert.Equal(DescribedMemberNames(typeof(ListOfArrayRoot)), RuntimeMemberNames(NewRobust(), typeof(ListOfArrayRoot)));
            Assert.Equal(DescribedMemberNames(typeof(DictOfArrayRoot)), RuntimeMemberNames(NewRobust(), typeof(DictOfArrayRoot)));

            var listBytes = NewReflected<ListOfArrayRoot>().Save(ListOfArrayModel(), 1);
            Assert.Equal(DataSection(NewRobust().Save(ListOfArrayModel(), 1)), DataSection(listBytes));
            var listClone = NewReflected<ListOfArrayRoot>().Load<ListOfArrayRoot>(listBytes, 1);
            Assert.Equal(new[] { 9 }, listClone.Values[0]);

            var dictBytes = NewReflected<DictOfArrayRoot>().Save(DictOfArrayModel(), 1);
            var dictClone = NewReflected<DictOfArrayRoot>().Load<DictOfArrayRoot>(dictBytes, 1);
            Assert.Equal(new[] { 9 }, dictClone.Map["k"]);
        }

        static void AssertMarkerRefusal(Action save, string member)
        {
            var error = Assert.Throws<CerasSchemaException>(save);

            Assert.Contains(member, error.Message, StringComparison.Ordinal);
            Assert.Contains("ArrayFormatter", error.Message, StringComparison.Ordinal);
            Assert.Contains("ReferenceFormatter_KnownSealedType", error.Message, StringComparison.Ordinal);
            // The member-list check did not answer this one: it reports a *member list*, and a marker is
            // in no member list, so its wording is the marker wording and not that one.
            Assert.Contains("reference marker", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A raw save with the reinterpret blitter off, so both writers take the same route and the only
        /// difference is the one under test. <paramref name="useReflectionFallback"/> takes the generated
        /// formatter away from <typeparamref name="T"/> so the fallback is the only thing left.
        /// </summary>
        static byte[] SerializeRaw<T>(T model, bool useReflectionFallback = false)
        {
            var raw = new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = useReflectionFallback;
                ((CerasRawConfig)c).UseReinterpretFormatter = false;
                c.UseGeneratedFormatters(ContextName);
                if (useReflectionFallback) c.ConfigType<T>().CustomFormatter = null;
            });

            return raw.Serialize(model);
        }
    }
}
