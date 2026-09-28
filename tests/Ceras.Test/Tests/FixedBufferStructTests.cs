// A C# `fixed` buffer is the one member shape Ceras cannot describe, and the reason is a single fact
// about what the compiler emits:
//
//     public fixed byte Buffer[25];
//
// does not become a field of some 25-element type. The compiler replaces it with a field of a generated
// one-element struct (`<Buffer>e__FixedBuffer`, which holds one `byte`) and records the declared length
// in a `System.Runtime.CompilerServices.FixedBufferAttribute` on the field. The length is therefore
// nowhere in the member's *type*, and a member-by-member encoder works from the member's type.
//
// Both member-by-member paths are defeated by that, in opposite ways:
//
//   * the reflection fallback resolves the member's type, finds the one element it really has, and
//     writes it. For `unsafe struct Fb { public fixed byte Buffer[25]; public int Value; }` — 32 bytes
//     of memory image (25 buffer + 3 padding + the 4-byte int) — it wrote 5: one byte for the buffer,
//     four for the int. The save succeeded, the stream stayed aligned, and Buffer[1..24] loaded back as
//     zeros. This is the worst failure class in the project: a value written short, the load
//     succeeding, the user losing data with no error anywhere.
//   * the source generator resolves the C# declaration, sees the `byte*` the surface really has, and
//     refuses the type at compile time (CERAS035, its own diagnostic - a fixed buffer is not a `System.*`
//     framework type, it is a member Ceras cannot describe at all).
//
// So the reflection path had a competitor that was not merely different but shorter, and the only
// reason nobody saw it is that the raw path's `ReinterpretFormatter` happened to claim the type: the
// blit writes all 32 bytes and is correct. The `fixed`-buffer exemption in `IsBlittableType` rested
// exactly on that — "there is no second encoding to disagree with" — which is what this file exists to
// pin as false.
//
// What is pinned here:
//   * the measurement, so the claim is a number and not an argument (image vs written vs lost);
//   * that the type is now REFUSED, loudly and with a typed error naming the cause, on every path —
//     the reflection fallback, the blit (which no longer claims it), and a struct that merely contains
//     one, an array of one, and a tuple holding one;
//   * that the refusal agrees with CERAS035 rather than telling a different story, so changing one
//     line of config cannot turn the build error into a silent truncation;
//   * that the two escape hatches still work — `[Exclude]` (the member is not in the payload, so
//     there is nothing to describe) and a custom `IFormatter<T>` (Ceras hands the type over whole);
//   * that a struct with no `fixed` buffer in it, including one with a `byte[]` member, is completely
//     unaffected — same bytes, same formatter, still blitted where it was.
using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ceras;
using Ceras.Exceptions;
using Ceras.Formatters;
using Ceras.Helpers;
using Ceras.Test.Generator;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Ceras.Test.Tests.FixedBuffer
{
    public class FixedBufferTests : TestBase
    {
        // ── The measurement ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The reproduction, as numbers. Every figure here was read off the code before the guard
        /// existed, and the test that follows is the one that failed then.
        /// </summary>
        [Fact]
        public unsafe void The_Image_Is_32_Bytes_And_The_Old_Member_Encoding_Was_5()
        {
            // 25 buffer bytes, then 3 of padding so the int lands on a 4-byte boundary, then the int.
            Assert.Equal(32, Unsafe.SizeOf<FixedBufferStruct>());

            // What the reflection fallback resolved the member to: the compiler's one-element struct.
            // The CLR reports Size=25 on it, but the struct holds a single byte field, so a formatter
            // built from its *members* writes one byte.
            var compilerStruct = typeof(FixedBufferStruct).GetField("Buffer").FieldType;
            var itsFields = compilerStruct.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.Single(itsFields);
            Assert.Equal(typeof(byte), itsFields.Single().FieldType);

            // The declared length lives only in the attribute, which is the whole reason this is hard.
            var declared = typeof(FixedBufferStruct).GetField("Buffer")
                .GetCustomAttribute<FixedBufferAttribute>();
            Assert.NotNull(declared);
            Assert.Equal(25, declared.Length);
            Assert.Equal(typeof(byte), declared.ElementType);

            // 1 byte for the buffer + 4 for the int. 27 of the image's 32 bytes were never written.
            const int oldEncodingLength = 1 + 4;
            Assert.Equal(5, oldEncodingLength);
            Assert.Equal(32 - oldEncodingLength, 27);
        }

        // ── The refusal ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The truncation itself, as a round trip. The shape of the assertion is "lossless or refused",
        /// because that is the whole contract: there is no third outcome a save may have. Before the
        /// guard existed this test failed on the first branch — the save succeeded, the load succeeded,
        /// the stream stayed aligned, and 24 of the 25 buffer bytes came back as zeros.
        /// </summary>
        [Fact]
        public unsafe void A_Fixed_Buffer_Struct_Is_Neither_Truncated_Nor_Silently_Lost()
        {
            var value = Sample();
            var raw = Raw(useReinterpret: false);

            byte[] bytes;
            try
            {
                bytes = raw.Serialize(value);
            }
            catch (Exception e)
            {
                // Refused: the only acceptable alternative to a correct encoding.
                Assert.Contains("Buffer", RefusalIn(e).Message, StringComparison.Ordinal);
                return;
            }

            // Not refused, so the encoding has to be the whole thing. 32 bytes of image, and a payload
            // that is 5 of them is the bug: the reader stays aligned and the data is simply gone.
            Assert.Equal(32, Unsafe.SizeOf<FixedBufferStruct>());
            Assert.True(bytes.Length >= 32,
                $"the member-by-member encoding wrote {bytes.Length} bytes for a 32-byte value, so at least " +
                $"one member was written short");

            var back = raw.Deserialize<FixedBufferStruct>(bytes);
            Assert.Equal(0x11223344, back.Value);
            for (int i = 0; i < 25; i++)
                Assert.Equal((byte)(i + 1), back.Buffer[i]);   // 24 of these were 0 before the guard
        }

        [Fact]
        public unsafe void The_Reflection_Path_Refuses_It_Instead_Of_Truncating_It()
        {
            var raw = Raw_Reflected();
            var refusal = AssertRefused(() => raw.GetSpecificFormatter(typeof(FixedBufferStruct)));

            // The message has to name the cause and the numbers, or it is not actionable: a user who
            // cannot tell "Ceras does not support this" from "your buffer was 25 bytes" cannot act.
            Assert.Contains("Buffer", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("fixed", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("25", refusal.Message, StringComparison.Ordinal);   // the declared byte length
            Assert.Contains("24", refusal.Message, StringComparison.Ordinal);   // the bytes that came back zero
            Assert.Contains(FixedBufferGuard.Advice.Replace("{0}", "Ceras.Test.Tests.FixedBuffer.FixedBufferStruct"),
                refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public unsafe void The_Blit_No_Longer_Claims_It_So_There_Is_No_One_Path_That_Round_Trips()
        {
            // The requirement is that no path is lossless while another is not. Declining the blit is
            // what routes the value to the refusal instead of to the encoding that hid the bug.
            Assert.False(ReflectionHelper.IsBlittableType(typeof(FixedBufferStruct)));

            // Both raw configurations now fail identically, with the same exception type. Before the
            // change the default configuration wrote 32 bytes and the one-line change wrote 5.
            foreach (var useReinterpret in new[] { true, false })
            {
                var raw = Raw(useReinterpret);
                var refusal = AssertRefused(() => raw.GetSpecificFormatter(typeof(FixedBufferStruct)));
                Assert.Contains("fixed", refusal.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// The packed case, which is the one that shows this cannot be fixed by removing the exemption
        /// alone. <c>[StructLayout(Pack = 1)]</c> takes away the 3 padding bytes, so the image is exactly
        /// 25 + 4 = 29 and the existing width check would have found nothing wrong with it: it would
        /// still have been blitted, while the reflection path still truncated it to 5. Padding is an
        /// accident of one declaration; the shape is what is undescribable.
        /// </summary>
        [Fact]
        public void A_Packed_Fixed_Buffer_Declines_The_Blit_Too_Even_Though_Its_Width_Matches()
        {
            Assert.Equal(29, Unsafe.SizeOf<PackedFixedBufferStruct>());
            Assert.False(ReflectionHelper.IsBlittableType(typeof(PackedFixedBufferStruct)));
        }

        // ── The same story in every position the shape can appear ──────────────────────────────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public unsafe void A_Struct_That_Merely_Contains_One_Is_Refused_Naming_The_Buffer(bool useReinterpret)
        {
            // The outer struct is not itself un-describable, so the message has to name the member that
            // is: it is reached through the outer type's member list, and the inner formatter is built
            // on the way to resolving it.
            var raw = Raw(useReinterpret);
            var refusal = AssertRefused(() => raw.GetSpecificFormatter(typeof(ContainsFixedBufferStruct)));
            Assert.Contains("Buffer", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("FixedBufferStruct", refusal.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public unsafe void An_Array_Of_One_Is_Refused(bool useReinterpret)
        {
            // ReinterpretArrayFormatter is chosen by the same IsBlittableType test, so the array of a
            // fixed-buffer struct is declined too — and then the element formatter refuses.
            var raw = Raw(useReinterpret);
            AssertRefused(() => raw.GetSpecificFormatter(typeof(FixedBufferStruct[])));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public unsafe void A_Tuple_Holding_One_Is_Refused(bool useReinterpret)
        {
            AssertRefused(() => Raw(useReinterpret).GetSpecificFormatter(typeof(ValueTuple<FixedBufferStruct, int>)));
        }

        [Fact]
        public void A_Struct_Declaring_Only_A_Fixed_Buffer_Is_Refused_Naming_It()
        {
            // The single-member case, which is also the one a user is most likely to hit: a fixed
            // buffer used as a small inline blob with nothing else on the struct.
            var refusal = AssertRefused(() => Raw_Reflected().GetSpecificFormatter(typeof(OnlyAFixedBuffer)));
            Assert.Contains("Data", refusal.Message, StringComparison.Ordinal);
        }

        // ── The two paths that are still allowed ───────────────────────────────────────────────────

        [Fact]
        public unsafe void A_Fixed_Buffer_Excluded_From_The_Payload_Is_Not_Refused()
        {
            // [Exclude] is the documented way to keep a member out of a type's payload, and it works
            // here as it does everywhere else: there is nothing to describe, so nothing is refused.
            // What remains is written and read back, and the excluded buffer is simply not there.
            var value = new ExcludedFixedBuffer { Value = 7 };

            var raw = Raw_Reflected();
            var formatter = raw.GetSpecificFormatter(typeof(ExcludedFixedBuffer));
            Assert.IsType<ReflectionFallbackFormatter<ExcludedFixedBuffer>>(formatter);

            var bytes = raw.Serialize(value);
            Assert.Equal(BitConverter.GetBytes(7), bytes);   // the int, and nothing else
            Assert.Equal(7, raw.Deserialize<ExcludedFixedBuffer>(bytes).Value);
        }

        [Fact]
        public unsafe void A_Custom_Formatter_For_The_Type_Is_Still_An_Escape_Hatch()
        {
            // The advice in the refusal message has to be true. A custom formatter for the declaring
            // type wins before any resolver runs, so the reflection fallback is never constructed and
            // the buffer is never looked at — which also means a struct merely *containing* such a
            // type can be written, because the member resolves to that formatter.
            CountingFixedBufferFormatter.Calls = 0;

            var raw = Raw_Reflected();
            raw.Config.ConfigType<FixedBufferStruct>().CustomFormatter = new CountingFixedBufferFormatter();

            Assert.IsType<CountingFixedBufferFormatter>(raw.GetSpecificFormatter(typeof(FixedBufferStruct)));

            var holder = new ContainsFixedBufferStruct { Inner = new FixedBufferStruct(), X = 3 };
            var bytes = raw.Serialize(holder);
            Assert.Equal(1, CountingFixedBufferFormatter.Calls);   // written once
            Assert.Equal(2 + 4, bytes.Length);   // the custom 2-byte payload, then the int
            Assert.Equal(new byte[] { 0xAA, 0xBB, 3, 0, 0, 0 }, bytes);

            // Read back: the custom formatter runs for the member and the int still lands in the right
            // place, which is the part that would break if the custom formatter had not been honoured
            // and the reflection fallback had been built for the type after all.
            var back = raw.Deserialize<ContainsFixedBufferStruct>(bytes);
            Assert.Equal(2, CountingFixedBufferFormatter.Calls);   // read once
            Assert.Equal(3, back.X);
        }

        // ── The two paths that must not change ─────────────────────────────────────────────────────

        [Fact]
        public unsafe void A_Struct_With_No_Fixed_Buffer_Is_Completely_Unaffected()
        {
            // The byte[] twin of the reproducer: the advice the refusal gives ("store the buffer as a
            // byte[] member") is only credible if that actually works. All 25 bytes are written, where
            // the fixed buffer lost 24 of its 25, and the rest of the type behaves exactly as before.
            var raw = Raw_Reflected();
            var value = new ByteArrayTwin
            {
                Buffer = Enumerable.Range(1, 25).Select(i => (byte)i).ToArray(),
                Value = 0x11223344,
            };

            Assert.IsType<ReflectionFallbackFormatter<ByteArrayTwin>>(
                raw.GetSpecificFormatter(typeof(ByteArrayTwin)));

            var bytes = raw.Serialize(value);
            // 0x02 the reference marker for the non-null array, 0x1a the biased varint count
            // (25 + 1), then all 25 elements, then the int.
            Assert.Equal(2 + 25 + 4, bytes.Length);
            Assert.Equal(0x02, bytes[0]);
            Assert.Equal(26, bytes[1]);                       // WriteUInt32Bias(25, 1)
            Assert.Equal(Enumerable.Range(1, 25).Select(i => (byte)i), bytes.Skip(2).Take(25));
            Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, bytes.Skip(27).Take(4));

            var back = raw.Deserialize<ByteArrayTwin>(bytes);
            Assert.Equal(value.Buffer, back.Buffer);
            Assert.Equal(value.Value, back.Value);
        }

        [Fact]
        public void A_Blittable_Struct_Is_Still_Blitted_With_Unchanged_Bytes()
        {
            // The gate: a type that never had a fixed buffer must not have lost the fast path, and its
            // bytes must be the memory image exactly as before.
            var value = new BlittablePair { A = 1, B = 2 };

            var raw = Raw(useReinterpret: true);
            Assert.IsType<ReinterpretFormatter<BlittablePair>>(raw.GetSpecificFormatter(typeof(BlittablePair)));

            var image = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();
            var bytes = raw.Serialize(value);

            Assert.Equal(image, bytes);
            Assert.Equal(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }, bytes);
            Assert.Equal(value, raw.Deserialize<BlittablePair>(bytes));
        }

        [Fact]
        public void The_Non_Fixed_Buffer_Blit_Exemptions_Are_Untouched()
        {
            // Guid and the other documented exemptions are a different question and must keep their
            // answers; only the fixed-buffer exemption is gone.
            Assert.True(ReflectionHelper.IsBlittableType(typeof(Guid)));
            Assert.True(ReflectionHelper.IsBlittableType(typeof(ContainsGuid)));
            Assert.False(ReflectionHelper.IsBlittableType(typeof(decimal)));
            Assert.False(ReflectionHelper.IsBlittableType(typeof(PaddedPair)));
        }

        // ── The generator tells the same story ─────────────────────────────────────────────────────

        /// <summary>
        /// The other half of the requirement: a user who marks the type <c>[CerasSerializable]</c> gets a
        /// build error, and a user who does not must not be able to reach a silent truncation instead.
        /// Both now say the same thing — Ceras cannot write a fixed buffer, and here is what to do —
        /// so the two are pinned together. What is pinned here is the *invariant* the fix rests on: the
        /// compile-time error is a refusal of the same shape, and the runtime agrees it is one. The
        /// agreement of the two texts themselves is pinned in FixedBufferDiagnosticTests, which is the only
        /// place that can see both the generator's message and the runtime's.
        /// </summary>
        [Fact]
        public void The_Generator_Refuses_It_At_Compile_Time_With_CERAS035()
        {
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests"")]
    [CerasSerializable]
    public unsafe struct Root
    {
        public fixed byte Buffer[25];
        public int Value;
    }
}";
            var diagnostic = Assert.Single(GeneratorTestHarness.Diagnostics(source, allowUnsafe: true)
                .Where(x => x.Id == "CERAS035"));
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

            // The member and the declared length are both named, so the build error is as specific as the
            // runtime refusal: 'Buffer', the element type, the element count and the byte count it works
            // out to. What it must not say is the old explanation - that 'byte*' is a System.* framework
            // type the user could have declared or formatted. A fixed buffer is neither, which is why it
            // has its own diagnostic instead of CERAS029, and one shape gets one diagnostic.
            string message = diagnostic.GetMessage();
            Assert.Contains("Buffer", message, StringComparison.Ordinal);
            Assert.Contains("fixed byte[25]", message, StringComparison.Ordinal);
            Assert.Contains("25 bytes", message, StringComparison.Ordinal);
            Assert.DoesNotContain("CERAS029", GeneratorTestHarness.DiagnosticIds(source, allowUnsafe: true));
        }

        [Fact]
        public void A_Type_With_A_Fixed_Buffer_Cannot_Be_Declared_At_All()
        {
            // Same, at the declaration site rather than the member: even a root that is nothing but a
            // fixed buffer is a build error, so there is no declaration that reaches the runtime. The
            // assertion is on the refusal, not on a particular id - see
            // The_Generator_Refuses_It_At_Compile_Time_With_CERAS035 for why the id is CERAS035.
            const string source = @"
using Ceras;
namespace T
{
    [CerasContext(""Tests2"")]
    [CerasSerializable]
    public unsafe struct Root { public fixed byte Data[4]; }
}";
            Assert.Contains("CERAS035", GeneratorTestHarness.DiagnosticIds(source, allowUnsafe: true));
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The raw path, with the fast path on or off — the config line that used to decide
        /// whether a fixed buffer round-tripped or lost 27 of its 32 bytes.</summary>
        static CerasFastSerializer Raw(bool useReinterpret)
            => new CerasFastSerializer(c =>
            {
                c.AllowReflectionFallback = true;
                ((CerasRawConfig)c).UseReinterpretFormatter = useReinterpret;
            });

        static CerasFastSerializer Raw_Reflected() => Raw(useReinterpret: false);

        /// <summary>25 distinguishable buffer bytes and a recognisable int, so a lost byte is visible.</summary>
        static unsafe FixedBufferStruct Sample()
        {
            var value = new FixedBufferStruct { Value = 0x11223344 };
            for (int i = 0; i < 25; i++)
                value.Buffer[i] = (byte)(i + 1);
            return value;
        }

        /// <summary>
        /// The refusal, as the caller sees it. <c>ReflectionFallbackFormatter</c> is constructed through
        /// <see cref="System.Reflection"/>, which wraps whatever its constructor threw — the same
        /// wrapping <c>TypeConfig.VerifyNoBannedMemberShapes</c>'s <c>BannedTypeException</c> gets, and
        /// the same reason those tests look for the diagnosis in the whole chain. The diagnosis the user
        /// acts on is the innermost one either way.
        /// </summary>
        static CerasSchemaException AssertRefused(Action action)
            => RefusalIn(Assert.ThrowsAny<Exception>(action));

        /// <summary>The <see cref="CerasSchemaException"/> somewhere in <paramref name="thrown"/>'s chain.</summary>
        static CerasSchemaException RefusalIn(Exception thrown)
        {
            for (var current = thrown; current != null; current = current.InnerException)
                if (current is CerasSchemaException refusal)
                    return refusal;

            Assert.Fail($"Expected a CerasSchemaException, but got: {thrown}");
            return null;
        }
    }

    // ── Models ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The reproducer. 32 bytes of image; the reflection path used to write 5.</summary>
    public unsafe struct FixedBufferStruct
    {
        public fixed byte Buffer[25];
        public int Value;
    }

    /// <summary>The same shape packed, so the image is 29 bytes and no padding hides the problem.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct PackedFixedBufferStruct
    {
        public fixed byte Buffer[25];
        public int Value;
    }

    /// <summary>Nothing but a buffer — the shape a user writes for a small inline blob.</summary>
    public unsafe struct OnlyAFixedBuffer
    {
        public fixed byte Data[16];
    }

    public unsafe struct ContainsFixedBufferStruct
    {
        public FixedBufferStruct Inner;
        public int X;
    }

    /// <summary>The escape hatch by attribute: the buffer is not in the payload, so it is not a
    /// member Ceras has to describe.</summary>
    public unsafe struct ExcludedFixedBuffer
    {
        [Exclude]
        public fixed byte Data[16];

        public int Value;
    }

    /// <summary>The advice the refusal gives, as a model: the same 25 bytes Ceras does understand.</summary>
    public struct ByteArrayTwin
    {
        public byte[] Buffer;
        public int Value;
    }

    public struct BlittablePair
    {
        public int A;
        public int B;
    }

    public struct PaddedPair
    {
        public byte B;
        public int I;
    }

    public struct ContainsGuid
    {
        public Guid Id;
        public int Count;
    }

    /// <summary>Writes two bytes and nothing else, so a test can tell it ran.</summary>
    public sealed class CountingFixedBufferFormatter : IFormatter<FixedBufferStruct>
    {
        public static int Calls;

        public void Serialize(IBufferWriter<byte> writer, FixedBufferStruct value)
        {
            Calls++;
            var span = writer.GetSpan(2);
            span[0] = 0xAA;
            span[1] = 0xBB;
            writer.Advance(2);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref FixedBufferStruct value)
        {
            Calls++;
            Span<byte> skip = stackalloc byte[2];
            reader.TryCopyTo(skip);
            reader.Advance(2);
            value = default;
        }

        public void SerializeUntyped(IBufferWriter<byte> writer, object value)
            => Serialize(writer, (FixedBufferStruct)value);

        public void DeserializeUntyped(ref SequenceReader<byte> reader, object container, MemberInfo member)
        {
            Calls++;
            Span<byte> skip = stackalloc byte[2];
            reader.TryCopyTo(skip);
            reader.Advance(2);
            if (member is FieldInfo field)
                field.SetValue(container, default(FixedBufferStruct));
            else
                ((PropertyInfo)member).SetValue(container, default(FixedBufferStruct));
        }
    }
}
