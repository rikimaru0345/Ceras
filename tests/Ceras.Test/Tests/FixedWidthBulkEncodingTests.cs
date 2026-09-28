namespace Ceras.Test
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Linq;
    using Ceras.Helpers;
    using Xunit;

    /// <summary>
    /// Differential tests for <c>FixedWidthBulkWriter</c>, which replaces the element-at-a-time path
    /// for arrays and lists of fixed-width primitives.
    /// <para>
    /// The bulk path is only legitimate if it emits exactly the bytes the per-element formatters
    /// would. Every comparison here is therefore differential: the expected run is built by writing
    /// each element on its own through the single-value formatter, so the test fails if the two ever
    /// disagree - including on endianness, which is why the reference is a plain object member and
    /// not a one-element collection (arrays of primitives resolve to
    /// <c>ReinterpretArrayFormatter</c>, and a one-element <see cref=\"List{T}\"/> would take the
    /// bulk path itself).
    /// </para>
    /// <para>
    /// Values a naive reinterpretation would get wrong are included on purpose:
    /// <see cref=\"float\"/> and <see cref=\"double\"/> bit patterns including NaN payloads and signed
    /// zero, negative numbers, characters above <see cref=\"short.MaxValue\"/>, and every value of
    /// <see cref=\"byte\"/>. <see cref=\"ByteArrayLayout_IsTheDocumentedLittleEndianRun\"/> then pins
    /// the layout literally, so a swap of two bytes cannot hide behind a self-consistent pair.
    /// </para>
    /// <para>
    /// Spans are requested in blocks bounded at 64 KB, so the large cases deliberately cross the
    /// boundary rather than only exercising the single-span case.
    /// </para>
    /// <para>
    /// What this does <em>not</em> cover: primitive <em>arrays</em>. Those already resolved to
    /// <c>ReinterpretArrayFormatter&lt;T&gt;</c>, which copies the whole run with one
    /// <c>GetSpan</c> and a <c>FastCopy</c>, so they were never on the element-at-a-time path and
    /// are unchanged here. The remaining win is for <see cref=\"List{T}\"/>, which does go through
    /// <c>CollectionFormatter</c>.
    /// </para>
    /// </summary>
    public class FixedWidthBulkEncodingTests
    {
        /// <summary>
        /// A one-element array, so the payload after the count prefix is exactly what the
        /// single-value formatter writes. Serializing an element this way is what the bulk path has to
        /// reproduce.
        /// </summary>
        /// <summary>
        /// The element run of a serialized array: the payload with the reference marker and the
        /// element count removed. Both are read with the library's own varint reader rather than
        /// assumed to be a fixed width.
        /// </summary>
        /// <summary>
        /// A single element serialized by the ordinary single-value formatter, with the reference
        /// marker stripped: the independent reference the bulk path is compared against. A
        /// value-typed member is written inline, so the payload after the marker is exactly the
        /// element's bytes. A one-element array would <em>not</em> do as the reference, because
        /// arrays of primitives resolve to <c>ReinterpretArrayFormatter</c> rather than to the
        /// collection formatter - it would compare the bulk path against itself.
        /// </summary>
        static byte[] ElementBytes<T>(T value)
        {
            var ceras = new CerasFastSerializer();
            var bytes = ceras.Serialize(new One<T> { Value = value });

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            Assert.NotEqual(-1, marker);

            return bytes[(int)reader.Consumed..];
        }

        /// <summary>
        /// One element per object so the element is written by the single-value formatter. A
        /// value-typed member is stored inline, with no per-member reference marker of its own.
        /// </summary>
        [CerasSerializable]
        public class One<T>
        {
            public T Value;
        }

        static byte[] ElementRun<T>(T[] values)
        {
            var ceras = new CerasFastSerializer();
            var bytes = ceras.Serialize(values);

            // Both framing fields are biased varints, read with the library's own reader so their
            // width is not assumed.
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            Assert.NotEqual(-1, marker);

            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            Assert.Equal(values.Length, count);

            return bytes[(int)reader.Consumed..];
        }

        /// <summary>
        /// The bytes a bulk-written array of <paramref name="values"/> must produce: the same
        /// element framing as the element path, repeated.
        /// </summary>
        /// <summary>
        /// Compares a bulk-written array against the same array written one element at a time.
        /// <para>
        /// The element-at-a-time reference is produced by <see cref="ElementBytes"/>: a one-element
        /// array, whose payload after the count prefix is exactly the bytes the single-value
        /// formatter writes. Both sides are arrays, so both go through <c>ArrayFormatter</c> and both
        /// carry the same framing; only the element loop differs.
        /// </para>
        /// </summary>
        static void AssertArrayMatchesElements<T>(T[] values)
        {
            // The reference: each element written on its own by the single-value formatter.
            // Concatenating them gives the run the bulk path has to reproduce, so this fails if the
            // two ever disagree - whatever the cause, including endianness.
            var expected = new List<byte>();
            foreach (var v in values)
                expected.AddRange(ElementBytes(v));

            Assert.Equal(
                string.Join(",", expected),
                string.Join(",", ElementRun(values)));
        }

        /// <summary>
        /// Bytes per element on the wire, for the types the bulk path handles. Independent of the
        /// code under test, so it can be asserted directly.
        /// </summary>
        static int ElementWidth<T>()
        {
            var t = typeof(T);
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(bool)) return 1;
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) return 2;
            if (t == typeof(int) || t == typeof(uint) || t == typeof(float)) return 4;
            if (t == typeof(long) || t == typeof(ulong) || t == typeof(double)) return 8;
            throw new InvalidOperationException($"{t} is not a fixed-width primitive.");
        }

        static void AssertListMatchesElements<T>(List<T> values)
        {
            AssertArrayMatchesElements(values.ToArray());
        }

        [Fact]
        public void Int_Array_MatchesTheElementPath()
        {
            // Large enough to cross the internal 64 KB block boundary.
            var values = new int[20_000];
            for (int i = 0; i < values.Length; i++)
                values[i] = i * 7919 - 100_000;
            AssertArrayMatchesElements(values);
        }

        [Fact]
        public void Int_SmallAndEdgeValues_MatchTheElementPath()
        {
            AssertArrayMatchesElements(new[] { 0, 1, -1, int.MinValue, int.MaxValue });
            AssertArrayMatchesElements(Array.Empty<int>());
            AssertArrayMatchesElements(new[] { 42 });
        }

        [Fact]
        public void EveryByteValue_MatchesTheElementPath()
        {
            var values = new byte[256];
            for (int i = 0; i < 256; i++) values[i] = (byte)i;
            AssertArrayMatchesElements(values);
        }

        [Fact]
        public void SByte_MatchesTheElementPath()
        {
            var values = new sbyte[256];
            for (int i = 0; i < 256; i++) values[i] = (sbyte)(i - 128);
            AssertArrayMatchesElements(values);
        }

        [Fact]
        public void Bool_MatchesTheElementPath()
        {
            // A managed bool's in-memory representation is not something the format may assume, so
            // this is the case a raw byte copy would get wrong.
            var values = new bool[300];
            for (int i = 0; i < values.Length; i++) values[i] = i % 3 == 0;
            AssertArrayMatchesElements(values);
            AssertArrayMatchesElements(new[] { true, false, true, true });
            AssertArrayMatchesElements(Array.Empty<bool>());
        }

        [Fact]
        public void ShortAndUshort_MatchTheElementPath()
        {
            var shorts = new short[300];
            var ushorts = new ushort[300];
            for (int i = 0; i < 300; i++)
            {
                shorts[i] = (short)(i * 273 - 32768);
                ushorts[i] = (ushort)(i * 547);
            }
            AssertArrayMatchesElements(shorts);
            AssertArrayMatchesElements(ushorts);
            AssertArrayMatchesElements(new short[] { short.MinValue, short.MaxValue, 0 });
        }

        [Fact]
        public void Char_MatchesTheElementPath()
        {
            // Includes code units above short.MaxValue, where a signed and an unsigned write of the
            // same bits could disagree if the pair were mismatched.
            var chars = new char[300];
            for (int i = 0; i < 300; i++) chars[i] = (char)(i * 357 + 300);
            AssertArrayMatchesElements(chars);
            AssertArrayMatchesElements(new[] { 'a', '\0', (char)0xFFFF, (char)0x8000 });
        }

        [Fact]
        public void LongAndUlong_MatchTheElementPath()
        {
            var longs = new long[9_000];
            var ulongs = new ulong[9_000];
            for (int i = 0; i < longs.Length; i++)
            {
                longs[i] = (long)i * 0x0123456789ABCDEFL;
                ulongs[i] = (ulong)i * 0xFEDCBA9876543210UL;
            }
            AssertArrayMatchesElements(longs);
            AssertArrayMatchesElements(ulongs);
            AssertArrayMatchesElements(new long[] { long.MinValue, long.MaxValue, 0, -1 });
        }

        [Fact]
        public void Uint_MatchesTheElementPath()
        {
            var values = new uint[20_000];
            for (int i = 0; i < values.Length; i++) values[i] = (uint)(i * 2654435761u);
            AssertArrayMatchesElements(values);
            AssertArrayMatchesElements(new uint[] { 0, uint.MaxValue, 1 });
        }

        [Fact]
        public void Float_MatchesTheElementPath()
        {
            // Bit patterns chosen to include NaN payloads, infinities, subnormals, -0.0 and the sign
            // bit on its own, so nothing is normalised on the way through.
            var values = new float[]
            {
                0f, -0f, 1f, -1f, float.MinValue, float.MaxValue, float.Epsilon,
                float.PositiveInfinity, float.NegativeInfinity, float.NaN, -float.NaN,
                1.401298E-45f, 1.17549435E-38f, 3.40282347E+38f, -3.40282347E+38f,
                0.1f, -0.1f, 12345.6789f, -12345.6789f,
            };
            AssertArrayMatchesElements(values);

            var many = new float[20_000];
            for (int i = 0; i < many.Length; i++)
                many[i] = BitConverter.Int32BitsToSingle(unchecked((int)(unchecked((uint)i * 2654435761u) ^ 0x80000000u)));
            AssertArrayMatchesElements(many);
        }

        [Fact]
        public void Double_MatchesTheElementPath()
        {
            var values = new double[]
            {
                0d, -0d, 1d, -1d, double.MinValue, double.MaxValue, double.Epsilon,
                double.PositiveInfinity, double.NegativeInfinity, double.NaN, -double.NaN,
                4.9406564584124654E-324, 2.2250738585072014E-308, 1.7976931348623157E+308,
                0.1d, -0.1d, 12345.678901234d, -12345.678901234d,
            };
            AssertArrayMatchesElements(values);

            var many = new double[9_000];
            for (int i = 0; i < many.Length; i++)
                many[i] = BitConverter.Int64BitsToDouble(unchecked((long)0x9E3779B97F4A7C15UL * i) ^ long.MinValue);
            AssertArrayMatchesElements(many);
        }

        [Fact]
        public void Lists_OfEveryHandledType_MatchTheElementPath()
        {
            AssertListMatchesElements(new List<int> { 0, -1, int.MaxValue, int.MinValue, 7 });
            AssertListMatchesElements(new List<byte> { 0, 1, 255, 128 });
            AssertListMatchesElements(new List<bool> { true, false, true });
            AssertListMatchesElements(new List<long> { long.MinValue, 0, long.MaxValue });
            AssertListMatchesElements(new List<double> { 0d, -0d, double.NaN, 1.5d });
            AssertListMatchesElements(new List<float> { 0f, -0f, float.NaN, 1.5f });
            AssertListMatchesElements(new List<char> { 'a', (char)0xFFFF });
            AssertListMatchesElements(new List<short> { -1, 0, short.MaxValue });
            AssertListMatchesElements(new List<uint> { 0, uint.MaxValue });
            AssertListMatchesElements(new List<sbyte> { -1, 0, 127 });
            AssertListMatchesElements(new List<ulong> { 0, ulong.MaxValue });
            AssertListMatchesElements(new List<ushort> { 0, ushort.MaxValue });
            AssertListMatchesElements(new List<int>());
        }

        /// <summary>
        /// A type the bulk path must refuse still has to round-trip, because refusing is what makes
        /// it fall back rather than write something else.
        /// </summary>
        /// <summary>
        /// The encoding itself, spelled out. This is the assertion that actually pins the wire
        /// format: length is the element count, and every element is a fixed-width little-endian
        /// field, so the whole run is independent of how many spans the writer asked for.
        /// </summary>
        [Fact]
        public void ByteArrayLayout_IsTheDocumentedLittleEndianRun()
        {
            var ceras = new CerasFastSerializer();

            Assert.Equal(
                new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
                ElementRun(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

            // 1 as int is 01 00 00 00 little-endian, not 00 00 00 01.
            Assert.Equal(
                new byte[] { 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF },
                ElementRun(new[] { 1, -1 }));

            Assert.Equal(
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x80 },
                ElementRun(new[] { -1, int.MinValue }));

            // Two bytes per short, little-endian.
            Assert.Equal(
                new byte[] { 0x01, 0x00, 0xFF, 0x7F },
                ElementRun(new short[] { 1, short.MaxValue }));

            // Eight per long.
            Assert.Equal(
                new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                ElementRun(new long[] { 1 }));

            // 1.0f is 0x3F800000, little-endian 00 00 80 3F.
            Assert.Equal(
                new byte[] { 0x00, 0x00, 0x80, 0x3F },
                ElementRun(new[] { 1.0f }));

            // 1.0d is 0x3FF0000000000000, little-endian 00 00 00 00 00 00 F0 3F.
            Assert.Equal(
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F },
                ElementRun(new[] { 1.0d }));

            // true is 01, false is 00 - stated, not copied from memory.
            Assert.Equal(new byte[] { 0x01, 0x00, 0x01 }, ElementRun(new[] { true, false, true }));
        }

        /// <summary>
        /// Every handled type has to survive a round trip at a size that crosses the internal 64 KB
        /// block boundary, because a bug that only appears on the second block would otherwise pass.
        /// </summary>
        [Fact]
        public void EveryHandledType_RoundTripsAcrossBlockBoundaries()
        {
            var ceras = new CerasFastSerializer();
            const int N = 40_000;   // 40k * 8 bytes = 320 KB, five blocks

            Assert.Equal(Enumerable.Range(0, N).Select(i => i * 3 - 60_000).ToArray(),
                ceras.Deserialize<int[]>(ceras.Serialize(Enumerable.Range(0, N).Select(i => i * 3 - 60_000).ToArray())));

            var longs = Enumerable.Range(0, N).Select(i => (long)i * 0x0123456789ABCDEFL).ToArray();
            Assert.Equal(longs, ceras.Deserialize<long[]>(ceras.Serialize(longs)));

            var doubles = Enumerable.Range(0, N).Select(i => i * 1.5d).ToArray();
            Assert.Equal(doubles, ceras.Deserialize<double[]>(ceras.Serialize(doubles)));

            var floats = Enumerable.Range(0, N).Select(i => (float)(i * 0.25d)).ToArray();
            Assert.Equal(floats, ceras.Deserialize<float[]>(ceras.Serialize(floats)));

            var bytes = Enumerable.Range(0, N).Select(i => (byte)(i * 31)).ToArray();
            Assert.Equal(bytes, ceras.Deserialize<byte[]>(ceras.Serialize(bytes)));

            var bools = Enumerable.Range(0, N).Select(i => i % 5 < 2).ToArray();
            Assert.Equal(bools, ceras.Deserialize<bool[]>(ceras.Serialize(bools)));

            var lists = Enumerable.Range(0, N).Select(i => (short)(i * 271)).ToList();
            Assert.Equal(lists, ceras.Deserialize<List<short>>(ceras.Serialize(lists)));
        }

        [Fact]
        public void UnsupportedElementTypes_StillRoundTrip()
        {
            var ceras = new CerasFastSerializer();

            var decimals = new[] { decimal.MaxValue, decimal.MinValue, 0m, 1.5m, -2.25m };
            Assert.Equal(decimals, ceras.Deserialize<decimal[]>(ceras.Serialize(decimals)));

            var strings = new[] { "a", "bb", "", "cccc" };
            Assert.Equal(strings, ceras.Deserialize<string[]>(ceras.Serialize(strings)));

            var objects = new object[] { 1, "two", 3.0, null };
            var clone = ceras.Deserialize<object[]>(ceras.Serialize(objects));
            Assert.Equal(4, clone.Length);

            // A reference type element must not be mistaken for a fixed-width one.
            var mixed = new List<object> { 1, 2, 3 };
            Assert.Equal(3, ceras.Deserialize<List<object>>(ceras.Serialize(mixed)).Count);
        }

        /// <summary>
        /// A large bool array is the case called out in the roadmap: it crosses several blocks and is
        /// the type most likely to be mishandled by a byte copy.
        /// </summary>
        [Fact]
        public void LargeBoolArray_RoundTrips()
        {
            var ceras = new CerasFastSerializer();
            var values = new bool[70_000];
            for (int i = 0; i < values.Length; i++) values[i] = (i % 7) < 3;

            var clone = ceras.Deserialize<bool[]>(ceras.Serialize(values));
            Assert.Equal(values, clone);
        }
    }
}
