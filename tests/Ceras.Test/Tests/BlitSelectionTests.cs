// Which types the raw (transient) path is allowed to write as a copy of their memory image.
//
// `ReinterpretFormatter<T>` writes `Unsafe.SizeOf<T>()` bytes of CLR memory: field order and padding
// included. It used to claim *any* blittable type, and a plain `struct S { int One; int Two; int Three; }`
// is blittable, so the raw path wrote One, Two, Three while the generated formatter and the reflection
// fallback both wrote One, Three, Two — Ceras orders a type's members by the member's *type* name first,
// so same-typed members fall through to the name alone. Nothing threw and every save still round-tripped,
// because each of the two encodings is self-consistent. The only symptom was that a transient buffer
// stopped being readable at the moment the type gained a `[CerasSerializable]` attribute and a generated
// formatter took over.
//
// The fix is in the selection, not in the order. `ReflectionHelper.IsBlittableType` now also requires that
// the memory image already *be* the member-by-member encoding — the same members, in the same order, at
// the same widths, with no padding — with `Guid` as the one documented exemption. A struct declaring a
// `fixed` buffer used to be the second exemption and is not one any more: it is refused, on every path, by
// `Formatters/FixedBufferGuard`, because the member-by-member encoding it was excused from disagreeing
// with exists, and it is shorter than the image by the buffer's declared length (5 bytes written for a
// 32-byte value, the rest loading back as zeros with the save succeeding). `FixedBufferStructTests` covers
// that shape; what is pinned here is the rule around it:
//   * the reproducer: one value, one set of bytes, through the generated formatter, through the raw path's
//     reflection fallback, and through the member order a robust context derives from the type itself;
//   * that the raw path really stopped claiming the type, by formatter identity and not only by equal bytes;
//   * the padded case, which differs in *width* and not merely in order;
//   * that a type which is provably identical is still blitted, still round-trips, and still writes its
//     memory image byte for byte.
//
// The two test families that already existed could not see any of this. The cross-mode byte-identity
// tests (generated vs. reflection fallback) compare the two *sorted* paths, which run the same sort key
// and therefore agree by construction — they say nothing about the blit, which neither of them uses. The
// tests that do reach the blit (`Blitting.cs`) compare it against the *memory image*, never against a
// sorted encoding — and every struct they blit already has its members in alphabetical order
// (`Vector3.X/Y/Z`, `Half2.X/Y`, `KeyValuePair.key/value`), so it qualifies under the
// new rule and its assertions did not change. A type can only expose a blit/sorted disagreement if its
// member order differs from its layout order, which takes two members of the same type: the sort key
// starts with the member's type name, so one member per type is decided by that prefix and lands in
// declaration order by luck. No model in this suite had same-typed members named out of order. The one
// struct with mixed member types, `BigStruct`, has padding and fixed buffers, so it never
// served as an order comparison either — and it is no longer blitted at all, for the second of those
// reasons: a `fixed` buffer is refused rather than exempted, which is `Blitting`'s and
// `FixedBufferStructTests`' business and not this file's.
using System;
using System.Buffers;
using System.Linq;
using System.Runtime.InteropServices;
using Ceras;
using Ceras.Formatters;
using Ceras.Generated;
using Ceras.Helpers;
using Ceras.Test.Tests.FixedBuffer;
using Xunit;

namespace Ceras.Test.Tests.BlitSelection
{
    public class BlitSelectionTests : TestBase
    {
        // ── The three writers under comparison ─────────────────────────────────────────────────────

        /// <summary>Raw path, no generated formatter: the only member-by-member order available here is the reflected one.</summary>
        static CerasFastSerializer Raw_Reflected() => new CerasFastSerializer(c => c.AllowReflectionFallback = true);

        /// <summary>Raw path, generated formatter registered — the configuration a type gets once it is marked [CerasSerializable].</summary>
        static CerasFastSerializer Raw_Generated() => new CerasFastSerializer(c =>
        {
            c.AllowReflectionFallback = true;
            c.UseGeneratedFormatters(Context);
        });

        /// <summary>
        /// A robust context, with the type's custom formatter cleared. Clearing it *after* the generated
        /// registration deliberately leaves this context on the reflected member order, so the reference the
        /// raw paths are compared against is derived from the type alone and not from generated code.
        /// </summary>
        static CerasRobustSerializer Robust_Reflected<T>() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Context);
            c.ConfigType<T>().CustomFormatter = null;
        });

        /// <summary>The data section of a save, minus the leading root type id: the root's own payload.</summary>
        static byte[] PayloadOf<T>(CerasRobustSerializer ceras, T value)
        {
            byte[] save = ceras.Save(value, 1);
            RobustHeader.Read(save, out _, out _, out _, out long dataSectionOffset, out long stringTableOffset, out _);
            var payload = new ReadOnlySequence<byte>(save.AsSpan((int)dataSectionOffset, (int)(stringTableOffset - dataSectionOffset)).ToArray());
            var reader = new SequenceReader<byte>(payload);
            Format.SchemaSection.ReadLeb128(ref reader); // the root's type id
            return reader.UnreadSpan.ToArray();
        }

        /// <summary>What <see cref="ReinterpretFormatter{T}"/> would write: the value's memory image.</summary>
        static byte[] MemoryImageOf<T>(T value) where T : struct
            => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();

        // ── The reproducer ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_Blittable_Struct_With_Same_Typed_Members_Encodes_The_Same_Bytes_In_Every_Mode()
        {
            var value = new SameTypedTriple { One = 1, Two = 2, Three = 3 };

            // The member order Ceras documents and both member-by-member paths use: One, Three, Two. Not
            // the declaration order, which is the only thing a blit could have written.
            var expected = Ints(1, 3, 2);

            var generated = Raw_Generated();
            var reflected = Raw_Reflected();
            var robust = Robust_Reflected<SameTypedTriple>();

            Assert.Equal(expected, generated.Serialize(value));
            Assert.Equal(expected, reflected.Serialize(value));
            Assert.Equal(expected, PayloadOf(robust, value));

            // Byte identity is not enough on its own: three writers can agree and all three be wrong, and
            // the claim is that they agree because none of them is a blit any more. So the formatters are
            // named, not just the bytes.
            Assert.Same(generated.Config.ConfigType<SameTypedTriple>().CustomFormatter,
                generated.GetSpecificFormatter(typeof(SameTypedTriple)));
            Assert.IsType<ReflectionFallbackFormatter<SameTypedTriple>>(reflected.GetSpecificFormatter(typeof(SameTypedTriple)));
            Assert.IsType<ReflectionFallbackFormatter<SameTypedTriple>>(robust.GetSpecificFormatter(typeof(SameTypedTriple)));

            // … and all three still read what they wrote.
            Assert.Equal(value, generated.Deserialize<SameTypedTriple>(generated.Serialize(value)));
            Assert.Equal(value, reflected.Deserialize<SameTypedTriple>(reflected.Serialize(value)));
            var saved = robust.Save(value, 1);
            Assert.Equal(value, robust.Load<SameTypedTriple>(saved, 1));
        }

        // ── The padded case: a different width, not just a different order ──────────────────────────

        [Fact]
        public void A_Padded_Struct_Is_Not_Blitted_Because_Its_Width_Differs()
        {
            var value = new PaddedPair { B = 0x11, I = 0x22334455 };

            // Declaration order and member order agree here (System.Byte sorts before System.Int32), so
            // only the padding separates the two encodings: 8 bytes of memory for 5 bytes of payload.
            var expected = new byte[] { 0x11, 0x55, 0x44, 0x33, 0x22 };
            Assert.Equal(8, Marshal.SizeOf(typeof(PaddedPair)));  // what a blit would have written
            Assert.Equal(5, expected.Length);                    // what the members write
            Assert.Equal(8, MemoryImageOf(value).Length);

            var reflected = Raw_Reflected();
            Assert.IsType<ReflectionFallbackFormatter<PaddedPair>>(reflected.GetSpecificFormatter(typeof(PaddedPair)));
            Assert.Equal(expected, reflected.Serialize(value));
            Assert.Equal(expected, Raw_Generated().Serialize(value));
            Assert.Equal(value, reflected.Deserialize<PaddedPair>(reflected.Serialize(value)));
        }

        // ── The fast path that is unambiguous is kept ──────────────────────────────────────────────

        [Fact]
        public void A_Struct_Whose_Members_Already_Sort_Into_Layout_Order_Is_Still_Blitted()
        {
            var value = new OrderedPair { A = 1, B = 2 };

            var raw = Raw_Reflected();
            // The fast path is retained, and it is retained for the reason it claims: the memory image and
            // the member encoding are the same bytes, so the blit is not a second encoding here.
            Assert.IsType<ReinterpretFormatter<OrderedPair>>(raw.GetSpecificFormatter(typeof(OrderedPair)));

            byte[] bytes = raw.Serialize(value);
            Assert.Equal(MemoryImageOf(value), bytes);
            Assert.Equal(raw.Serialize(value), Raw_Generated().Serialize(value));
            Assert.Equal(raw.Serialize(value), PayloadOf(Robust_Reflected<OrderedPair>(), value));
            Assert.Equal(value, raw.Deserialize<OrderedPair>(bytes));
        }

        [Fact]
        public void A_Guid_Is_Still_Blitted_Because_Its_Encoding_Is_Its_Memory_Image()
        {
            // The documented exemption, pinned because it is the one place where a composite struct's
            // members do NOT sort into layout order (Guid's eleven private fields sort _d.._k, _b, _c, _a)
            // and the blit is still correct: SerializerBinary.WriteGuid copies the same 16 bytes a member of
            // type Guid is written with. Losing this would change no bytes, only the formatter, so the test
            // asks for the formatter.
            var raw = Raw_Reflected();
            Assert.IsType<ReinterpretFormatter<Guid>>(raw.GetSpecificFormatter(typeof(Guid)));

            var withGuid = new GuidAndCount { Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Count = 7 };
            Assert.IsType<ReinterpretFormatter<GuidAndCount>>(raw.GetSpecificFormatter(typeof(GuidAndCount)));
            Assert.Equal(MemoryImageOf(withGuid), raw.Serialize(withGuid));
            Assert.Equal(raw.Serialize(withGuid), Raw_Generated().Serialize(withGuid));
            Assert.Equal(withGuid, raw.Deserialize<GuidAndCount>(raw.Serialize(withGuid)));
        }

        [Fact]
        public void The_Width_Rule_Is_About_Padding_Not_About_Alignment()
        {
            // The same shape as the padded struct, packed so that it genuinely has no padding. The blit is
            // then the same five bytes the members write, so it stays — the rule refuses padding, not
            // narrow fields.
            var value = new PackedPair { B = 0x11, I = 0x22334455 };
            Assert.Equal(5, Marshal.SizeOf(typeof(PackedPair)));

            var raw = Raw_Reflected();
            Assert.IsType<ReinterpretFormatter<PackedPair>>(raw.GetSpecificFormatter(typeof(PackedPair)));
            Assert.Equal(MemoryImageOf(value), raw.Serialize(value));
            Assert.Equal(raw.Serialize(value), Raw_Generated().Serialize(value));
            Assert.Equal(value, raw.Deserialize<PackedPair>(raw.Serialize(value)));
        }

        [Fact]
        public void A_Struct_Declaring_A_Fixed_Buffer_Is_Not_One_Of_The_Exemptions_Any_More()
        {
            // The exemption this rule used to carry, pinned as gone. The old comment claimed a `fixed`
            // buffer "has no member encoding to disagree with at all"; it has one, and it is shorter than
            // the image by the buffer's declared length, so the blit was the only lossless encoding only
            // because the competing one was broken. Declining the blit is what routes the value to the
            // refusal instead, so no path is lossless while another is not.
            //
            // The refusal itself, and the numbers behind it, are FixedBufferStructTests' business; what
            // belongs to *this* rule is only the answer it gives.
            Assert.False(ReflectionHelper.IsBlittableType(typeof(Tests.FixedBuffer.FixedBufferStruct)));
            Assert.False(ReflectionHelper.IsBlittableType(typeof(Tests.FixedBuffer.PackedFixedBufferStruct)));
            Assert.False(ReflectionHelper.IsBlittableType(typeof(Tests.FixedBuffer.ContainsFixedBufferStruct)));

            // The exemption that is left is untouched, so this is a change to one shape and not to the
            // rule: Guid is still blittable, and a struct that merely contains one still is.
            Assert.True(ReflectionHelper.IsBlittableType(typeof(Guid)));
            Assert.True(ReflectionHelper.IsBlittableType(typeof(GuidAndCount)));
        }

        [Fact]
        public void A_Member_The_Payload_Leaves_Out_Disqualifies_The_Whole_Struct()
        {            // Not a size question: the payload has *no* bytes at all for the field below, while the image
            // has four. Whichever way the member order came out, the blit would write data no reader
            // accounts for, so the struct is written member by member instead. No [CerasSerializable] on
            // these two: the reference is the reflected member order, which is the point.
            var raw = Raw_Reflected();

            var notSerialized = new NotSerializedMember { A = 1, B = 2 };
            Assert.IsType<ReflectionFallbackFormatter<NotSerializedMember>>(raw.GetSpecificFormatter(typeof(NotSerializedMember)));
            Assert.Equal(Ints(1), raw.Serialize(notSerialized));
            Assert.Equal(new NotSerializedMember { A = 1, B = 0 }, raw.Deserialize<NotSerializedMember>(raw.Serialize(notSerialized)));

            var excluded = new ExcludedMember { A = 1, B = 2 };
            Assert.IsType<ReflectionFallbackFormatter<ExcludedMember>>(raw.GetSpecificFormatter(typeof(ExcludedMember)));
            Assert.Equal(Ints(1), raw.Serialize(excluded));
            Assert.Equal(new ExcludedMember { A = 1, B = 0 }, raw.Deserialize<ExcludedMember>(raw.Serialize(excluded)));
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────

        static byte[] Ints(params int[] values)
            => values.SelectMany(BitConverter.GetBytes).ToArray();
    }

    // ── Models ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reproducer. Blittable, no padding, and its members are the same type, so nothing but the
    /// member *name* decides the order — which is what puts One, Three, Two ahead of One, Two, Three.
    /// </summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public struct SameTypedTriple
    {
        public int One;
        public int Two;
        public int Three;
    }

    /// <summary>Same order, different width: the CLR pads this to 8 bytes and the payload is 5.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public struct PaddedPair
    {
        public byte B;
        public int I;
    }

    /// <summary>The shape that is provably identical, so the blit stays and its bytes do not change.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public struct OrderedPair
    {
        public int A;
        public int B;
    }

    /// <summary>Proves the <see cref="Guid"/> exemption reaches a struct that merely contains one.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public struct GuidAndCount
    {
        public Guid Id;
        public int Count;
    }

    /// <summary>The padded struct's twin with the padding removed, so the blit is provably identical.</summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PackedPair
    {
        public byte B;
        public int I;
    }

    /// <summary>
    /// The member set is not the field set: <c>[NonSerialized]</c> takes <c>B</c> out of the payload while
    /// <c>RespectNonSerializedAttribute</c> is on, which is the default. Deliberately not
    /// <c>[CerasSerializable]</c>: the reference is the reflected member order.
    /// </summary>
    public struct NotSerializedMember
    {
        public int A;
        [NonSerialized] public int B;
    }

    /// <summary>Same, through Ceras's own exclusion.</summary>
    public struct ExcludedMember
    {
        public int A;
        [Exclude] public int B;
    }
}
