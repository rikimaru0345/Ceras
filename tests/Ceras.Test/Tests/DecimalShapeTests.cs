using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.BuiltinShapes
{
    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsDecimalHolder
    {
        public decimal D;
        public int After;
    }

    /// <summary>
    /// TODO 0.5 (folded "Review later"): decimal is a fully supported fixed-width scalar. Well-known id
    /// 13, 16 bytes, the four <see cref="decimal.GetBits(decimal)"/> words (lo, mid, hi, flags) each
    /// little-endian. Covered as root and member on both the matching fast path and the migration
    /// interpreter, with pinned bytes and boundary/precision values.
    /// </summary>
    public class DecimalShapeTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        static byte[] SlowPath(byte[] bytes)
        {
            var copy = (byte[])bytes.Clone();
            copy[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(copy);
            return copy;
        }

        static byte[] Payload<T>(T value)
        {
            var s = New();
            var bytes = s.Save(value, 1);
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            int off = (int)dataOffset;
            SchemaSection.ReadLeb128(bytes, ref off); // skip the root type id
            return bytes[off..(int)stringOffset];
        }

        static readonly decimal[] Cases =
        {
            0m, 1m, -1m, 42.5m,
            decimal.MinValue, decimal.MaxValue,
            decimal.One, decimal.MinusOne, decimal.Zero,
            0.0000000000000000000000000001m,   // smallest precision
            79228162514264337593543950335m,      // MaxValue as integer
            -79228162514264337593543950335m,
            123456789.987654321m,
        };

        [Fact]
        public void Decimal_Root_RoundTrips_Both_Paths()
        {
            var s = New();
            foreach (var value in Cases)
            {
                var bytes = s.Save(value, 1);
                Assert.Equal(value, s.Load<decimal>(bytes, 1));
                Assert.Equal(value, s.Load<decimal>(SlowPath(bytes), 1));
            }
        }

        [Fact]
        public void Decimal_Member_RoundTrips_Both_Paths_With_Following_Field()
        {
            var s = New();
            foreach (var value in Cases)
            {
                var bytes = s.Save(new BsDecimalHolder { D = value, After = 321 }, 1);
                var fast = s.Load<BsDecimalHolder>(bytes, 1);
                var slow = s.Load<BsDecimalHolder>(SlowPath(bytes), 1);
                Assert.Equal(value, fast.D);
                Assert.Equal(321, fast.After);
                Assert.Equal(value, slow.D);
                Assert.Equal(321, slow.After);
            }
        }

        [Fact]
        public void Decimal_Root_Payload_Bytes_Are_Pinned_GetBits()
        {
            Assert.Equal(ToGetBitsBytes(0m), Payload(0m));
            Assert.Equal(ToGetBitsBytes(1m), Payload(1m));
            Assert.Equal(ToGetBitsBytes(-1m), Payload(-1m));
            Assert.Equal(ToGetBitsBytes(42.5m), Payload(42.5m));
            Assert.Equal(ToGetBitsBytes(decimal.MinValue), Payload(decimal.MinValue));
            Assert.Equal(ToGetBitsBytes(decimal.MaxValue), Payload(decimal.MaxValue));

            // Explicit pin for 1m: lo=1, mid=0, hi=0, flags=0.
            Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Payload(1m));

            // 42.5 = 425 * 10^-1 → scale 1 in the flags word, so flags == 0x00010000.
            var bits = decimal.GetBits(42.5m);
            Assert.Equal(425, bits[0]);
            Assert.Equal(0x00010000, bits[3]);
        }

        [Fact]
        public void Decimal_Payload_Is_16_Bytes()
            => Assert.Equal(16, Payload(42.5m).Length);

        [Fact]
        public void Decimal_Registered_As_WellKnown_Scalar()
        {
            Assert.True(WellKnownTypes.TryGetId(typeof(decimal), out int id));
            Assert.Equal(WellKnownTypes.Decimal, id);
            Assert.Equal(16, WellKnownTypes.GetFixedSize(WellKnownTypes.Decimal));
        }

        static byte[] ToGetBitsBytes(decimal value)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            var result = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0), bits[0]);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), bits[1]);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), bits[2]);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), bits[3]);
            return result;
        }
    }
}
